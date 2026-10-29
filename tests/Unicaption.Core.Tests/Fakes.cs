using System.Net;
using System.Text;
using System.Threading.Channels;
using Unicaption.Core.Api;
using Unicaption.Core.Audio;
using Unicaption.Core.Realtime;
using Unicaption.Core.Session;
using Unicaption.Core.Time;

namespace Unicaption.Core.Tests;

internal sealed class FakeClock : IMonotonicClock
{
    public double NowMs { get; set; } = 1_000_000;
    public void Advance(double ms) => NowMs += ms;
}

internal sealed class FakeTokens : IAccessTokenProvider
{
    public int ForcedRefreshes;
    public string? Token = "id-token";

    public Task<string?> GetTokenAsync(bool forceRefresh, CancellationToken ct)
    {
        if (forceRefresh) ForcedRefreshes++;
        return Task.FromResult(Token);
    }
}

internal sealed class FakeUsage : IUsageReporter
{
    public readonly List<UsageReport> Reports = [];

    public Task ReportAsync(UsageReport report, CancellationToken ct)
    {
        Reports.Add(report);
        return Task.CompletedTask;
    }
}

internal sealed class FakeAudio : IAudioEngine
{
    public AudioCaptureException? AccessError;
    public int Starts;
    public int Stops;
    public PcmSink? Sink;

    public event EventHandler<AudioCaptureException>? Failed;

    public Task EnsureAccessAsync(AudioSourceKind source, CancellationToken ct) =>
        AccessError is null ? Task.CompletedTask : Task.FromException(AccessError);

    public Task StartAsync(AudioSourceKind source, PcmSink sink, CancellationToken ct)
    {
        Starts++;
        Sink = sink;
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        Stops++;
        return Task.CompletedTask;
    }

    public void Fail(AudioErrorKind kind) => Failed?.Invoke(this, new AudioCaptureException(kind, "x"));
}

internal sealed class FakeWs : IWsConnection
{
    private readonly Channel<string?> _incoming = Channel.CreateUnbounded<string?>();
    public bool Authenticate = true;

    /// <summary>What the server sends after "auth". Default is the deployed backend's real reply.</summary>
    public string AuthReply = "{\"status\": \"authenticated\", \"plan\": \"ultra\"}";
    public readonly List<string> SentText = [];
    public long BinaryBytes;
    public bool Closed;

    public Task ConnectAsync(Uri uri, CancellationToken ct) => Task.CompletedTask;

    public Task SendTextAsync(string text, CancellationToken ct)
    {
        lock (SentText) SentText.Add(text);
        if (Authenticate && text.Contains("\"auth\"")) Push(AuthReply);
        return Task.CompletedTask;
    }

    public Task SendBinaryAsync(ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        Interlocked.Add(ref BinaryBytes, data.Length);
        return Task.CompletedTask;
    }

    public async Task<string?> ReceiveTextAsync(CancellationToken ct) => await _incoming.Reader.ReadAsync(ct);

    public void Push(string? message) => _incoming.Writer.TryWrite(message);

    public Task CloseAsync(CancellationToken ct)
    {
        Closed = true;
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    public readonly List<HttpRequestMessage> Requests = [];

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        lock (Requests) Requests.Add(request);
        return Task.FromResult(respond(request));
    }

    public static HttpResponseMessage Json(string body, HttpStatusCode code = HttpStatusCode.OK) =>
        new(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
}
