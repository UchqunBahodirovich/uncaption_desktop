using Unicaption.Core.Api;
using Unicaption.Core.Audio;
using Unicaption.Core.Auth;
using Unicaption.Core.Diagnostics;
using Unicaption.Core.Realtime;
using Unicaption.Core.Time;
using Unicaption.Core.Transcript;

namespace Unicaption.Core.Session;

public enum SessionState
{
    Idle,
    RequestingPermission,
    Connecting,
    StartingCapture,
    Live,
    Stopping,
    Error,
}

public enum SessionErrorKind
{
    PermissionDenied,
    NoDevice,
    DeviceLost,
    AuthExpired,
    NetworkOffline,
    ConnectionLost,
    LimitReached,
    Overload,
    Suspended,
    Unknown,
}

public sealed record SessionError(SessionErrorKind Kind, string Message);

public sealed record SessionOptions(AudioSourceKind Source, RealtimeConfig Config);

public interface IUsageReporter
{
    Task ReportAsync(UsageReport report, CancellationToken ct);
}

/// <summary>
/// SESSION 01/02: the only owner of capture, socket and clock. Guarantees at most one
/// start in flight and one live session; late callbacks from an older session are
/// ignored by session id. Public methods must be called on the UI thread; events are
/// raised on the UI thread through <c>post</c>.
/// </summary>
public sealed class SessionController
{
    private static readonly TimeSpan DrainTimeout = TimeSpan.FromMilliseconds(1500);

    private readonly IAudioEngine _audio;
    private readonly Func<IWsConnection> _wsFactory;
    private readonly IAccessTokenProvider _tokens;
    private readonly IUsageReporter _usage;
    private readonly Uri _wsUri;
    private readonly IDiagnostics _diag;
    private readonly Action<Action> _post;

    private int _sessionId;
    private RealtimeClient? _rt;
    private BoundedAudioQueue? _queue;
    private CancellationTokenSource? _startCts;
    private RealtimeClosedEventArgs? _closedBeforeLive; // upstream may reject the config while capture starts

    public SessionController(
        IAudioEngine audio,
        Func<IWsConnection> wsFactory,
        IAccessTokenProvider tokens,
        IUsageReporter usage,
        Uri wsUri,
        TranscriptStore transcript,
        IMonotonicClock monotonic,
        IDiagnostics diag,
        Action<Action> post)
    {
        _audio = audio;
        _wsFactory = wsFactory;
        _tokens = tokens;
        _usage = usage;
        _wsUri = wsUri;
        Transcript = transcript;
        Clock = new SessionClock(monotonic);
        _diag = diag;
        _post = post;
        _audio.Failed += OnAudioFailed;
    }

    public SessionState State { get; private set; } = SessionState.Idle;
    public SessionError? LastError { get; private set; }
    public SessionClock Clock { get; }
    public TranscriptStore Transcript { get; }

    /// <summary>Returns a reason to block Start (limits), or null. Server still enforces (SEC 02).</summary>
    public Func<SessionError?>? EntitlementCheck { get; set; }

    /// <summary>Server auth acknowledgement limit (RT 01); overridable for tests.</summary>
    internal TimeSpan AuthTimeout { get; set; } = RealtimeClient.AuthTimeout;

    public bool IsBusy => State is not (SessionState.Idle or SessionState.Error);
    public bool IsLive => State == SessionState.Live;

    public event EventHandler? StateChanged;
    public event EventHandler<(bool Source, bool Translation)>? TranscriptChanged;
    /// <summary>Raised after a session ended and its usage was submitted (account refresh trigger).</summary>
    public event EventHandler<UsageReport>? SessionEnded;

    public async Task StartAsync(SessionOptions options)
    {
        if (IsBusy) return; // AC 03: repeated clicks never start a second session
        var id = ++_sessionId;
        _startCts = new CancellationTokenSource();
        var ct = _startCts.Token;
        LastError = null;
        _closedBeforeLive = null;
        SetState(SessionState.RequestingPermission);
        _diag.Info("session.start", ("id", id.ToString()), ("source", options.Source.ToString()));

        RealtimeClient? rt = null;
        BoundedAudioQueue? queue = null;
        var captureStarted = false;
        try
        {
            if (EntitlementCheck?.Invoke() is { } blocked) throw new SessionFailure(blocked);

            var token = await _tokens.GetTokenAsync(false, ct);
            if (token is null) throw new SessionFailure(new(SessionErrorKind.AuthExpired, "Please sign in again."));

            await _audio.EnsureAccessAsync(options.Source, ct);
            if (id != _sessionId) return;

            SetState(SessionState.Connecting);
            rt = new RealtimeClient(_wsFactory(), _diag, AuthTimeout);
            rt.TokensReceived += (_, batch) => _post(() => OnTokens(id, batch));
            rt.Closed += (_, e) => _post(() => OnRealtimeClosed(id, e));
            await rt.ConnectAsync(_wsUri, token, options.Config, ct);
            if (id != _sessionId) return;

            SetState(SessionState.StartingCapture);
            queue = new BoundedAudioQueue();
            var q = queue;
            var overflowReported = 0;
            await _audio.StartAsync(options.Source, pcm =>
            {
                if (!q.TryWrite(pcm) && Interlocked.Exchange(ref overflowReported, 1) == 0)
                    _post(() => OnOverflow(id));
            }, ct);
            captureStarted = true;
            if (id != _sessionId) return;

            rt.StartAudio(queue);
            _rt = rt;
            _queue = queue;
            Transcript.ResetForNewSession(); // previous text is kept until a successful start
            TranscriptChanged?.Invoke(this, (true, true));
            Clock.Start();
            SetState(SessionState.Live);
            _diag.Info("session.live", ("id", id.ToString()));
            if (_closedBeforeLive is { } early) OnRealtimeClosed(id, early);
        }
        catch (Exception e)
        {
            var cancelled = e is OperationCanceledException && ct.IsCancellationRequested;
            var error = Classify(e);
            _diag.Error("session.start_failed", e, ("id", id.ToString()), ("kind", error.Kind.ToString()));
            queue?.Complete();
            if (captureStarted) await SafeAsync(_audio.StopAsync);
            if (rt is not null) await rt.DisposeAsync();
            if (id == _sessionId)
            {
                if (cancelled) SetState(SessionState.Idle);
                else Fail(error);
            }
        }
        finally
        {
            _startCts?.Dispose();
            _startCts = null;
        }
    }

    /// <summary>User Stop. Repeated calls are ignored (AC 13).</summary>
    public Task StopAsync() => StopCoreAsync(_sessionId, null);

    /// <summary>OS suspend (TIME 02): stop before sleep so sleep time is never billed.</summary>
    public Task OnSystemSuspendAsync() =>
        StopCoreAsync(_sessionId, new(SessionErrorKind.Suspended, "Session stopped because the computer went to sleep."));

    /// <summary>Logout / app exit: cancels a pending start and stops a live session.</summary>
    public async Task ShutdownAsync()
    {
        _startCts?.Cancel();
        if (State == SessionState.Live) await StopAsync();
    }

    /// <summary>Clears the error banner and returns to Idle.</summary>
    public void DismissError()
    {
        if (State != SessionState.Error) return;
        LastError = null;
        SetState(SessionState.Idle);
    }

    private async Task StopCoreAsync(int id, SessionError? reason)
    {
        if (id != _sessionId || State != SessionState.Live) return;
        SetState(SessionState.Stopping);

        var elapsed = Clock.Stop(); // snapshot first: cleanup time is not billed
        var report = new UsageReport((int)Math.Floor(elapsed.TotalSeconds), Transcript.SessionWordCount);
        _diag.Info("session.stop", ("id", id.ToString()), ("seconds", report.Seconds.ToString()),
            ("reason", reason?.Kind.ToString() ?? "user"));

        var rt = _rt;
        var queue = _queue;
        _rt = null;
        _queue = null;

        await SafeAsync(_audio.StopAsync);
        queue?.Complete();
        if (rt is not null)
        {
            await SafeAsync(() => rt.FinishAsync(DrainTimeout));
            await rt.DisposeAsync();
        }

        if (report.Seconds > 0)
        {
            try
            {
                await _usage.ReportAsync(report, CancellationToken.None);
            }
            catch (Exception e)
            {
                // No automatic resend without an idempotency contract (SEC 02).
                _diag.Error("usage.report_failed", e, ("seconds", report.Seconds.ToString()));
            }
        }

        if (reason is null)
        {
            SetState(SessionState.Idle);
        }
        else
        {
            Fail(reason);
        }
        SessionEnded?.Invoke(this, report);
    }

    private void OnTokens(int id, IReadOnlyList<SttToken> batch)
    {
        if (id != _sessionId || State is not (SessionState.Live or SessionState.Stopping)) return;
        var changed = Transcript.Apply(batch);
        if (changed.Source || changed.Translation) TranscriptChanged?.Invoke(this, changed);
    }

    private void OnRealtimeClosed(int id, RealtimeClosedEventArgs e)
    {
        if (id != _sessionId) return;
        if (State is SessionState.Connecting or SessionState.StartingCapture)
        {
            _closedBeforeLive = e;
            return;
        }
        if (State != SessionState.Live) return;
        var error = e.Error is RealtimeException { Reason: RealtimeCloseReason.ServiceError } service
            ? new SessionError(SessionErrorKind.Unknown, $"The transcription service stopped the session: {service.Message}")
            : new SessionError(SessionErrorKind.ConnectionLost, "Connection to the transcription service was lost.");
        _ = StopCoreAsync(id, error);
    }

    private void OnOverflow(int id)
    {
        if (id != _sessionId || State != SessionState.Live) return;
        _ = StopCoreAsync(id, new(SessionErrorKind.Overload,
            "Audio could not be sent fast enough. Check your connection and start again."));
    }

    private void OnAudioFailed(object? sender, AudioCaptureException e)
    {
        var id = _sessionId;
        _post(() =>
        {
            if (id != _sessionId || State != SessionState.Live) return;
            _ = StopCoreAsync(id, MapAudio(e));
        });
    }

    private void Fail(SessionError error)
    {
        LastError = error;
        SetState(SessionState.Error);
    }

    private void SetState(SessionState s)
    {
        if (State == s) return;
        State = s;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private async Task SafeAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception e)
        {
            _diag.Error("session.cleanup_failed", e);
        }
    }

    internal static SessionError Classify(Exception e) => e switch
    {
        SessionFailure f => f.Error,
        AudioCaptureException a => MapAudio(a),
        ReauthenticationRequiredException => new(SessionErrorKind.AuthExpired, "Your session has expired. Please sign in again."),
        ApiException { StatusCode: 401 or 403 } => new(SessionErrorKind.AuthExpired, "Your session has expired. Please sign in again."),
        RealtimeException { Reason: RealtimeCloseReason.AuthTimeout } =>
            new(SessionErrorKind.NetworkOffline, "Could not connect: the server did not respond in time."),
        RealtimeException { Reason: RealtimeCloseReason.AuthRejected } =>
            new(SessionErrorKind.AuthExpired, "The server rejected this session. Please sign in again."),
        RealtimeException or HttpRequestException =>
            new(SessionErrorKind.NetworkOffline, "Could not connect. Check your internet connection and retry."),
        _ => new(SessionErrorKind.Unknown, "Could not start transcription. Please try again."),
    };

    private static SessionError MapAudio(AudioCaptureException a) => a.Kind switch
    {
        AudioErrorKind.PermissionDenied => new(SessionErrorKind.PermissionDenied,
            "Microphone access is blocked. Allow desktop apps to use the microphone in Windows Settings."),
        AudioErrorKind.NoDevice => new(SessionErrorKind.NoDevice, "No audio device found. Connect a device and retry."),
        AudioErrorKind.DeviceLost or AudioErrorKind.DefaultDeviceChanged =>
            new(SessionErrorKind.DeviceLost, "The audio device was disconnected or changed. Start again to continue."),
        AudioErrorKind.QueueOverflow => new(SessionErrorKind.Overload, "Audio could not be sent fast enough."),
        _ => new(SessionErrorKind.Unknown, "Audio capture failed. Please try again."),
    };

    internal sealed class SessionFailure(SessionError error) : Exception(error.Message)
    {
        public SessionError Error { get; } = error;
    }
}
