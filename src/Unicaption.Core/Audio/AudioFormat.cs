namespace Unicaption.Core.Audio;

/// <summary>Wire format sent to the backend (AUDIO 02): raw PCM16 LE, 16 kHz, mono, no header.</summary>
public static class AudioFormat
{
    public const int SampleRate = 16_000;
    public const int Channels = 1;
    public const int BytesPerSample = 2;
    public const int BytesPerSecond = SampleRate * Channels * BytesPerSample; // 32 000
}

/// <summary>Receives 16 kHz mono PCM16 LE. Runs on an audio thread: must not block.</summary>
public delegate void PcmSink(ReadOnlySpan<byte> pcm);

public enum AudioSourceKind
{
    Mic = 0,
    System = 1,
    Both = 2,
}

public enum AudioErrorKind
{
    PermissionDenied,
    NoDevice,
    DeviceLost,
    DefaultDeviceChanged,
    QueueOverflow,
    NativeError,
}

public sealed class AudioCaptureException(AudioErrorKind kind, string message, Exception? inner = null)
    : Exception(message, inner)
{
    public AudioErrorKind Kind { get; } = kind;
}

/// <summary>
/// Platform capture engine (Windows: WASAPI capture + shared-mode loopback).
/// Delivers 16 kHz mono PCM16 to <paramref name="sink"/> on an audio thread; the
/// sink must not block, do I/O or touch the UI.
/// </summary>
public interface IAudioEngine
{
    /// <summary>
    /// Verifies the OS allows access to the devices needed for <paramref name="source"/>
    /// (Windows privacy settings) and that they exist. Throws <see cref="AudioCaptureException"/>.
    /// </summary>
    Task EnsureAccessAsync(AudioSourceKind source, CancellationToken ct);

    /// <summary>Starts capture and returns only after the first device buffer has actually been received.</summary>
    Task StartAsync(AudioSourceKind source, PcmSink sink, CancellationToken ct);

    Task StopAsync();

    /// <summary>Raised from any thread when capture fails after it started (device lost, etc).</summary>
    event EventHandler<AudioCaptureException>? Failed;
}
