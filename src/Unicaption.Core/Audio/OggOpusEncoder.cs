using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Concentus.Enums;
using Concentus;

namespace Unicaption.Core.Audio;

/// <summary>
/// Turns the 16 kHz mono PCM16 stream into a live Ogg/Opus stream (RFC 7845). The deployed
/// consumer always asks Soniox for <c>audio_format: auto</c>, which cannot decode headerless PCM
/// but detects Ogg/Opus from its first page (verified against the live service). Every call
/// returns whole Ogg pages, so each WebSocket frame can be forwarded on its own.
/// <see cref="Reset"/> starts a new logical stream (headers again) for a new upstream session.
/// </summary>
public sealed class OggOpusEncoder
{
    public const int FrameSamples = AudioFormat.SampleRate / 50; // 20 ms
    private const int GranulePerFrame = 48_000 / 50;             // Ogg Opus granule is always 48 kHz
    private const int Bitrate = 32_000;
    private const int MaxPacket = 1275;

    private readonly IOpusEncoder _opus;
    private readonly short[] _frame = new short[FrameSamples];
    private readonly byte[] _packet = new byte[MaxPacket];
    private readonly MemoryStream _out = new();
    private readonly List<byte[]> _pending = [];
    private int _frameFill;
    private byte _oddByte;
    private bool _hasOddByte;
    private uint _serial;
    private uint _pageSeq;
    private long _granule;
    private bool _headersSent;

    public OggOpusEncoder()
    {
        _opus = OpusCodecFactory.CreateEncoder(AudioFormat.SampleRate, AudioFormat.Channels, OpusApplication.OPUS_APPLICATION_AUDIO);
        _opus.Bitrate = Bitrate;
        _opus.SignalType = OpusSignal.OPUS_SIGNAL_VOICE;
        Reset();
    }

    /// <summary>Begins a new logical stream: the next <see cref="Encode"/> starts with OpusHead/OpusTags.</summary>
    public void Reset()
    {
        _opus.ResetState();
        _serial = (uint)Random.Shared.Next();
        _pageSeq = 0;
        _granule = 0;
        _frameFill = 0;
        _hasOddByte = false;
        _headersSent = false;
    }

    /// <summary>Encodes PCM16 LE bytes; returns the Ogg pages completed by them (may be empty).</summary>
    public byte[] Encode(ReadOnlySpan<byte> pcm)
    {
        _out.SetLength(0);
        if (!_headersSent)
        {
            WritePage(OpusHead(), 0, 0x02);
            WritePage(OpusTags(), 0, 0x00);
            _headersSent = true;
        }

        if (_hasOddByte && pcm.Length > 0)
        {
            PushSample((short)(_oddByte | (pcm[0] << 8)));
            pcm = pcm[1..];
            _hasOddByte = false;
        }
        var samples = MemoryMarshal.Cast<byte, short>(pcm[..(pcm.Length & ~1)]);
        foreach (var s in samples) PushSample(BitConverter.IsLittleEndian ? s : BinaryPrimitives.ReverseEndianness(s));
        if ((pcm.Length & 1) == 1)
        {
            _oddByte = pcm[^1];
            _hasOddByte = true;
        }

        FlushPackets();
        return _out.ToArray();
    }

    private void PushSample(short s)
    {
        _frame[_frameFill++] = s;
        if (_frameFill < FrameSamples) return;
        _frameFill = 0;
        var n = _opus.Encode(_frame, FrameSamples, _packet, MaxPacket);
        _pending.Add(_packet[..n]);
        _granule += GranulePerFrame;
    }

    /// <summary>One page per call (latency); a page holds at most 255 lacing values.</summary>
    private void FlushPackets()
    {
        if (_pending.Count == 0) return;
        var page = new List<byte[]>();
        var lacing = 0;
        var granule = _granule - (long)_pending.Count * GranulePerFrame; // end of the packets in `page`
        foreach (var p in _pending)
        {
            var need = p.Length / 255 + 1;
            if (lacing + need > 255)
            {
                WritePage(page, granule, 0x00);
                page.Clear();
                lacing = 0;
            }
            page.Add(p);
            lacing += need;
            granule += GranulePerFrame;
        }
        WritePage(page, _granule, 0x00);
        _pending.Clear();
    }

    private void WritePage(byte[] packet, long granule, byte flags) => WritePage([packet], granule, flags);

    private void WritePage(IReadOnlyList<byte[]> packets, long granule, byte flags)
    {
        var lacing = new List<byte>();
        foreach (var p in packets)
        {
            for (var left = p.Length; ; left -= 255)
            {
                if (left < 255) { lacing.Add((byte)left); break; }
                lacing.Add(255);
            }
        }
        var body = packets.Sum(p => p.Length);
        var page = new byte[27 + lacing.Count + body];
        "OggS"u8.CopyTo(page);
        page[5] = flags;
        BinaryPrimitives.WriteInt64LittleEndian(page.AsSpan(6), granule);
        BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(14), _serial);
        BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(18), _pageSeq++);
        page[26] = (byte)lacing.Count;
        lacing.CopyTo(page, 27);
        var at = 27 + lacing.Count;
        foreach (var p in packets)
        {
            p.CopyTo(page, at);
            at += p.Length;
        }
        BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(22), OggCrc(page));
        _out.Write(page);
    }

    private byte[] OpusHead()
    {
        var h = new byte[19];
        "OpusHead"u8.CopyTo(h);
        h[8] = 1;
        h[9] = AudioFormat.Channels;
        BinaryPrimitives.WriteUInt16LittleEndian(h.AsSpan(10), (ushort)(_opus.Lookahead * (48_000 / AudioFormat.SampleRate)));
        BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(12), AudioFormat.SampleRate);
        return h; // output gain 0, mapping family 0
    }

    private static byte[] OpusTags()
    {
        var vendor = "Unicaption"u8;
        var t = new byte[8 + 4 + vendor.Length + 4];
        "OpusTags"u8.CopyTo(t);
        BinaryPrimitives.WriteUInt32LittleEndian(t.AsSpan(8), (uint)vendor.Length);
        vendor.CopyTo(t.AsSpan(12));
        return t; // zero user comments
    }

    private static readonly uint[] CrcTable = BuildCrcTable();

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            var r = i << 24;
            for (var k = 0; k < 8; k++) r = (r & 0x8000_0000) != 0 ? (r << 1) ^ 0x04C1_1DB7 : r << 1;
            table[i] = r;
        }
        return table;
    }

    internal static uint OggCrc(ReadOnlySpan<byte> page)
    {
        uint crc = 0;
        foreach (var b in page) crc = (crc << 8) ^ CrcTable[(byte)(crc >> 24) ^ b];
        return crc;
    }
}
