namespace Unicaption.Core.Audio;

/// <summary>
/// Streaming windowed-sinc resampler: interleaved float frames at any device rate/channel
/// count → mono float at 16 kHz. Stateful across calls so buffer boundaries are seamless.
/// Allocation-free after warm-up; safe to call on the audio thread.
/// </summary>
public sealed class StreamingResampler
{
    private const int HalfTaps = 16;

    private readonly int _channels;
    private readonly double _step;
    private readonly double _cutoff;
    private float[] _buf;
    private int _count;
    private double _pos;

    public StreamingResampler(int inputRate, int channels, int outputRate = AudioFormat.SampleRate)
    {
        if (inputRate <= 0 || channels <= 0) throw new ArgumentOutOfRangeException(nameof(inputRate));
        InputRate = inputRate;
        _channels = channels;
        _step = (double)inputRate / outputRate;
        // Normalised cutoff relative to the input rate, with a little headroom below Nyquist.
        _cutoff = Math.Min(1.0, (double)outputRate / inputRate) * 0.92;
        _buf = new float[Math.Max(4096, inputRate / 5)];
        _count = HalfTaps; // zero history
        _pos = HalfTaps;
    }

    public int InputRate { get; }

    /// <summary>Appends <paramref name="interleaved"/> and writes produced 16 kHz samples to <paramref name="output"/>.</summary>
    /// <returns>Number of samples written.</returns>
    public int Process(ReadOnlySpan<float> interleaved, Span<float> output)
    {
        var frames = interleaved.Length / _channels;
        EnsureCapacity(_count + frames);
        for (var f = 0; f < frames; f++)
        {
            float sum = 0;
            var baseIdx = f * _channels;
            for (var c = 0; c < _channels; c++) sum += interleaved[baseIdx + c];
            _buf[_count++] = sum / _channels;
        }

        var written = 0;
        while (written < output.Length && _pos + HalfTaps < _count)
        {
            output[written++] = Sample(_pos);
            _pos += _step;
        }

        var drop = (int)Math.Floor(_pos) - HalfTaps;
        if (drop > 0)
        {
            Array.Copy(_buf, drop, _buf, 0, _count - drop);
            _count -= drop;
            _pos -= drop;
        }
        return written;
    }

    /// <summary>Upper bound of output samples for <paramref name="inputFrames"/> frames.</summary>
    public int MaxOutput(int inputFrames) => (int)Math.Ceiling((inputFrames + HalfTaps * 2) / _step) + 2;

    private float Sample(double center)
    {
        var i0 = (int)Math.Floor(center);
        double acc = 0, norm = 0;
        for (var i = i0 - HalfTaps + 1; i <= i0 + HalfTaps; i++)
        {
            var t = i - center;
            var w = Kernel(t);
            acc += _buf[i] * w;
            norm += w;
        }
        return norm == 0 ? 0 : (float)(acc / norm);
    }

    private double Kernel(double t)
    {
        var x = _cutoff * t;
        var sinc = Math.Abs(x) < 1e-9 ? 1.0 : Math.Sin(Math.PI * x) / (Math.PI * x);
        var window = 0.5 * (1 + Math.Cos(Math.PI * t / HalfTaps)); // Hann
        return sinc * window;
    }

    private void EnsureCapacity(int needed)
    {
        if (needed <= _buf.Length) return;
        Array.Resize(ref _buf, Math.Max(needed, _buf.Length * 2));
    }
}
