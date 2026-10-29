using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wave;
using Unicaption.Core.Audio;
using Unicaption.Core.Diagnostics;
using Unicaption.Core.Time;

namespace Unicaption.App.Platform;

/// <summary>
/// AUDIO 01–04 on Windows: WASAPI shared-mode capture for the default microphone and
/// WASAPI shared-mode loopback for the default output device. Each source is converted
/// to float, downmixed and resampled to 16 kHz on its own capture thread, then mixed on a
/// single monotonic timeline by <see cref="TimelineMixer"/>. A dedicated pump thread
/// drives the timeline so silent loopback periods become real silence, not missing time.
/// No HTTP, disk or UI work happens on these threads.
/// </summary>
public sealed class WasapiAudioEngine : IAudioEngine
{
    private const int E_ACCESSDENIED = unchecked((int)0x80070005);
    private const int E_NOTFOUND = unchecked((int)0x80070490);
    private const int AUDCLNT_E_DEVICE_INVALIDATED = unchecked((int)0x88890004);
    private static readonly TimeSpan FirstBufferTimeout = TimeSpan.FromSeconds(3);

    private readonly IDiagnostics _diag;
    private readonly object _gate = new();
    private readonly List<SourceCapture> _sources = [];
    private MMDeviceEnumerator? _enumerator;
    private DeviceWatcher? _watcher;
    private TimelineMixer? _mixer;
    private Thread? _pump;
    private volatile bool _running;
    private int _failedRaised;

    public WasapiAudioEngine(IDiagnostics diag) => _diag = diag;

    public event EventHandler<AudioCaptureException>? Failed;

    public Task EnsureAccessAsync(AudioSourceKind source, CancellationToken ct) => Task.Run(() =>
    {
        using var en = new MMDeviceEnumerator();
        if (source is AudioSourceKind.Mic or AudioSourceKind.Both)
        {
            if (!en.HasDefaultAudioEndpoint(DataFlow.Capture, Role.Console))
                throw new AudioCaptureException(AudioErrorKind.NoDevice, "No microphone found.");
            using var device = en.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console);
            try
            {
                using var client = device.AudioClient;
                client.Initialize(AudioClientShareMode.Shared, AudioClientStreamFlags.None, 200_000, 0, client.MixFormat, Guid.Empty);
            }
            catch (COMException e) when (e.HResult == E_ACCESSDENIED)
            {
                throw new AudioCaptureException(AudioErrorKind.PermissionDenied, "Microphone access denied.", e);
            }
            catch (COMException e)
            {
                throw new AudioCaptureException(AudioErrorKind.NativeError, "Microphone could not be opened.", e);
            }
        }
        if (source is AudioSourceKind.System or AudioSourceKind.Both &&
            !en.HasDefaultAudioEndpoint(DataFlow.Render, Role.Console))
            throw new AudioCaptureException(AudioErrorKind.NoDevice, "No output device found for system audio.");
    }, ct);

    public async Task StartAsync(AudioSourceKind source, PcmSink sink, CancellationToken ct)
    {
        await StopAsync().ConfigureAwait(false);
        Interlocked.Exchange(ref _failedRaised, 0);

        var waitForData = new List<Task>();
        try
        {
            await Task.Run(() =>
            {
                lock (_gate)
                {
                    _enumerator = new MMDeviceEnumerator();
                    var flows = new List<DataFlow>();
                    if (source is AudioSourceKind.Mic or AudioSourceKind.Both) flows.Add(DataFlow.Capture);
                    if (source is AudioSourceKind.System or AudioSourceKind.Both) flows.Add(DataFlow.Render);

                    _mixer = new TimelineMixer(flows.Count, sink);
                    for (var i = 0; i < flows.Count; i++)
                    {
                        var flow = flows[i];
                        MMDevice device;
                        try
                        {
                            device = _enumerator.GetDefaultAudioEndpoint(flow, Role.Console);
                        }
                        catch (COMException e) when (e.HResult == E_NOTFOUND)
                        {
                            throw new AudioCaptureException(AudioErrorKind.NoDevice, "Audio device not found.", e);
                        }
                        var capture = new SourceCapture(i, flow, device, _mixer, OnSourceFailed);
                        _sources.Add(capture);
                        // Loopback is silent (no packets) while nothing plays, so only the mic must prove data flow.
                        if (flow == DataFlow.Capture) waitForData.Add(capture.FirstData);
                    }

                    _watcher = new DeviceWatcher(_sources.Select(s => (s.Flow, s.DeviceId)).ToList(), OnSourceFailed);
                    _enumerator.RegisterEndpointNotificationCallback(_watcher);

                    _running = true;
                    _mixer.Start(SystemMonotonicClock.Instance.NowMs);
                    foreach (var s in _sources) s.Start();
                    _pump = new Thread(PumpLoop) { IsBackground = true, Name = "Unicaption audio pump", Priority = ThreadPriority.AboveNormal };
                    _pump.Start();
                }
            }, ct).ConfigureAwait(false);

            // AUDIO 04: Live only after capture is proven to deliver buffers.
            await Task.WhenAll(waitForData).WaitAsync(FirstBufferTimeout, ct).ConfigureAwait(false);
            await Task.Delay(150, ct).ConfigureAwait(false); // surface immediate loopback init failures
            if (Volatile.Read(ref _failedRaised) == 1)
                throw new AudioCaptureException(AudioErrorKind.NativeError, "Audio capture stopped unexpectedly.");
            _diag.Info("audio.started", ("source", source.ToString()),
                ("formats", string.Join(";", _sources.Select(s => s.FormatDescription))));
        }
        catch (TimeoutException e)
        {
            await StopAsync().ConfigureAwait(false);
            throw new AudioCaptureException(AudioErrorKind.NativeError, "The microphone did not deliver audio.", e);
        }
        catch
        {
            await StopAsync().ConfigureAwait(false);
            throw;
        }
    }

    public Task StopAsync() => Task.Run(() =>
    {
        List<SourceCapture> sources;
        Thread? pump;
        lock (_gate)
        {
            _running = false;
            sources = [.. _sources];
            _sources.Clear();
            pump = _pump;
            _pump = null;
        }
        foreach (var s in sources) s.Stop();
        pump?.Join(TimeSpan.FromSeconds(1));
        lock (_gate)
        {
            _mixer?.Pump(SystemMonotonicClock.Instance.NowMs); // flush what is already due
            _mixer = null;
            if (_enumerator is not null && _watcher is not null)
            {
                try { _enumerator.UnregisterEndpointNotificationCallback(_watcher); }
                catch (COMException) { }
            }
            _watcher = null;
            _enumerator?.Dispose();
            _enumerator = null;
        }
    });

    private void PumpLoop()
    {
        while (_running)
        {
            _mixer?.Pump(SystemMonotonicClock.Instance.NowMs);
            Thread.Sleep(20);
        }
    }

    private void OnSourceFailed(AudioCaptureException e)
    {
        if (!_running || Interlocked.Exchange(ref _failedRaised, 1) == 1) return;
        _diag.Warn("audio.failed", ("kind", e.Kind.ToString()));
        Failed?.Invoke(this, e);
    }

    /// <summary>One WASAPI stream (mic capture or loopback) feeding one mixer input.</summary>
    private sealed class SourceCapture
    {
        private readonly int _input;
        private readonly WasapiCapture _capture;
        private readonly StreamingResampler _resampler;
        private readonly TimelineMixer _mixer;
        private readonly Action<AudioCaptureException> _onFailed;
        private readonly TaskCompletionSource _firstData = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ManualResetEventSlim _stopped = new(false);
        private readonly SampleReader _reader;
        private float[] _interleaved = new float[4800];
        private float[] _resampled = new float[2048];
        private volatile bool _stopping;

        public SourceCapture(int input, DataFlow flow, MMDevice device, TimelineMixer mixer, Action<AudioCaptureException> onFailed)
        {
            _input = input;
            Flow = flow;
            DeviceId = device.ID;
            _mixer = mixer;
            _onFailed = onFailed;
            _capture = flow == DataFlow.Render
                ? new WasapiLoopbackCapture(device)
                : new WasapiCapture(device, true, 20);
            var fmt = _capture.WaveFormat;
            _reader = SampleReader.For(fmt);
            _resampler = new StreamingResampler(fmt.SampleRate, fmt.Channels);
            FormatDescription = $"{(flow == DataFlow.Render ? "loopback" : "mic")}:{fmt.SampleRate}Hz/{fmt.Channels}ch/{fmt.BitsPerSample}bit";
            _capture.DataAvailable += OnData;
            _capture.RecordingStopped += OnStopped;
        }

        public DataFlow Flow { get; }
        public string DeviceId { get; }
        public string FormatDescription { get; }
        public Task FirstData => _firstData.Task;

        public void Start()
        {
            try
            {
                _capture.StartRecording();
            }
            catch (COMException e) when (e.HResult == E_ACCESSDENIED)
            {
                throw new AudioCaptureException(AudioErrorKind.PermissionDenied, "Audio access denied.", e);
            }
            catch (COMException e)
            {
                throw new AudioCaptureException(AudioErrorKind.NativeError, "Audio capture could not start.", e);
            }
        }

        public void Stop()
        {
            _stopping = true;
            try
            {
                _capture.StopRecording();
                _stopped.Wait(TimeSpan.FromSeconds(1));
            }
            catch (COMException) { }
            catch (InvalidOperationException) { }
            _capture.DataAvailable -= OnData;
            _capture.RecordingStopped -= OnStopped;
            _capture.Dispose();
        }

        private void OnData(object? sender, WaveInEventArgs e)
        {
            if (e.BytesRecorded <= 0) return;
            var samples = _reader.SampleCount(e.BytesRecorded);
            if (_interleaved.Length < samples) _interleaved = new float[samples * 2];
            _reader.Read(e.Buffer.AsSpan(0, e.BytesRecorded), _interleaved);

            var frames = samples / _reader.Channels;
            var max = _resampler.MaxOutput(frames);
            if (_resampled.Length < max) _resampled = new float[max * 2];
            var n = _resampler.Process(_interleaved.AsSpan(0, samples), _resampled);
            _mixer.Write(_input, _resampled.AsSpan(0, n));
            _firstData.TrySetResult();
        }

        private void OnStopped(object? sender, StoppedEventArgs e)
        {
            _stopped.Set();
            if (_stopping) return;
            var kind = e.Exception is COMException { HResult: AUDCLNT_E_DEVICE_INVALIDATED } || e.Exception is null
                ? AudioErrorKind.DeviceLost
                : AudioErrorKind.NativeError;
            var error = new AudioCaptureException(kind, "Audio capture stopped.", e.Exception);
            _firstData.TrySetException(error);
            _onFailed(error);
        }
    }

    /// <summary>Converts WASAPI mix-format bytes to interleaved float.</summary>
    private sealed class SampleReader
    {
        private static readonly Guid IeeeFloatSubtype = new("00000003-0000-0010-8000-00aa00389b71"); // KSDATAFORMAT_SUBTYPE_IEEE_FLOAT

        private readonly int _bytesPerSample;
        private readonly bool _isFloat;

        private SampleReader(int bits, bool isFloat, int channels)
        {
            _bytesPerSample = bits / 8;
            _isFloat = isFloat;
            Channels = channels;
        }

        public int Channels { get; }

        public static SampleReader For(WaveFormat fmt)
        {
            var isFloat = fmt.Encoding == WaveFormatEncoding.IeeeFloat ||
                          (fmt is WaveFormatExtensible ext && ext.SubFormat == IeeeFloatSubtype);
            if (isFloat && fmt.BitsPerSample != 32 || !isFloat && fmt.BitsPerSample is not (16 or 24 or 32))
                throw new AudioCaptureException(AudioErrorKind.NativeError, $"Unsupported device format ({fmt.BitsPerSample}-bit).");
            return new SampleReader(fmt.BitsPerSample, isFloat, fmt.Channels);
        }

        public int SampleCount(int bytes) => bytes / _bytesPerSample / Channels * Channels;

        public void Read(ReadOnlySpan<byte> src, Span<float> dst)
        {
            var count = SampleCount(src.Length);
            if (_isFloat)
            {
                MemoryMarshal.Cast<byte, float>(src[..(count * 4)]).CopyTo(dst);
                return;
            }
            for (var i = 0; i < count; i++)
            {
                var o = i * _bytesPerSample;
                dst[i] = _bytesPerSample switch
                {
                    2 => BitConverter.ToInt16(src.Slice(o, 2)) / 32768f,
                    3 => ((src[o] << 8 | src[o + 1] << 16 | src[o + 2] << 24) >> 8) / 8388608f,
                    _ => BitConverter.ToInt32(src.Slice(o, 4)) / 2147483648f,
                };
            }
        }
    }

    /// <summary>Default-device change or removal ends the session with a clear error (AUDIO 04).</summary>
    private sealed class DeviceWatcher(List<(DataFlow Flow, string DeviceId)> watched, Action<AudioCaptureException> onFailed)
        : IMMNotificationClient
    {
        public void OnDeviceStateChanged(string deviceId, DeviceState newState)
        {
            if (newState != DeviceState.Active && watched.Any(w => w.DeviceId == deviceId))
                onFailed(new AudioCaptureException(AudioErrorKind.DeviceLost, "Audio device disconnected."));
        }

        public void OnDeviceAdded(string pwstrDeviceId) { }

        public void OnDeviceRemoved(string deviceId)
        {
            if (watched.Any(w => w.DeviceId == deviceId))
                onFailed(new AudioCaptureException(AudioErrorKind.DeviceLost, "Audio device removed."));
        }

        public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
        {
            if (role != Role.Console) return;
            if (watched.Any(w => w.Flow == flow && w.DeviceId != defaultDeviceId))
                onFailed(new AudioCaptureException(AudioErrorKind.DefaultDeviceChanged, "Default audio device changed."));
        }

        public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) { }
    }
}
