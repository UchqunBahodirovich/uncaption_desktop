using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using Unicaption.Core.Api;
using Unicaption.Core.Audio;
using Unicaption.Core.Diagnostics;

namespace Unicaption.Core.Realtime;

/// <summary>Thin transport seam so the protocol can be tested without a server.</summary>
public interface IWsConnection : IAsyncDisposable
{
    Task ConnectAsync(Uri uri, CancellationToken ct);
    Task SendTextAsync(string text, CancellationToken ct);
    Task SendBinaryAsync(ReadOnlyMemory<byte> data, CancellationToken ct);
    /// <summary>Returns the next complete text message, or null when the socket closed.</summary>
    Task<string?> ReceiveTextAsync(CancellationToken ct);
    Task CloseAsync(CancellationToken ct);
}

public sealed class ClientWsConnection : IWsConnection
{
    private readonly ClientWebSocket _ws = new();

    public ClientWsConnection() => _ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);

    public Task ConnectAsync(Uri uri, CancellationToken ct) => _ws.ConnectAsync(uri, ct);

    public Task SendTextAsync(string text, CancellationToken ct) =>
        _ws.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, ct);

    public Task SendBinaryAsync(ReadOnlyMemory<byte> data, CancellationToken ct) =>
        _ws.SendAsync(data, WebSocketMessageType.Binary, true, ct).AsTask();

    public async Task<string?> ReceiveTextAsync(CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        using var ms = new MemoryStream();
        while (true)
        {
            var r = await _ws.ReceiveAsync(buffer, ct).ConfigureAwait(false);
            if (r.MessageType == WebSocketMessageType.Close) return null;
            ms.Write(buffer, 0, r.Count);
            if (r.EndOfMessage)
            {
                if (r.MessageType == WebSocketMessageType.Text) return Encoding.UTF8.GetString(ms.ToArray());
                ms.SetLength(0); // binary from server is not part of the protocol; ignore
            }
        }
    }

    public async Task CloseAsync(CancellationToken ct)
    {
        if (_ws.State is WebSocketState.Open or WebSocketState.CloseReceived)
            await _ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "stop", ct).ConfigureAwait(false);
    }

    public ValueTask DisposeAsync()
    {
        _ws.Dispose();
        return ValueTask.CompletedTask;
    }
}

public enum RealtimeCloseReason { Normal, ServerClosed, Error, AuthTimeout, AuthRejected, ServiceError }

public sealed class RealtimeClosedEventArgs(RealtimeCloseReason reason, Exception? error) : EventArgs
{
    public RealtimeCloseReason Reason { get; } = reason;
    public Exception? Error { get; } = error;
}

public sealed class RealtimeException(RealtimeCloseReason reason, string message, Exception? inner = null)
    : Exception(message, inner)
{
    public RealtimeCloseReason Reason { get; } = reason;
}

/// <summary>
/// WSS protocol from section 10: auth → authenticated (10 s limit) → config → binary audio,
/// ping every 30 s, token batches out. One instance = one session. Close/error/timeout are
/// surfaced once through <see cref="Closed"/> so the SessionController can end Live (RT 03).
/// </summary>
public sealed class RealtimeClient : IAsyncDisposable
{
    public static readonly TimeSpan AuthTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan PingInterval = TimeSpan.FromSeconds(30);
    private const int MaxConfigResends = 3;

    private readonly IWsConnection _ws;
    private readonly IDiagnostics _diag;
    private readonly CancellationTokenSource _life = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly TaskCompletionSource _authenticated = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private RealtimeConfig? _config;
    private Task? _receiveLoop;
    private Task? _pingLoop;
    private Task? _audioLoop;
    private int _configResends;
    private int _closedRaised;
    private long _lastTokenAtTicks;
    private volatile bool _stopping;

    private readonly TimeSpan _authTimeout;

    public RealtimeClient(IWsConnection ws, IDiagnostics diag, TimeSpan? authTimeout = null)
    {
        _ws = ws;
        _diag = diag;
        _authTimeout = authTimeout ?? AuthTimeout;
    }

    public event EventHandler<IReadOnlyList<SttToken>>? TokensReceived;
    public event EventHandler<RealtimeClosedEventArgs>? Closed;

    public long BytesSent { get; private set; }

    /// <summary>Connects, authenticates and sends config. Throws <see cref="RealtimeException"/> on failure.</summary>
    public async Task ConnectAsync(Uri uri, string idToken, RealtimeConfig config, CancellationToken ct)
    {
        _config = config;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _life.Token);
        linked.CancelAfter(_authTimeout);
        try
        {
            await _ws.ConnectAsync(uri, linked.Token).ConfigureAwait(false);
            _receiveLoop = Task.Run(ReceiveLoopAsync);
            await SendTextAsync(RealtimeConfig.AuthJson(idToken), linked.Token).ConfigureAwait(false);
            await _authenticated.Task.WaitAsync(linked.Token).ConfigureAwait(false);
            await SendTextAsync(config.ToJson(), linked.Token).ConfigureAwait(false);
            _pingLoop = Task.Run(PingLoopAsync);
            _diag.Info("ws.authenticated");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new RealtimeException(RealtimeCloseReason.AuthTimeout, "The server did not respond in time.");
        }
        catch (RealtimeException)
        {
            throw;
        }
        catch (Exception e) when (e is WebSocketException or HttpRequestException or IOException)
        {
            throw new RealtimeException(RealtimeCloseReason.Error, "Could not connect to the transcription service.", e);
        }
    }

    /// <summary>Starts forwarding audio from the queue as binary frames.</summary>
    public void StartAudio(BoundedAudioQueue queue) => _audioLoop = Task.Run(() => AudioLoopAsync(queue));

    /// <summary>
    /// Graceful finish: flushes queued audio, waits for trailing tokens (bounded),
    /// then closes. Never raises <see cref="Closed"/>.
    /// </summary>
    public async Task FinishAsync(TimeSpan drainTimeout)
    {
        _stopping = true;
        try
        {
            if (_audioLoop is not null)
                await _audioLoop.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
        }
        catch (TimeoutException) { }

        var deadline = DateTime.UtcNow + drainTimeout;
        var quiet = TimeSpan.FromMilliseconds(500);
        while (DateTime.UtcNow < deadline && _receiveLoop is { IsCompleted: false })
        {
            var sinceToken = TimeSpan.FromTicks(DateTime.UtcNow.Ticks - Interlocked.Read(ref _lastTokenAtTicks));
            if (sinceToken > quiet) break;
            await Task.Delay(100).ConfigureAwait(false);
        }

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await _ws.CloseAsync(cts.Token).ConfigureAwait(false);
        }
        catch (Exception e) when (e is WebSocketException or OperationCanceledException or IOException)
        {
            // Already closed by the server; nothing to clean up.
        }
    }

    public async ValueTask DisposeAsync()
    {
        _stopping = true;
        _life.Cancel();
        foreach (var t in new[] { _receiveLoop, _pingLoop, _audioLoop })
        {
            if (t is null) continue;
            try { await t.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
            catch (Exception) { /* loops end on cancellation; failures already reported */ }
        }
        await _ws.DisposeAsync().ConfigureAwait(false);
        _life.Dispose();
    }

    private async Task ReceiveLoopAsync()
    {
        try
        {
            while (!_life.IsCancellationRequested)
            {
                var raw = await _ws.ReceiveTextAsync(_life.Token).ConfigureAwait(false);
                if (raw is null)
                {
                    _authenticated.TrySetException(new RealtimeException(RealtimeCloseReason.AuthRejected,
                        "The transcription service closed the connection."));
                    RaiseClosed(RealtimeCloseReason.ServerClosed, null);
                    return;
                }
                HandleMessage(raw);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception e)
        {
            _authenticated.TrySetException(new RealtimeException(RealtimeCloseReason.Error, "Connection lost.", e));
            RaiseClosed(RealtimeCloseReason.Error, e);
        }
    }

    internal void HandleMessage(string raw)
    {
        var msg = RealtimeConfig.TryParseMessage(raw);
        if (msg is null)
        {
            _diag.Warn("ws.malformed_message", ("length", raw.Length.ToString()));
            return;
        }
        // The deployed backend answers auth with {"status":"authenticated","plan":…} and rejects with
        // {"error":"…"} (no "type"); "type" is still accepted for the documented RT 01 shape.
        var kind = RealtimeConfig.TypeOf(msg)
                   ?? JsonNormalizer.OptionalString(msg, "status")
                   ?? (msg.ContainsKey("error") || msg.ContainsKey("error_code") ? "error" : null);
        switch (kind)
        {
            case "authenticated":
                _authenticated.TrySetResult();
                _configResends = 0;
                return;
            case "pong":
                return;
            case "soniox_disconnected":
                if (_configResends++ < MaxConfigResends && _config is not null)
                {
                    _diag.Warn("ws.upstream_disconnected", ("attempt", _configResends.ToString()));
                    _ = Task.Delay(1000, _life.Token).ContinueWith(
                        _ => SendTextAsync(_config.ToJson(), _life.Token), TaskContinuationOptions.OnlyOnRanToCompletion);
                }
                else
                {
                    RaiseClosed(RealtimeCloseReason.ServerClosed,
                        new RealtimeException(RealtimeCloseReason.ServerClosed, "The transcription engine disconnected."));
                }
                return;
            case "error":
                if (_authenticated.TrySetException(new RealtimeException(RealtimeCloseReason.AuthRejected,
                        "The transcription service rejected the session."))) return;
                // After auth the errors come from Soniox ({"error_code":400,"error_message":"Invalid
                // language hint."}); keep its short message so the user learns what to change.
                var detail = JsonNormalizer.OptionalString(msg, "error_message", "error", "message");
                _diag.Warn("ws.service_error", ("code", JsonNormalizer.OptionalString(msg, "error_code") ?? ""));
                RaiseClosed(RealtimeCloseReason.ServiceError,
                    new RealtimeException(RealtimeCloseReason.ServiceError, detail ?? "The transcription service reported an error."));
                return;
        }

        if (msg["tokens"] is JsonArray tokens)
        {
            var batch = TokenParser.Parse(tokens);
            if (batch.Count == 0) return;
            Interlocked.Exchange(ref _lastTokenAtTicks, DateTime.UtcNow.Ticks);
            TokensReceived?.Invoke(this, batch);
        }
    }

    private async Task PingLoopAsync()
    {
        try
        {
            using var timer = new PeriodicTimer(PingInterval);
            while (await timer.WaitForNextTickAsync(_life.Token).ConfigureAwait(false))
                await SendTextAsync("{\"type\":\"ping\"}", _life.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (Exception e)
        {
            RaiseClosed(RealtimeCloseReason.Error, e);
        }
    }

    private async Task AudioLoopAsync(BoundedAudioQueue queue)
    {
        try
        {
            while (true)
            {
                var chunk = await queue.ReadAsync(_life.Token).ConfigureAwait(false);
                if (chunk is null) return; // queue completed by Stop
                await _sendLock.WaitAsync(_life.Token).ConfigureAwait(false);
                try
                {
                    await _ws.SendBinaryAsync(chunk, _life.Token).ConfigureAwait(false);
                    BytesSent += chunk.Length;
                }
                finally
                {
                    _sendLock.Release();
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception e)
        {
            RaiseClosed(RealtimeCloseReason.Error, e);
        }
    }

    private async Task SendTextAsync(string text, CancellationToken ct)
    {
        await _sendLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _ws.SendTextAsync(text, ct).ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private void RaiseClosed(RealtimeCloseReason reason, Exception? error)
    {
        if (_stopping || Interlocked.Exchange(ref _closedRaised, 1) == 1) return;
        _diag.Warn("ws.closed", ("reason", reason.ToString()));
        Closed?.Invoke(this, new RealtimeClosedEventArgs(reason, error));
    }
}
