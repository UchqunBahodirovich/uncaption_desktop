using Unicaption.Core.Audio;
using Xunit;

namespace Unicaption.Core.Tests;

public class AudioTests
{
    private static float[] Sine(int rate, int channels, double seconds, double hz, float amp = 0.5f)
    {
        var frames = (int)(rate * seconds);
        var data = new float[frames * channels];
        for (var i = 0; i < frames; i++)
        for (var c = 0; c < channels; c++)
            data[i * channels + c] = amp * (float)Math.Sin(2 * Math.PI * hz * i / rate);
        return data;
    }

    [Theory] // AUDIO 02: 44.1/48 kHz stereo → 16 kHz mono
    [InlineData(48_000, 2)]
    [InlineData(44_100, 2)]
    [InlineData(16_000, 1)]
    public void Resampler_ProducesSixteenKhz_InChunks(int rate, int channels)
    {
        var r = new StreamingResampler(rate, channels);
        var input = Sine(rate, channels, 1.0, 440);
        var output = new float[20_000];
        var produced = 0;
        var chunk = rate / 100 * channels; // 10 ms device buffers
        for (var off = 0; off < input.Length; off += chunk)
        {
            var span = input.AsSpan(off, Math.Min(chunk, input.Length - off));
            produced += r.Process(span, output.AsSpan(produced));
        }
        Assert.InRange(produced, 15_950, 16_000);

        var peak = output.Skip(1000).Take(10_000).Max(Math.Abs);
        Assert.InRange(peak, 0.45f, 0.55f); // speech band preserved
    }

    [Fact]
    public void Resampler_AttenuatesAboveNyquist()
    {
        var r = new StreamingResampler(48_000, 1);
        var input = Sine(48_000, 1, 0.5, 12_000);
        var output = new float[10_000];
        var n = r.Process(input, output);
        var peak = output.Take(n).Skip(200).Max(Math.Abs);
        Assert.True(peak < 0.05f, $"aliasing peak {peak}");
    }

    [Fact] // AC 07 / AUDIO 03: two sources, one timeline, not double duration
    public void Mixer_TwoSources_OneSecondIsOneSecond()
    {
        long bytes = 0;
        var mixer = new TimelineMixer(2, pcm => bytes += pcm.Length);
        var t = 0.0;
        mixer.Start(t);
        var tenMs = new float[160];
        Array.Fill(tenMs, 0.1f);
        for (var i = 0; i < 100; i++)
        {
            mixer.Write(0, tenMs);
            mixer.Write(1, tenMs);
            t += 10;
            mixer.Pump(t);
        }
        mixer.Pump(t + 120); // drain jitter budget
        Assert.Equal(AudioFormat.BytesPerSecond, bytes);
    }

    [Fact] // Loopback delivers nothing during silence: the timeline is padded
    public void Mixer_SilentSource_IsPaddedWithZeros()
    {
        var all = new List<byte>();
        var mixer = new TimelineMixer(1, pcm => all.AddRange(pcm.ToArray()));
        mixer.Start(0);
        mixer.Pump(1_120);
        Assert.Equal(AudioFormat.BytesPerSecond, all.Count);
        Assert.All(all, b => Assert.Equal(0, b));
    }

    [Fact]
    public void Mixer_SumIsSoftLimited()
    {
        short first = 0;
        var mixer = new TimelineMixer(2, pcm => first = BitConverter.ToInt16(pcm[..2]));
        mixer.Start(0);
        var loud = new float[16];
        Array.Fill(loud, 0.9f);
        mixer.Write(0, loud);
        mixer.Write(1, loud);
        mixer.Pump(121);
        Assert.InRange(first, (short)30_000, short.MaxValue);
        Assert.Equal(0.5f, TimelineMixer.SoftClip(0.5f));
        Assert.True(TimelineMixer.SoftClip(5f) <= 1f);
        Assert.True(TimelineMixer.SoftClip(-5f) >= -1f);
    }

    [Fact] // AUDIO 04: bounded queue refuses instead of growing
    public async Task Queue_OverflowsAtTwoSeconds()
    {
        var q = new BoundedAudioQueue();
        var second = new byte[AudioFormat.BytesPerSecond];
        Assert.True(q.TryWrite(second));
        Assert.True(q.TryWrite(second));
        Assert.False(q.TryWrite(new byte[2]));
        Assert.True(q.Overflowed);

        var chunk = await q.ReadAsync(CancellationToken.None);
        Assert.Equal(AudioFormat.BytesPerSecond, chunk!.Length);
        Assert.Equal(AudioFormat.BytesPerSecond, q.QueuedBytes);
        q.Complete();
        Assert.NotNull(await q.ReadAsync(CancellationToken.None));
        Assert.Null(await q.ReadAsync(CancellationToken.None));
    }
}
