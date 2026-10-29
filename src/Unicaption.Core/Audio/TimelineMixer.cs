namespace Unicaption.Core.Audio;

/// <summary>
/// Mixes 1..N already-resampled 16 kHz mono inputs on one monotonic timeline (AUDIO 03).
/// Inputs only append to their FIFO (cheap, called from device callbacks); a pump thread
/// calls <see cref="Pump"/> periodically and the mixer emits exactly as many samples as
/// wall time demands. A source that is silent (e.g. loopback with nothing playing
/// delivers no packets) is padded with zeros; a source that runs ahead because of device
/// clock drift is trimmed. One second of capture therefore yields ~32 000 bytes.
/// </summary>
public sealed class TimelineMixer
{
    private const double JitterMs = 120;          // latency budget before padding a late source
    private const int MaxBacklogSamples = 8_000;  // 0.5 s; older samples are dropped (drift)

    private readonly Fifo[] _inputs;
    private readonly PcmSink _sink;
    private readonly object _pumpGate = new();
    private float[] _mix = new float[3200];
    private byte[] _pcm = new byte[6400];
    private double _startMs = double.NaN;
    private long _emitted;

    public TimelineMixer(int inputCount, PcmSink sink)
    {
        if (inputCount < 1) throw new ArgumentOutOfRangeException(nameof(inputCount));
        _inputs = Enumerable.Range(0, inputCount).Select(_ => new Fifo(AudioFormat.SampleRate * 2)).ToArray();
        _sink = sink;
    }

    public long EmittedSamples => Interlocked.Read(ref _emitted);
    public long DroppedSamples => _inputs.Sum(i => i.Dropped);

    /// <summary>Marks t=0 of the timeline. Must be called before the first <see cref="Pump"/>.</summary>
    public void Start(double nowMs)
    {
        lock (_pumpGate)
        {
            _startMs = nowMs;
            _emitted = 0;
            foreach (var f in _inputs) f.Clear();
        }
    }

    public void Write(int input, ReadOnlySpan<float> samples) => _inputs[input].Write(samples, MaxBacklogSamples);

    /// <summary>Emits samples due up to <paramref name="nowMs"/>; returns the number emitted.</summary>
    public int Pump(double nowMs)
    {
        lock (_pumpGate)
        {
            if (double.IsNaN(_startMs)) return 0;
            var due = (long)Math.Floor((nowMs - _startMs - JitterMs) * AudioFormat.SampleRate / 1000.0);
            var n = (int)Math.Min(due - _emitted, AudioFormat.SampleRate); // at most 1 s per pump
            if (n <= 0) return 0;

            if (_mix.Length < n) _mix = new float[n];
            if (_pcm.Length < n * 2) _pcm = new byte[n * 2];
            var mix = _mix.AsSpan(0, n);
            mix.Clear();
            foreach (var input in _inputs) input.ReadAdd(mix);

            var pcm = _pcm.AsSpan(0, n * 2);
            for (var i = 0; i < n; i++)
            {
                var s = (short)Math.Round(SoftClip(mix[i]) * short.MaxValue);
                pcm[2 * i] = (byte)s;          // little-endian
                pcm[2 * i + 1] = (byte)(s >> 8);
            }
            _emitted += n;
            _sink(pcm);
            return n;
        }
    }

    /// <summary>Transparent below 0.9, smooth knee to ±1 above (clipping limit).</summary>
    internal static float SoftClip(float x)
    {
        var a = Math.Abs(x);
        if (a <= 0.9f) return x;
        var y = 0.9f + 0.1f * MathF.Tanh((a - 0.9f) / 0.1f);
        return MathF.CopySign(y, x);
    }

    private sealed class Fifo(int capacity)
    {
        private readonly float[] _data = new float[capacity];
        private readonly object _gate = new();
        private int _head;
        private int _count;
        public long Dropped;

        public void Clear()
        {
            lock (_gate) { _head = 0; _count = 0; }
        }

        public void Write(ReadOnlySpan<float> samples, int maxBacklog)
        {
            lock (_gate)
            {
                foreach (var s in samples)
                {
                    if (_count == _data.Length) { _head = (_head + 1) % _data.Length; _count--; Dropped++; }
                    _data[(_head + _count) % _data.Length] = s;
                    _count++;
                }
                if (_count > maxBacklog)
                {
                    var drop = _count - maxBacklog;
                    _head = (_head + drop) % _data.Length;
                    _count -= drop;
                    Dropped += drop;
                }
            }
        }

        /// <summary>Adds up to dest.Length samples into dest; missing samples stay silent.</summary>
        public void ReadAdd(Span<float> dest)
        {
            lock (_gate)
            {
                var n = Math.Min(dest.Length, _count);
                for (var i = 0; i < n; i++) dest[i] += _data[(_head + i) % _data.Length];
                _head = (_head + n) % _data.Length;
                _count -= n;
            }
        }
    }
}
