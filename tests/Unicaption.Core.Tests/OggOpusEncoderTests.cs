using System.Buffers.Binary;
using Concentus;
using Unicaption.Core.Audio;
using Xunit;

namespace Unicaption.Core.Tests;

public class OggOpusEncoderTests
{
    private sealed record Page(byte Flags, long Granule, uint Serial, uint Seq, List<byte[]> Packets);

    /// <summary>Parses Ogg pages and checks each CRC, as a decoder would.</summary>
    private static List<Page> ParsePages(byte[] data)
    {
        var pages = new List<Page>();
        var at = 0;
        while (at < data.Length)
        {
            Assert.Equal("OggS"u8.ToArray(), data[at..(at + 4)]);
            var segments = data[at + 26];
            var lacing = data[(at + 27)..(at + 27 + segments)];
            var length = 27 + segments + lacing.Sum(b => b);
            var page = data[at..(at + length)];
            var crc = BinaryPrimitives.ReadUInt32LittleEndian(page.AsSpan(22));
            page.AsSpan(22, 4).Clear();
            Assert.Equal(crc, OggOpusEncoder.OggCrc(page));

            var packets = new List<byte[]>();
            var body = 27 + segments;
            var current = new List<byte>();
            foreach (var l in lacing)
            {
                current.AddRange(page[body..(body + l)]);
                body += l;
                if (l < 255) { packets.Add([.. current]); current.Clear(); }
            }
            pages.Add(new Page(page[5], BinaryPrimitives.ReadInt64LittleEndian(page.AsSpan(6)),
                BinaryPrimitives.ReadUInt32LittleEndian(page.AsSpan(14)),
                BinaryPrimitives.ReadUInt32LittleEndian(page.AsSpan(18)), packets));
            at += length;
        }
        return pages;
    }

    private static byte[] Tone(int samples)
    {
        var pcm = new byte[samples * 2];
        for (var i = 0; i < samples; i++)
            BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 2), (short)(8000 * Math.Sin(i * 2 * Math.PI * 440 / 16000)));
        return pcm;
    }

    [Fact] // RFC 7845: BOS page with OpusHead, then OpusTags, then audio with 48 kHz granules
    public void FirstOutput_StartsWithHeaders_ThenDecodableAudio()
    {
        var enc = new OggOpusEncoder();
        var pages = ParsePages(enc.Encode(Tone(1600))); // 100 ms = 5 Opus frames

        Assert.Equal(3, pages.Count);
        Assert.Equal(0x02, pages[0].Flags);
        Assert.Equal("OpusHead"u8.ToArray(), pages[0].Packets[0][..8]);
        Assert.Equal(1, pages[0].Packets[0][9]);
        Assert.Equal(16000u, BinaryPrimitives.ReadUInt32LittleEndian(pages[0].Packets[0].AsSpan(12)));
        Assert.Equal("OpusTags"u8.ToArray(), pages[1].Packets[0][..8]);
        Assert.Equal([0u, 1u, 2u], pages.Select(p => p.Seq));
        Assert.All(pages, p => Assert.Equal(pages[0].Serial, p.Serial));
        Assert.Equal(5 * 960, pages[2].Granule);

        var decoder = OpusCodecFactory.CreateDecoder(16000, 1);
        var pcm = new short[OggOpusEncoder.FrameSamples];
        Assert.Equal(5, pages[2].Packets.Count);
        foreach (var packet in pages[2].Packets)
            Assert.Equal(OggOpusEncoder.FrameSamples, decoder.Decode(packet, pcm, OggOpusEncoder.FrameSamples));
    }

    [Fact] // chunks that do not end on a frame or even a sample boundary are carried over
    public void PartialChunks_AreBufferedAcrossCalls()
    {
        var enc = new OggOpusEncoder();
        var tone = Tone(OggOpusEncoder.FrameSamples);
        var first = ParsePages(enc.Encode(tone.AsSpan(0, 301)));
        Assert.Equal(2, first.Count); // headers only, no full frame yet
        var rest = ParsePages(enc.Encode(tone.AsSpan(301)));
        Assert.Single(rest);
        Assert.Single(rest[0].Packets);
        Assert.Equal(960, rest[0].Granule);
        Assert.Empty(enc.Encode(new byte[10]));
    }

    [Fact] // a resent config opens a new Soniox stream, which needs its own headers
    public void Reset_StartsNewLogicalStream()
    {
        var enc = new OggOpusEncoder();
        var before = ParsePages(enc.Encode(Tone(640)));
        enc.Reset();
        var after = ParsePages(enc.Encode(Tone(640)));
        Assert.Equal(0x02, after[0].Flags);
        Assert.Equal(0u, after[0].Seq);
        Assert.Equal(2 * 960, after[^1].Granule);
        Assert.NotEqual(before[0].Serial, after[0].Serial);
    }
}
