using System.Buffers.Binary;
using System.Text;
using Xunit;

namespace PulseGen.Engine.Client.Tests;

/// <summary>Silent Layer III packets exercise container structure independently of an encoder.</summary>
public sealed class Mp3PacketStructureTests : IDisposable
{
    private readonly string directory = Directory.CreateTempSubdirectory("pulsegen-mp3-packets-").FullName;

    [Theory]
    [InlineData(2, 0, 1, 22050)]
    [InlineData(2, 1, 2, 24000)]
    [InlineData(2, 2, 2, 16000)]
    [InlineData(0, 0, 2, 11025)]
    [InlineData(0, 1, 1, 12000)]
    [InlineData(0, 2, 1, 8000)]
    public void Mpeg2And25PacketsKeepTheirPhysicalRateChannelsAndDuration(int version, int rateIndex, int channels, int rate)
    {
        var bytes = SilentPackets(version, rateIndex, channels, 4);
        var audio = SampleAudioReader.Read(Write(bytes));
        Assert.Equal(rate, audio.SampleRate);
        Assert.Equal(channels, audio.Channels);
        Assert.Equal(4 * 576, audio.Frames);
        Assert.All(audio.Samples, sample => Assert.Equal(0, sample));
    }

    [Fact]
    public void MatchedId3v24FooterAndApeHeaderFooterDoNotBecomeAudio()
    {
        var packets = SilentPackets(3, 0, 2, 4);
        var plain = SampleAudioReader.Read(Write(packets));
        var decorated = Join(Id3WithFooter(), packets, ApeTag(), Id3v1());
        var path = Write(decorated);
        var audio = SampleAudioReader.Read(path);
        Assert.Equal(plain.Samples, audio.Samples);
        Assert.Equal(decorated, File.ReadAllBytes(path));
    }

    [Theory]
    [InlineData("id3-footer")]
    [InlineData("id3-synchsafe")]
    [InlineData("ape-size")]
    [InlineData("ape-header")]
    [InlineData("ape-item")]
    public void MalformedMetadataCannotHideUnaccountedPacketBytes(string damage)
    {
        var id3 = Id3WithFooter();
        var ape = ApeTag();
        switch (damage)
        {
            case "id3-footer": id3[^10] = (byte)'X'; break;
            case "id3-synchsafe": id3[6] = 0x80; break;
            case "ape-size": BinaryPrimitives.WriteUInt32LittleEndian(ape.AsSpan(ape.Length - 20), uint.MaxValue); break;
            case "ape-header": ape[8] ^= 1; break;
            case "ape-item": BinaryPrimitives.WriteUInt32LittleEndian(ape.AsSpan(32), 100000); break;
        }
        var path = Write(Join(id3, SilentPackets(3, 0, 2, 4), ape));
        Assert.Throws<InvalidDataException>(() => SampleAudioReader.Read(path));
    }

    [Fact]
    public void CompleteVbriStreamUsesEveryPhysicalPacketWithoutInventedGaplessTrim()
    {
        var bytes = VbriPackets();
        var audio = SampleAudioReader.Read(Write(bytes));
        Assert.Equal(4 * 1152, audio.Frames);
        Assert.Equal(44100, audio.SampleRate);
        Assert.Equal(2, audio.Channels);
        Assert.All(audio.Samples, sample => Assert.Equal(0, sample));
    }

    [Theory]
    [InlineData("frame-count")]
    [InlineData("byte-count")]
    [InlineData("entry-width")]
    [InlineData("entry-coverage")]
    [InlineData("entry-value")]
    [InlineData("truncated-table")]
    public void CorruptVbriDeclarationsAndTablesAreRejected(string damage)
    {
        var bytes = VbriPackets(damage == "truncated-table" ? 200 : 4);
        var tag = bytes.AsSpan(36);
        switch (damage)
        {
            case "frame-count": BinaryPrimitives.WriteUInt32BigEndian(tag[14..], 99); break;
            case "byte-count": BinaryPrimitives.WriteUInt32BigEndian(tag[10..], 1); break;
            case "entry-width": BinaryPrimitives.WriteUInt16BigEndian(tag[22..], 5); break;
            case "entry-coverage": BinaryPrimitives.WriteUInt16BigEndian(tag[24..], 1); break;
            case "entry-value": BinaryPrimitives.WriteUInt32BigEndian(tag[26..], 0); break;
            case "truncated-table":
                BinaryPrimitives.WriteUInt16BigEndian(tag[18..], 200);
                BinaryPrimitives.WriteUInt16BigEndian(tag[24..], 1);
                break;
        }
        Assert.Throws<InvalidDataException>(() => SampleAudioReader.Read(Write(bytes)));
    }

    private string Write(byte[] bytes)
    {
        var path = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".mp3");
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static byte[] SilentPackets(int version, int rateIndex, int channels, int count)
    {
        var packets = new List<byte[]>();
        var rate = (rateIndex switch { 0 => 44100, 1 => 48000, _ => 32000 }) >> (version == 3 ? 0 : version == 2 ? 1 : 2);
        var bitrateIndex = version == 3 ? 9 : 8;
        var bitrate = version == 3 ? 128 : 64;
        for (var index = 0; index < count; index++)
        {
            var padding = index & 1;
            var header = 0xffe00000U | ((uint)version << 19) | (1U << 17) | (1U << 16) |
                ((uint)bitrateIndex << 12) | ((uint)rateIndex << 10) | ((uint)padding << 9) |
                (channels == 1 ? 3U << 6 : 0U);
            var packet = new byte[(version == 3 ? 144000 : 72000) * bitrate / rate + padding];
            BinaryPrimitives.WriteUInt32BigEndian(packet, header);
            packets.Add(packet);
        }
        return Join(packets.ToArray());
    }

    private static byte[] Id3WithFooter()
    {
        var result = new byte[24];
        "ID3"u8.CopyTo(result);
        result[3] = 4;
        result[5] = 16;
        result[9] = 4;
        "3DI"u8.CopyTo(result.AsSpan(14));
        result.AsSpan(3, 7).CopyTo(result.AsSpan(17));
        return result;
    }

    private static byte[] Id3v1()
    {
        var result = new byte[128];
        "TAG"u8.CopyTo(result);
        return result;
    }

    private static byte[] ApeTag()
    {
        var key = "Title\0"u8.ToArray();
        var value = Encoding.UTF8.GetBytes("test");
        var result = new byte[32 + 8 + key.Length + value.Length + 32];
        var footer = result.AsSpan(result.Length - 32);
        "APETAGEX"u8.CopyTo(footer);
        BinaryPrimitives.WriteUInt32LittleEndian(footer[8..], 2000);
        BinaryPrimitives.WriteUInt32LittleEndian(footer[12..], (uint)result.Length - 32);
        BinaryPrimitives.WriteUInt32LittleEndian(footer[16..], 1);
        BinaryPrimitives.WriteUInt32LittleEndian(footer[20..], 0x80000000);
        footer.CopyTo(result);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(20), 0xa0000000);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(32), (uint)value.Length);
        key.CopyTo(result, 40);
        value.CopyTo(result, 40 + key.Length);
        return result;
    }

    private static byte[] VbriPackets(int count = 4)
    {
        var result = SilentPackets(3, 0, 2, count);
        var tag = result.AsSpan(36);
        "VBRI"u8.CopyTo(tag);
        BinaryPrimitives.WriteUInt16BigEndian(tag[4..], 1);
        BinaryPrimitives.WriteUInt32BigEndian(tag[10..], (uint)result.Length);
        BinaryPrimitives.WriteUInt32BigEndian(tag[14..], (uint)count);
        BinaryPrimitives.WriteUInt16BigEndian(tag[18..], 1);
        BinaryPrimitives.WriteUInt16BigEndian(tag[20..], 1);
        BinaryPrimitives.WriteUInt16BigEndian(tag[22..], 4);
        BinaryPrimitives.WriteUInt16BigEndian(tag[24..], (ushort)count);
        BinaryPrimitives.WriteUInt32BigEndian(tag[26..], (uint)result.Length);
        return result;
    }

    private static byte[] Join(params byte[][] sections)
    {
        var result = new byte[sections.Sum(section => section.Length)];
        var offset = 0;
        foreach (var section in sections) { section.CopyTo(result, offset); offset += section.Length; }
        return result;
    }

    public void Dispose() => Directory.Delete(directory, recursive: true);
}
