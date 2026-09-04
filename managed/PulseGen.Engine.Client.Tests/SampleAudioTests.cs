using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using PulseGen.Contracts;
using Xunit;

namespace PulseGen.Engine.Client.Tests;

public sealed class SampleAudioTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"pulsegen-sample-tests-{Guid.NewGuid():N}");

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void FloatWaveRetainsChannelsRateOverloadsAndExactFrameCount(int channels)
    {
        var input = Enumerable.Range(0, 128 * channels).Select(index => index % 2 == 0 ? 1.5f : -0.125f).ToArray();
        var path = WriteWave("source.wav", 24000, channels, input);
        var decoded = SampleAudioReader.Read(path);
        Assert.Equal(24000, decoded.SampleRate);
        Assert.Equal(channels, decoded.Channels);
        Assert.Equal(128, decoded.Frames);
        Assert.Equal(128.0 / 24000, decoded.DurationSeconds);
        Assert.Equal("WAV", decoded.Container);
        Assert.Contains("1.2.2", decoded.DecoderVersion);
        Assert.Equal(input, decoded.Samples);
    }

    [Theory]
    [InlineData(0x020002, "AIFF")]
    [InlineData(0x170002, "FLAC")]
    [InlineData(0x230082, "MP3")]
    [InlineData(0x200060, "Ogg Vorbis")]
    [InlineData(0x200064, "Opus")]
    public void PinnedDecoderAcceptsEachRequiredSourceContainer(int format, string container)
    {
        var path = Path.Combine(root, "encoded.audio");
        Directory.CreateDirectory(root);
        WriteEncoded(path, format);
        var audio = SampleAudioReader.Read(path);
        Assert.Equal(container, audio.Container);
        Assert.Equal(48000, audio.SampleRate);
        Assert.Equal(2, audio.Channels);
        Assert.Equal(4800, audio.Frames);
        Assert.Contains(audio.Samples, sample => Math.Abs(sample) > 0.1f);
        Assert.All(audio.Samples, sample => Assert.True(float.IsFinite(sample)));
    }

    [Fact]
    public void ReaderRejectsTruncatedNonfiniteOrUnsupportedDimensionsAndHonorsCancellation()
    {
        var path = WriteWave("truncated.wav", 48000, 1, [0.1f, 0.2f, 0.3f]);
        using (var stream = File.OpenWrite(path)) stream.SetLength(stream.Length - 4);
        Assert.Throws<InvalidDataException>(() => SampleAudioReader.Read(path));
        Assert.Throws<InvalidDataException>(() => SampleAudioReader.Read(WriteWave("nan.wav", 48000, 1, [float.NaN])));
        Assert.Throws<InvalidDataException>(() => SampleAudioReader.Read(WriteWave("surround.wav", 48000, 3, [0f, 0f, 0f])));
        Assert.Throws<InvalidDataException>(() => SampleAudioReader.Read(WriteWave("slow.wav", 7999, 1, [0.1f])));
        Assert.Throws<OperationCanceledException>(() => SampleAudioReader.Read(path, new CancellationToken(canceled: true)));
    }

    [Fact]
    public void ReaderRejectsDecodedBudgetOverflowBeforeAllocatingAudio()
    {
        var path = WriteWave("too-large.wav", 48000, 1, [0f]);
        var dataBytes = checked((SampleAudioReader.MaximumDecodedSamples + 1) * sizeof(float));
        using (var stream = File.Open(path, FileMode.Open, FileAccess.Write))
        using (var writer = new BinaryWriter(stream))
        {
            stream.SetLength(44L + dataBytes);
            stream.Position = 4;
            writer.Write(36 + dataBytes);
            stream.Position = 40;
            writer.Write(dataBytes);
        }
        var exception = Assert.Throws<InvalidDataException>(() => SampleAudioReader.Read(path));
        Assert.Contains("64 MiB", exception.Message);
    }

    [Fact]
    public void ReaderRejectsExtremeFiniteAmplitudeWithoutNormalizingIt()
    {
        var path = WriteWave("extreme.wav", 48000, 1, [65f]);
        var exception = Assert.Throws<InvalidDataException>(() => SampleAudioReader.Read(path));
        Assert.Contains("no normalization was applied", exception.Message);
        Assert.Equal(65f, BitConverter.ToSingle(File.ReadAllBytes(path), 44));
        Assert.Equal(new[] { -64f, 64f }, SampleAudioReader.Read(WriteWave("headroom.wav", 48000, 1, [-64f, 64f])).Samples);
    }

    [Theory]
    [InlineData(0x020002)]
    [InlineData(0x170002)]
    [InlineData(0x230082)]
    [InlineData(0x200060)]
    [InlineData(0x200064)]
    public void ReaderRejectsIncompleteEncodedSources(int format)
    {
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "incomplete.audio");
        WriteEncoded(path, format);
        using (var stream = File.OpenWrite(path)) stream.SetLength(stream.Length - 31);
        Assert.Throws<InvalidDataException>(() => SampleAudioReader.Read(path));
    }

    [Fact]
    public async Task VerifiedReadRejectsTamperedBytesAndIncorrectMetadata()
    {
        var project = Path.Combine(root, "source.pulseproj");
        var path = WriteWave("verified.wav", 24000, 1, [0.1f, 0.2f]);
        var asset = await Import(project, path);
        Assert.Equal(2, SampleAudioReader.ReadVerified(project, asset).Frames);
        Assert.Throws<InvalidDataException>(() => SampleAudioReader.ReadVerified(project, asset with { SampleRate = 48000 }));
        var stored = ProjectStore.ResolveAssetPath(project, asset.Sha256);
        var bytes = File.ReadAllBytes(stored);
        bytes[^1] ^= 1;
        File.WriteAllBytes(stored, bytes);
        Assert.Throws<InvalidDataException>(() => SampleAudioReader.ReadVerified(project, asset));
    }

    [Fact]
    public async Task StereoBedResamplesWithoutCrossfeedAndPreservesSourceThroughProjectRoundTrip()
    {
        var samples = new float[2400 * 2];
        for (var frame = 0; frame < 2400; frame++) samples[frame * 2] = (float)(0.3 * Math.Sin(2 * Math.PI * 750 * frame / 24000));
        var project = Path.Combine(root, "bed.pulseproj");
        var asset = await Import(project, WriteWave("bed.wav", 24000, 2, samples));
        var scene = CreateScene(asset, SourceChannelMapping.NonSpatialStereoBed);
        var node = scene.Emitters[0].SignalGraph.Nodes[0];
        scene.Emitters[0].SignalGraph.Nodes[0] = node with
        {
            SamplePlayback = node.SamplePlayback! with { StartTimeSeconds = 0.02, OffsetSeconds = 0.01 }
        };
        await ProjectStore.SaveAsync(scene, project);
        var loaded = await ProjectStore.LoadAsync(project);
        Assert.Equal(scene.Emitters[0].SignalGraph.Nodes[0].SamplePlayback, loaded.Emitters[0].SignalGraph.Nodes[0].SamplePlayback);
        var summary = NativeSceneRenderer.Render(loaded, Path.Combine(root, "bed-output"), projectDirectory: project);
        var audio = SampleAudioReader.Read(Directory.GetFiles(Path.Combine(root, "bed-output"), "*.wav").Single());
        Assert.Equal(12000, audio.Frames);
        Assert.All(audio.Samples.Where((_, index) => index % 2 != 0), sample => Assert.Equal(0f, sample));
        Assert.All(audio.Samples.Take(900 * 2), sample => Assert.Equal(0f, sample));
        Assert.All(audio.Samples.Skip(5400 * 2), sample => Assert.Equal(0f, sample));
        Assert.InRange(Rms(audio, 0, 2000, 4000), 0.209, 0.215);
        using var receipt = JsonDocument.Parse(File.ReadAllText(summary.RenderReceiptPath));
        var source = Assert.Single(receipt.RootElement.GetProperty("sampleSources").EnumerateArray());
        Assert.Equal(asset.Sha256, source.GetProperty("assetSha256").GetString());
        Assert.Equal(24000, source.GetProperty("sampleRate").GetInt32());
        Assert.Equal("none", source.GetProperty("normalization").GetString());
        Assert.Equal("Native sample integration fixture", source.GetProperty("provenance").GetString());
        Assert.Equal(samples, SampleAudioReader.ReadVerified(project, asset).Samples);
    }

    [Fact]
    public async Task ExplicitFoldDownCancelsOppositeStereoChannels()
    {
        var samples = new float[4800 * 2];
        for (var frame = 0; frame < 4800; frame++)
        {
            samples[frame * 2] = (float)(0.3 * Math.Sin(2 * Math.PI * 440 * frame / 24000));
            samples[frame * 2 + 1] = -samples[frame * 2];
        }
        var project = Path.Combine(root, "fold.pulseproj");
        var asset = await Import(project, WriteWave("fold.wav", 24000, 2, samples));
        var summary = NativeSceneRenderer.Render(CreateScene(asset, SourceChannelMapping.ExplicitMonoFoldDown), Path.Combine(root, "fold-output"), projectDirectory: project);
        Assert.Equal(0, summary.PeakLeft);
        Assert.Equal(0, summary.PeakRight);
    }

    [Fact]
    public async Task LinkedEmittersRouteTheirOwnChannelsThroughIndependentSpatialPaths()
    {
        var samples = new float[6000 * 2];
        for (var frame = 0; frame < 6000; frame++) samples[frame * 2] = (float)(0.3 * Math.Sin(2 * Math.PI * 440 * frame / 24000));
        var project = Path.Combine(root, "pair.pulseproj");
        var asset = await Import(project, WriteWave("pair.wav", 24000, 2, samples));
        var scene = CreateScene(asset, SourceChannelMapping.LinkedDualEmitters);
        var first = scene.Emitters[0];
        var secondId = Guid.NewGuid();
        var secondPath = scene.MotionPaths[0] with
        {
            Id = Guid.NewGuid(), Name = "Far channel path",
            Keyframes = [new() { TimeSeconds = 0, PositionMetres = new(0, 0, 10) }, new() { TimeSeconds = 0.25, PositionMetres = new(0, 0, 10) }]
        };
        scene.MotionPaths.Add(secondPath);
        scene.Emitters[0] = first with { LinkedRightEmitterId = secondId };
        var rightGraph = SampleGraph(asset, 1);
        scene.Emitters.Add(first with { Id = secondId, Name = "Right sample", MotionPathId = secondPath.Id, LinkedRightEmitterId = first.Id, SignalGraph = rightGraph });
        var near = NativeSceneRenderer.Render(scene, Path.Combine(root, "near"), projectDirectory: project);
        var firstNode = scene.Emitters[0].SignalGraph.Nodes[0];
        scene.Emitters[0].SignalGraph.Nodes[0] = firstNode with { SamplePlayback = firstNode.SamplePlayback! with { ChannelIndex = 1 } };
        var secondNode = rightGraph.Nodes[0];
        rightGraph.Nodes[0] = secondNode with { SamplePlayback = secondNode.SamplePlayback! with { ChannelIndex = 0 } };
        var far = NativeSceneRenderer.Render(scene, Path.Combine(root, "far"), projectDirectory: project);
        Assert.InRange(near.RmsLeft / far.RmsLeft, 9, 12);
        Assert.InRange(near.RmsRight / far.RmsRight, 9, 12);
    }

    private static SignalGraph SampleGraph(AssetRef asset, int channel = 0)
    {
        var source = new SignalNode { Id = Guid.NewGuid(), Name = "Audio file", Kind = SignalNodeKind.Sample, SamplePlayback = new() { AssetSha256 = asset.Sha256, ChannelIndex = channel } };
        var output = new SignalNode { Id = Guid.NewGuid(), Name = "Output", Kind = SignalNodeKind.Output };
        return new() { Nodes = [source, output], Connections = [new() { FromNodeId = source.Id, FromPort = "out", ToNodeId = output.Id, ToPort = "in" }] };
    }

    private static SceneDocument CreateScene(AssetRef asset, SourceChannelMapping mapping)
    {
        var scene = SampleScenes.CreateFlyby();
        scene.Geometry.Clear();
        scene.Assets.Add(asset);
        scene.MotionPaths[0] = scene.MotionPaths[0] with
        {
            Kind = MotionPathKind.Linear,
            Keyframes = [new() { TimeSeconds = 0, PositionMetres = new(0, 0, 1) }, new() { TimeSeconds = 0.25, PositionMetres = new(0, 0, 1) }]
        };
        scene.Emitters[0] = scene.Emitters[0] with { SignalGraph = SampleGraph(asset), ChannelMapping = mapping, GainLinear = 1, MinDistanceMetres = 1 };
        scene.ReceiverRigs.Clear();
        scene.ReceiverRigs.Add(new() { Id = Guid.NewGuid(), Name = "Pair", Kind = ReceiverRigKind.StereoMicrophone, Transform = new(), SpacingMetres = 0.01, PolarPattern = PolarPattern.Omni });
        scene.RenderProfiles[0] = scene.RenderProfiles[0] with { SampleRate = 48000, Quality = RenderQuality.Final, ReflectionOrder = 0, SampleFormat = RenderSampleFormat.Float32, Dither = DitherMode.None };
        return scene with { Timeline = new() { DurationSeconds = 0.25 } };
    }

    private static async Task<AssetRef> Import(string project, string path)
    {
        await ProjectStore.SaveAsync(SampleScenes.CreateFlyby(), project);
        var audio = SampleAudioReader.Read(path);
        var asset = await ProjectStore.ImportAssetAsync(project, path, AssetKind.Audio, "audio/wav", "Native sample integration fixture");
        return asset with { SampleRate = audio.SampleRate, Channels = audio.Channels, DurationSeconds = audio.DurationSeconds };
    }

    private string WriteWave(string name, int sampleRate, int channels, float[] samples)
    {
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, name);
        using var writer = new BinaryWriter(File.Create(path), Encoding.ASCII);
        writer.Write("RIFF"u8); writer.Write(36 + samples.Length * 4); writer.Write("WAVEfmt "u8); writer.Write(16);
        writer.Write((ushort)3); writer.Write((ushort)channels); writer.Write(sampleRate); writer.Write(sampleRate * channels * 4);
        writer.Write((ushort)(channels * 4)); writer.Write((ushort)32); writer.Write("data"u8); writer.Write(samples.Length * 4);
        foreach (var sample in samples) writer.Write(sample);
        return path;
    }

    private static double Rms(DecodedSampleAudio audio, int channel, int startFrame, int endFrame) =>
        Math.Sqrt(Enumerable.Range(startFrame, endFrame - startFrame).Average(frame => Math.Pow(audio.Samples[frame * audio.Channels + channel], 2)));

    private static void WriteEncoded(string path, int format)
    {
        var library = NativeLibrary.Load(Path.Combine(AppContext.BaseDirectory, "sndfile.dll"));
        try
        {
            var open = Marshal.GetDelegateForFunctionPointer<OpenDelegate>(NativeLibrary.GetExport(library, "sf_wchar_open"));
            var write = Marshal.GetDelegateForFunctionPointer<WriteDelegate>(NativeLibrary.GetExport(library, "sf_writef_float"));
            var close = Marshal.GetDelegateForFunctionPointer<CloseDelegate>(NativeLibrary.GetExport(library, "sf_close"));
            var info = new SndInfo { SampleRate = 48000, Channels = 2, Format = format };
            var file = open(path, 0x20, ref info);
            Assert.NotEqual(nint.Zero, file);
            try
            {
                var samples = Enumerable.Range(0, 9600).Select(index => (float)(0.3 * Math.Sin(2 * Math.PI * 440 * (index / 2) / 48000))).ToArray();
                Assert.Equal(4800, write(file, samples, 4800));
            }
            finally { Assert.Equal(0, close(file)); }
        }
        finally { NativeLibrary.Free(library); }
    }

    [StructLayout(LayoutKind.Sequential)] private struct SndInfo { public long Frames; public int SampleRate, Channels, Format, Sections, Seekable; }
    [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Unicode)] private delegate nint OpenDelegate([MarshalAs(UnmanagedType.LPWStr)] string path, int mode, ref SndInfo info);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate long WriteDelegate(nint file, float[] samples, long frames);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int CloseDelegate(nint file);

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
}
