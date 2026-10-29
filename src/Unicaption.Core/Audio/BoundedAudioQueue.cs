using System.Threading.Channels;

namespace Unicaption.Core.Audio;

/// <summary>
/// Single-producer / single-consumer byte queue between the audio thread and the
/// network sender (AUDIO 04). Writes never block; when the byte budget (default 2 s
/// of 16 kHz mono PCM16) is exceeded the write is refused and <see cref="Overflowed"/>
/// is set so the session can stop with a clear error instead of growing RAM.
/// </summary>
public sealed class BoundedAudioQueue
{
    private readonly Channel<byte[]> _channel = Channel.CreateUnbounded<byte[]>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
    private long _bytes;
    private int _overflowed;

    public BoundedAudioQueue(int capacityBytes = AudioFormat.BytesPerSecond * 2) => CapacityBytes = capacityBytes;

    public int CapacityBytes { get; }
    public long QueuedBytes => Interlocked.Read(ref _bytes);
    public bool Overflowed => Volatile.Read(ref _overflowed) == 1;

    /// <summary>Called on the audio thread. Allocation-light, lock-free, non-blocking.</summary>
    public bool TryWrite(ReadOnlySpan<byte> pcm)
    {
        if (pcm.IsEmpty) return true;
        if (Overflowed) return false;
        if (Interlocked.Read(ref _bytes) + pcm.Length > CapacityBytes)
        {
            Interlocked.Exchange(ref _overflowed, 1);
            return false;
        }
        Interlocked.Add(ref _bytes, pcm.Length);
        return _channel.Writer.TryWrite(pcm.ToArray());
    }

    public void Complete() => _channel.Writer.TryComplete();

    public async ValueTask<byte[]?> ReadAsync(CancellationToken ct)
    {
        try
        {
            var chunk = await _channel.Reader.ReadAsync(ct).ConfigureAwait(false);
            Interlocked.Add(ref _bytes, -chunk.Length);
            return chunk;
        }
        catch (ChannelClosedException)
        {
            return null;
        }
    }
}
