using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using Xunit;

namespace PulseGen.Engine.Client.Tests;

/// <summary>Independent MP3 fixtures come from the pinned encoder; none contain user media.</summary>
public sealed class Mp3ImportTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"pulsegen-mp3-import-{Guid.NewGuid():N}");

    [Theory]
    [InlineData(44100, 1)]
    [InlineData(44100, 2)]
    [InlineData(48000, 1)]
    [InlineData(48000, 2)]
    public void TaggedGaplessSourcesKeepTheirExactOriginalDuration(int sampleRate, int channels)
    {
        const int originalFrames = 44117;
        var path = WriteTagged("gapless.mp3", sampleRate, channels, originalFrames, variableBitrate: true);
        var originalBytes = File.ReadAllBytes(path);
        var packets = ScanFixturePackets(originalBytes);
        Assert.True(packets.Sum(packet => packet.PcmFrames) > originalFrames);

        var decoded = SampleAudioReader.Read(path);

        Assert.Equal(sampleRate, decoded.SampleRate);
        Assert.Equal(channels, decoded.Channels);
        Assert.Equal(originalFrames, decoded.Frames);
        Assert.Equal(originalFrames / (double)sampleRate, decoded.DurationSeconds);
        Assert.Equal("MP3", decoded.Container);
        Assert.Contains(decoded.Samples, sample => Math.Abs(sample) > 0.1f);
        Assert.Equal(originalBytes, File.ReadAllBytes(path));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void CompleteUntaggedCbrWithPaddingUsesActualPacketsInsteadOfEstimatedLength(int channels)
    {
        var path = WriteUntagged("cbr.mp3", channels, 157701, variableBitrate: false);
        var bytes = File.ReadAllBytes(path);
        var packets = ScanFixturePackets(bytes);
        Assert.Contains(packets, packet => packet.Padding);
        Assert.Contains(packets, packet => !packet.Padding);
        var expectedFrames = packets.Sum(packet => packet.PcmFrames);
        var oldEstimate = LegacyFrameEstimate(path);
        Assert.True(oldEstimate > expectedFrames,
            $"Fixture must reproduce a positive estimate error; initial={oldEstimate}, packets={expectedFrames}.");

        var decoded = SampleAudioReader.Read(path);

        Assert.Equal(44100, decoded.SampleRate);
        Assert.Equal(channels, decoded.Channels);
        Assert.Equal(expectedFrames, decoded.Frames);
        Assert.Contains(decoded.Samples, sample => Math.Abs(sample) > 0.1f);
        Assert.Equal(bytes, File.ReadAllBytes(path));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UntaggedVariableBitrateRetainsAllPacketsForBothEstimateDirections(bool beginsWithComplexSound)
    {
        var path = WriteUntagged("variable.mp3", 2, 88200, variableBitrate: true,
            profile: beginsWithComplexSound ? SignalProfile.ComplexThenQuiet : SignalProfile.QuietThenComplex);
        var packets = ScanFixturePackets(File.ReadAllBytes(path));
        Assert.True(packets.Select(packet => packet.Length).Distinct().Count() > 2);
        var expectedFrames = packets.Sum(packet => packet.PcmFrames);
        var oldEstimate = LegacyFrameEstimate(path);
        Assert.True(beginsWithComplexSound ? oldEstimate < expectedFrames : oldEstimate > expectedFrames,
            $"Fixture must exercise {(beginsWithComplexSound ? "underestimate" : "overestimate")}; initial={oldEstimate}, packets={expectedFrames}.");

        var decoded = SampleAudioReader.Read(path);

        Assert.Equal(expectedFrames, decoded.Frames);
        Assert.Equal(2, decoded.Channels);
        Assert.All(decoded.Samples, sample => Assert.True(float.IsFinite(sample)));
    }

    [Fact]
    public void MetadataTagsDoNotBecomeAudioOrAlterCompletePacketCount()
    {
        var path = WriteUntagged("plain.mp3", 1, 48117, variableBitrate: false);
        var audioBytes = File.ReadAllBytes(path);
        var plain = SampleAudioReader.Read(path);
        var decorated = Path.Combine(root, "metadata.mp3");
        var tagBytes = BuildId3Tags(audioBytes);
        File.WriteAllBytes(decorated, tagBytes);

        var decoded = SampleAudioReader.Read(decorated);

        Assert.Equal(plain.Frames, decoded.Frames);
        Assert.Equal(plain.Samples, decoded.Samples);
        Assert.Equal("Synthetic MP3 import fixture", decoded.Title);
        Assert.Equal("Kept original metadata", decoded.Comment);
        Assert.Equal(tagBytes, File.ReadAllBytes(decorated));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void IncompleteLastPacketIsRejectedWhetherOrNotGaplessMetadataExists(bool tagged)
    {
        var path = tagged
            ? WriteTagged("incomplete.mp3", 44100, 2, 22067, variableBitrate: true)
            : WriteUntagged("incomplete.mp3", 2, 22067, variableBitrate: false);
        using (var file = File.OpenWrite(path)) file.SetLength(file.Length - 17);
        var unchanged = File.ReadAllBytes(path);

        Assert.Throws<InvalidDataException>(() => SampleAudioReader.Read(path));
        Assert.Equal(unchanged, File.ReadAllBytes(path));
    }

    [Fact]
    public void MissingWholePacketIsRejectedWhenTagDeclaresItStillExists()
    {
        var path = WriteTagged("missing-frame.mp3", 44100, 1, 44117, variableBitrate: true);
        var bytes = File.ReadAllBytes(path);
        var last = ScanFixturePackets(bytes)[^1];
        File.WriteAllBytes(path, bytes[..last.Offset]);

        Assert.Throws<InvalidDataException>(() => SampleAudioReader.Read(path));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DishonestXingFrameOrByteCountsAreRejected(bool alterByteCount)
    {
        var path = WriteTagged("dishonest-count.mp3", 44100, 2, 44117, variableBitrate: true);
        var bytes = File.ReadAllBytes(path);
        var firstPacket = ScanFixturePackets(bytes)[0];
        var xing = FindXingOffset(bytes.AsSpan(firstPacket.Offset, firstPacket.Length)) + firstPacket.Offset;
        var flags = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(xing + 4, 4));
        Assert.Equal(3u, flags & 3u);
        var field = xing + (alterByteCount ? 12 : 8);
        var original = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(field, 4));
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(field, 4), original + 1);
        File.WriteAllBytes(path, bytes);

        Assert.Throws<InvalidDataException>(() => SampleAudioReader.Read(path));
    }

    [Fact]
    public void Mp3CancellationOccursBeforeDecodeAndLeavesBytesUntouched()
    {
        var path = WriteUntagged("cancel.mp3", 1, 44117, variableBitrate: false);
        var bytes = File.ReadAllBytes(path);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() => SampleAudioReader.Read(path, cancellation.Token));
        Assert.Equal(bytes, File.ReadAllBytes(path));
    }

    [Fact]
    public void Mp3PacketCountBeyondDecodedBudgetIsRejectedWithoutTrustingLengthEstimate()
    {
        var seed = WriteUntagged("budget-seed.mp3", 2, 4410, variableBitrate: false);
        var bytes = File.ReadAllBytes(seed);
        var first = ScanFixturePackets(bytes)[0];
        var packet = bytes.AsSpan(first.Offset, first.Length).ToArray();
        // The first encoder packet has no predecessor reservoir and can be repeated independently.
        var sideInfo = (packet[1] & 1) == 0 ? 6 : 4;
        Assert.Equal(0, (packet[sideInfo] << 1) | (packet[sideInfo + 1] >> 7));
        var count = SampleAudioReader.MaximumDecodedSamples / (first.PcmFrames * 2) + 1;
        var path = Path.Combine(root, "over-budget.mp3");
        using (var output = File.Create(path))
            for (var index = 0; index < count; index++) output.Write(packet);

        var error = Assert.Throws<InvalidDataException>(() => SampleAudioReader.Read(path));
        Assert.Contains("64 MiB", error.Message);
    }

    private string WriteUntagged(string name, int channels, int frames, bool variableBitrate,
        SignalProfile profile = SignalProfile.Tone)
    {
        var path = WriteTagged(name, 44100, channels, frames, variableBitrate, profile);
        var bytes = File.ReadAllBytes(path);
        var packets = ScanFixturePackets(bytes);
        _ = FindXingOffset(bytes.AsSpan(packets[0].Offset, packets[0].Length));
        Assert.Equal(0, packets[0].Offset);
        // Remove only the metadata packet. All encoded audio packets remain byte-for-byte intact.
        File.WriteAllBytes(path, bytes[packets[0].Length..]);
        return path;
    }

    private string WriteTagged(string name, int sampleRate, int channels, int frames, bool variableBitrate,
        SignalProfile profile = SignalProfile.Tone)
    {
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, name);
        using var codec = new FixtureCodec();
        var info = new SndInfo { SampleRate = sampleRate, Channels = channels, Format = 0x230082 };
        var file = codec.Open(path, 0x20, ref info);
        Assert.NotEqual(nint.Zero, file);
        try
        {
            var compression = variableBitrate ? 0.2 : 2.0 / 9;
            Assert.Equal(1, codec.CommandDouble(file, 0x1301, ref compression, sizeof(double)));
            var bitrateMode = variableBitrate ? 2 : 0;
            Assert.Equal(1, codec.CommandInt(file, 0x1305, ref bitrateMode, sizeof(int)));
            var samples = new float[frames * channels];
            uint random = 0x41c64e6d;
            for (var frame = 0; frame < frames; frame++)
            for (var channel = 0; channel < channels; channel++)
            {
                random ^= random << 13;
                random ^= random >> 17;
                random ^= random << 5;
                var noise = ((random >> 8) / 16777216.0 - 0.5) * 0.5;
                var tone = 0.3 * Math.Sin(2 * Math.PI * (channel == 0 ? 440 : 661) * frame / sampleRate);
                samples[frame * channels + channel] = (float)(profile switch
                {
                    SignalProfile.ComplexThenQuiet => frame < sampleRate / 3 ? noise : tone * 0.01,
                    SignalProfile.QuietThenComplex => frame < sampleRate / 3 ? 0 : noise,
                    _ => tone
                });
            }
            Assert.Equal(frames, codec.Write(file, samples, frames));
        }
        finally { Assert.Equal(0, codec.Close(file)); }
        return path;
    }

    private static long LegacyFrameEstimate(string path)
    {
        using var codec = new FixtureCodec();
        var info = new SndInfo();
        var file = codec.Open(path, 0x10, ref info);
        Assert.NotEqual(nint.Zero, file);
        try { return info.Frames; }
        finally { Assert.Equal(0, codec.Close(file)); }
    }

    private static List<FixturePacket> ScanFixturePackets(byte[] bytes)
    {
        // This intentionally small, independent fixture parser handles our generated MPEG-1 packets.
        int[] bitrates = [0, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320];
        int[] rates = [44100, 48000, 32000];
        var packets = new List<FixturePacket>();
        for (var offset = 0; offset < bytes.Length;)
        {
            Assert.True(bytes.Length - offset >= 4, "The generated fixture ends inside a packet header.");
            var header = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(offset, 4));
            Assert.Equal(0xffe00000u, header & 0xffe00000u);
            Assert.Equal(3u, (header >> 19) & 3u);
            Assert.Equal(1u, (header >> 17) & 3u);
            var bitrateIndex = (int)((header >> 12) & 15u);
            var rateIndex = (int)((header >> 10) & 3u);
            Assert.InRange(bitrateIndex, 1, 14);
            Assert.InRange(rateIndex, 0, 2);
            var padding = ((header >> 9) & 1u) != 0;
            var length = 144000 * bitrates[bitrateIndex] / rates[rateIndex] + (padding ? 1 : 0);
            Assert.True(length <= bytes.Length - offset, "The generated fixture ends inside an audio packet.");
            packets.Add(new(offset, length, 1152, padding));
            offset += length;
        }
        Assert.NotEmpty(packets);
        return packets;
    }

    private static int FindXingOffset(ReadOnlySpan<byte> firstPacket)
    {
        var offset = firstPacket.IndexOf("Xing"u8);
        if (offset < 0) offset = firstPacket.IndexOf("Info"u8);
        Assert.True(offset >= 0, "The pinned encoder must produce an intact Xing/Info fixture packet.");
        return offset;
    }

    private static byte[] BuildId3Tags(byte[] audio)
    {
        var title = Encoding.ASCII.GetBytes("Synthetic MP3 import fixture");
        var bodySize = 11 + title.Length;
        var header = new byte[10 + bodySize];
        "ID3"u8.CopyTo(header);
        header[3] = 3;
        header[6] = (byte)((bodySize >> 21) & 127);
        header[7] = (byte)((bodySize >> 14) & 127);
        header[8] = (byte)((bodySize >> 7) & 127);
        header[9] = (byte)(bodySize & 127);
        "TIT2"u8.CopyTo(header.AsSpan(10));
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(14, 4), (uint)(title.Length + 1));
        title.CopyTo(header.AsSpan(21));
        var trailer = new byte[128];
        "TAG"u8.CopyTo(trailer);
        "Legacy title"u8.CopyTo(trailer.AsSpan(3));
        "Kept original metadata"u8.CopyTo(trailer.AsSpan(97));
        return [.. header, .. audio, .. trailer];
    }

    private enum SignalProfile { Tone, ComplexThenQuiet, QuietThenComplex }
    private sealed record FixturePacket(int Offset, int Length, int PcmFrames, bool Padding);
    [StructLayout(LayoutKind.Sequential)] private struct SndInfo { public long Frames; public int SampleRate, Channels, Format, Sections, Seekable; }
    [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Unicode)] private delegate nint OpenDelegate([MarshalAs(UnmanagedType.LPWStr)] string path, int mode, ref SndInfo info);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate long WriteDelegate(nint file, float[] samples, long frames);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int CloseDelegate(nint file);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int CommandDoubleDelegate(nint file, int command, ref double data, int size);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int CommandIntDelegate(nint file, int command, ref int data, int size);

    private sealed class FixtureCodec : IDisposable
    {
        private readonly nint library = NativeLibrary.Load(Path.Combine(AppContext.BaseDirectory, "sndfile.dll"));
        public OpenDelegate Open => Bind<OpenDelegate>("sf_wchar_open");
        public WriteDelegate Write => Bind<WriteDelegate>("sf_writef_float");
        public CloseDelegate Close => Bind<CloseDelegate>("sf_close");
        public CommandDoubleDelegate CommandDouble => Bind<CommandDoubleDelegate>("sf_command");
        public CommandIntDelegate CommandInt => Bind<CommandIntDelegate>("sf_command");
        private T Bind<T>(string name) where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(library, name));
        public void Dispose() => NativeLibrary.Free(library);
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
}
