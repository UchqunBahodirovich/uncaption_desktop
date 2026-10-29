using Unicaption.Core.Audio;
using Unicaption.Core.Diagnostics;
using Unicaption.Core.Realtime;
using Unicaption.Core.Session;
using Unicaption.Core.Transcript;
using Xunit;

namespace Unicaption.Core.Tests;

public class SessionControllerTests
{
    private sealed class Rig
    {
        public readonly FakeAudio Audio = new();
        public readonly FakeUsage Usage = new();
        public readonly FakeTokens Tokens = new();
        public readonly FakeClock Clock = new();
        public readonly List<FakeWs> Sockets = [];
        public bool Authenticate = true;
        public string? AuthReply;
        public readonly SessionController Controller;
        public static readonly SessionOptions Options = new(AudioSourceKind.Mic, new RealtimeConfig("stt-rt-v3", "en", "ru"));

        public Rig()
        {
            Controller = new SessionController(Audio, () =>
            {
                var ws = new FakeWs { Authenticate = Authenticate };
                if (AuthReply is not null) ws.AuthReply = AuthReply;
                lock (Sockets) Sockets.Add(ws);
                return ws;
            }, Tokens, Usage, new Uri("wss://example.test/ws"), new TranscriptStore(Clock), Clock,
                NullDiagnostics.Instance, a => a());
        }
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++) await Task.Delay(10);
        Assert.True(condition());
    }

    [Fact] // AC 03
    public async Task TwentyClicks_CreateOneSession()
    {
        var rig = new Rig();
        var starts = Enumerable.Range(0, 20).Select(_ => rig.Controller.StartAsync(Rig.Options)).ToArray();
        await Task.WhenAll(starts);
        Assert.Equal(SessionState.Live, rig.Controller.State);
        Assert.Single(rig.Sockets);
        Assert.Equal(1, rig.Audio.Starts);
        Assert.True(rig.Controller.Clock.IsRunning);
    }

    [Fact] // AC 04
    public async Task PermissionDenied_NoFakeLive_RetryWorks()
    {
        var rig = new Rig { Audio = { AccessError = new AudioCaptureException(AudioErrorKind.PermissionDenied, "denied") } };
        await rig.Controller.StartAsync(Rig.Options);
        Assert.Equal(SessionState.Error, rig.Controller.State);
        Assert.Equal(SessionErrorKind.PermissionDenied, rig.Controller.LastError!.Kind);
        Assert.Empty(rig.Sockets);
        Assert.False(rig.Controller.Clock.IsRunning);

        rig.Audio.AccessError = null;
        await rig.Controller.StartAsync(Rig.Options);
        Assert.Equal(SessionState.Live, rig.Controller.State);
    }

    [Fact] // AC 09
    public async Task AuthTimeout_CleansUp_NextStartPossible()
    {
        var rig = new Rig { Authenticate = false };
        rig.Controller.AuthTimeout = TimeSpan.FromMilliseconds(150);
        await rig.Controller.StartAsync(Rig.Options);
        Assert.Equal(SessionState.Error, rig.Controller.State);
        Assert.Equal(SessionErrorKind.NetworkOffline, rig.Controller.LastError!.Kind);
        Assert.Equal(0, rig.Audio.Starts);

        rig.Authenticate = true;
        await rig.Controller.StartAsync(Rig.Options);
        Assert.Equal(SessionState.Live, rig.Controller.State);
    }

    [Theory] // backend replies with "status"; the documented RT 01 shape uses "type"
    [InlineData("{\"status\": \"authenticated\", \"plan\": \"ultra\"}")]
    [InlineData("{\"type\":\"authenticated\"}")]
    public async Task AuthenticatedReply_BothShapes_GoLive(string reply)
    {
        var rig = new Rig { AuthReply = reply };
        await rig.Controller.StartAsync(Rig.Options);
        Assert.Equal(SessionState.Live, rig.Controller.State);
    }

    [Fact] // rejection is {"error": "..."} without "type": fail at once, not after the auth timeout
    public async Task AuthRejected_ErrorShape_FailsFast()
    {
        var rig = new Rig { AuthReply = "{\"error\": \"Authentication failed\"}" };
        rig.Controller.AuthTimeout = TimeSpan.FromSeconds(30);
        var started = DateTime.UtcNow;
        await rig.Controller.StartAsync(Rig.Options);
        Assert.Equal(SessionState.Error, rig.Controller.State);
        Assert.Equal(SessionErrorKind.AuthExpired, rig.Controller.LastError!.Kind);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(5));
    }

    [Fact] // AC 13 + TIME 01
    public async Task RepeatedStop_OneCleanup_OneUsage_SnapshotSeconds()
    {
        var rig = new Rig();
        await rig.Controller.StartAsync(Rig.Options);
        rig.Clock.Advance(65_400);
        await Task.WhenAll(rig.Controller.StopAsync(), rig.Controller.StopAsync(), rig.Controller.StopAsync());
        Assert.Equal(SessionState.Idle, rig.Controller.State);
        Assert.Equal(1, rig.Audio.Stops);
        Assert.Single(rig.Usage.Reports);
        Assert.Equal(65, rig.Usage.Reports[0].Seconds);
        Assert.True(rig.Sockets[0].Closed);
    }

    [Fact] // AC 20
    public async Task TextKeptAfterStop_ClearedOnNextStart()
    {
        var rig = new Rig();
        await rig.Controller.StartAsync(Rig.Options);
        rig.Sockets[0].Push("""{"tokens":[{"text":"Hello","speaker":1},{"text":"Привет","translation_status":"translation"}]}""");
        await WaitUntil(() => rig.Controller.Transcript.Source.Segments.Count == 1);
        rig.Clock.Advance(1_000);
        await rig.Controller.StopAsync();
        Assert.Single(rig.Controller.Transcript.Source.Segments);
        Assert.Single(rig.Controller.Transcript.Translation.Segments);
        Assert.Equal(1, rig.Usage.Reports[0].WordsCount);

        await rig.Controller.StartAsync(Rig.Options);
        Assert.Empty(rig.Controller.Transcript.Source.Segments);
        Assert.Empty(rig.Controller.Transcript.Translation.Segments);
    }

    [Fact] // AC 32 / RT 03
    public async Task NetworkDrop_EndsLive_UsageSentOnce()
    {
        var rig = new Rig();
        await rig.Controller.StartAsync(Rig.Options);
        rig.Clock.Advance(3_000);
        rig.Sockets[0].Push(null); // server closed
        await WaitUntil(() => rig.Controller.State == SessionState.Error);
        Assert.Equal(SessionErrorKind.ConnectionLost, rig.Controller.LastError!.Kind);
        await rig.Controller.StopAsync();
        Assert.Single(rig.Usage.Reports);
        Assert.Equal(3, rig.Usage.Reports[0].Seconds);
    }

    [Fact] // AC 08
    public async Task DeviceLost_EndsSessionWithManagedError()
    {
        var rig = new Rig();
        await rig.Controller.StartAsync(Rig.Options);
        rig.Audio.Fail(AudioErrorKind.DeviceLost);
        await WaitUntil(() => rig.Controller.State == SessionState.Error);
        Assert.Equal(SessionErrorKind.DeviceLost, rig.Controller.LastError!.Kind);
        Assert.Equal(1, rig.Audio.Stops);
    }

    [Fact] // AC 31 / TIME 02
    public async Task Suspend_StopsBeforeSleep()
    {
        var rig = new Rig();
        await rig.Controller.StartAsync(Rig.Options);
        rig.Clock.Advance(10_000);
        await rig.Controller.OnSystemSuspendAsync();
        rig.Clock.Advance(3_600_000); // sleeping
        Assert.Equal(10, rig.Usage.Reports.Single().Seconds);
        Assert.Equal(SessionErrorKind.Suspended, rig.Controller.LastError!.Kind);
    }

    [Fact] // AUDIO 04: overflow stops the session instead of growing memory
    public async Task QueueOverflow_StopsSession()
    {
        var rig = new Rig();
        await rig.Controller.StartAsync(Rig.Options);
        var big = new byte[AudioFormat.BytesPerSecond * 3];
        rig.Audio.Sink!(big);
        await WaitUntil(() => rig.Controller.State == SessionState.Error);
        Assert.Equal(SessionErrorKind.Overload, rig.Controller.LastError!.Kind);
    }

    [Fact]
    public async Task Entitlement_BlocksStart()
    {
        var rig = new Rig();
        rig.Controller.EntitlementCheck = () => new SessionError(SessionErrorKind.LimitReached, "limit");
        await rig.Controller.StartAsync(Rig.Options);
        Assert.Equal(SessionErrorKind.LimitReached, rig.Controller.LastError!.Kind);
        Assert.Empty(rig.Sockets);
    }

    [Fact]
    public async Task AudioIsForwardedAsBinaryFrames()
    {
        var rig = new Rig();
        await rig.Controller.StartAsync(Rig.Options);
        rig.Audio.Sink!(new byte[3200]);
        await WaitUntil(() => Interlocked.Read(ref rig.Sockets[0].BinaryBytes) == 3200);
        Assert.Contains(rig.Sockets[0].SentText, t => t.Contains("\"config\""));
    }
}
