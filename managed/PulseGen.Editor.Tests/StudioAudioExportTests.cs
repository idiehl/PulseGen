using PulseGen.Contracts;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace PulseGen.Editor.Tests;

public sealed class StudioAudioExportTests
{
    [Fact]
    public void KWeightedGatedMeasurementRecognizesCalibratedProgrammeTone()
    {
        const int sampleRate = 48000;
        const int seconds = 5;
        var samples = new float[sampleRate * seconds * 2];
        var amplitude = Math.Sqrt(2) * Math.Pow(10, -23d / 20);
        for (var frame = 0; frame < sampleRate * seconds; frame++)
        {
            samples[frame * 2] = (float)(amplitude * Math.Sin(2 * Math.PI * 1000 * frame / sampleRate));
        }

        var metrics = StudioAudioAnalyzer.Analyze(new FloatWaveBuffer(sampleRate, 2, samples));

        Assert.NotNull(metrics.IntegratedLoudnessLufs);
        Assert.InRange(metrics.IntegratedLoudnessLufs!.Value, -23.15, -22.85);
        Assert.NotNull(metrics.LoudnessRangeLu);
        Assert.InRange(metrics.LoudnessRangeLu!.Value, 0, 0.05);
        Assert.NotNull(metrics.MaximumMomentaryLoudnessLufs);
        Assert.NotNull(metrics.MaximumShortTermLoudnessLufs);
    }

    [Fact]
    public void OversampledTruePeakFindsPeakBetweenStoredSamples()
    {
        const int sampleRate = 48000;
        var samples = new float[sampleRate / 5 * 2];
        for (var frame = 0; frame < samples.Length / 2; frame++)
        {
            var value = (float)Math.Sin((2 * Math.PI * 12000 * frame / sampleRate) + (Math.PI / 4));
            samples[frame * 2] = value;
            samples[(frame * 2) + 1] = value;
        }

        var metrics = StudioAudioAnalyzer.Analyze(new FloatWaveBuffer(sampleRate, 2, samples));

        Assert.InRange(metrics.SamplePeakLinear, 0.70, 0.72);
        Assert.True(metrics.TruePeakLinear > 0.95);
        Assert.True(metrics.TruePeakDbTp > metrics.SamplePeakDbFs);
    }

    [Fact]
    public void FloatWaveCarriesFactChunkAndRoundTripsWithoutNormalization()
    {
        var root = TemporaryDirectory();
        try
        {
            var path = Path.Combine(root, "master.wav");
            var buffer = TestBuffer(overload: true);
            var result = StudioMasterExporter.Export(
                Request(path, StudioExportContainer.Wave, StudioSampleEncoding.Float32, DitherMode.None),
                buffer);

            var bytes = File.ReadAllBytes(path);
            Assert.Equal("RIFF", Ascii(bytes, 0));
            Assert.Equal("WAVE", Ascii(bytes, 8));
            Assert.Equal((ushort)3, BinaryPrimitives.ReadUInt16LittleEndian(FindRiffChunk(bytes, "fmt ")));
            Assert.Equal((uint)buffer.Frames, BinaryPrimitives.ReadUInt32LittleEndian(FindRiffChunk(bytes, "fact")));
            Assert.Equal(buffer.InterleavedSamples, FloatWaveFile.Read(path).InterleavedSamples);
            Assert.True(result.Metrics.SamplePeakLinear > 1);

            using var receipt = JsonDocument.Parse(File.ReadAllText(result.ReceiptPath));
            Assert.False(receipt.RootElement.GetProperty("normalizationApplied").GetBoolean());
            Assert.Equal(result.Sha256, receipt.RootElement.GetProperty("outputSha256").GetString());
            Assert.Equal("post-mix/pre-encoding", receipt.RootElement.GetProperty("measurementStage").GetString());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData(0, 16)]
    [InlineData(1, 24)]
    [InlineData(2, 32)]
    public void BroadcastWaveEmbedsVersionTwoProvenanceAndExplicitPcmDepth(int encodingValue, int bits)
    {
        var root = TemporaryDirectory();
        try
        {
            var path = Path.Combine(root, $"master-{bits}.wav");
            var result = StudioMasterExporter.Export(
                Request(path, StudioExportContainer.BroadcastWave, (StudioSampleEncoding)encodingValue, DitherMode.Tpdf),
                TestBuffer());
            var bytes = File.ReadAllBytes(path);
            var format = FindRiffChunk(bytes, "fmt ");
            var broadcast = FindRiffChunk(bytes, "bext");

            Assert.Equal((ushort)1, BinaryPrimitives.ReadUInt16LittleEndian(format));
            Assert.Equal((ushort)bits, BinaryPrimitives.ReadUInt16LittleEndian(format[14..]));
            Assert.Equal((ushort)2, BinaryPrimitives.ReadUInt16LittleEndian(broadcast[346..]));
            Assert.NotEqual(short.MaxValue, BinaryPrimitives.ReadInt16LittleEndian(broadcast[416..]));
            Assert.Contains("normalization none", System.Text.Encoding.ASCII.GetString(broadcast[..256]), StringComparison.Ordinal);
            Assert.Equal(result.Sha256, Convert.ToHexStringLower(SHA256.HashData(bytes)));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Rf64UsesDs64AndSentinelSizesEvenForACompactMaster()
    {
        var root = TemporaryDirectory();
        try
        {
            var path = Path.Combine(root, "master.wav");
            var buffer = TestBuffer();
            StudioMasterExporter.Export(
                Request(path, StudioExportContainer.Rf64BroadcastWave, StudioSampleEncoding.Pcm24, DitherMode.None),
                buffer);
            var bytes = File.ReadAllBytes(path);

            Assert.Equal("RF64", Ascii(bytes, 0));
            Assert.Equal(uint.MaxValue, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4, 4)));
            Assert.Equal("ds64", Ascii(bytes, 12));
            Assert.Equal((ulong)(bytes.Length - 8), BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(20, 8)));
            Assert.Equal((ulong)(buffer.InterleavedSamples.Length * 3), BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(28, 8)));
            Assert.Equal((ulong)buffer.Frames, BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(36, 8)));
            var dataHeader = FindChunkHeader(bytes, "data", 12);
            Assert.Equal(uint.MaxValue, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(dataHeader + 4, 4)));
            Assert.True(FindChunkHeader(bytes, "bext", 12) > 0);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void AiffWritesBigEndianPcmAndExactExtendedSampleRate()
    {
        var root = TemporaryDirectory();
        try
        {
            var path = Path.Combine(root, "master.aiff");
            var buffer = TestBuffer();
            StudioMasterExporter.Export(
                Request(path, StudioExportContainer.Aiff, StudioSampleEncoding.Pcm24, DitherMode.None),
                buffer);
            var bytes = File.ReadAllBytes(path);
            var common = FindAiffChunk(bytes, "COMM");
            var sound = FindAiffChunk(bytes, "SSND");

            Assert.Equal("FORM", Ascii(bytes, 0));
            Assert.Equal("AIFF", Ascii(bytes, 8));
            Assert.Equal((ushort)2, BinaryPrimitives.ReadUInt16BigEndian(common));
            Assert.Equal((uint)buffer.Frames, BinaryPrimitives.ReadUInt32BigEndian(common[2..]));
            Assert.Equal((ushort)24, BinaryPrimitives.ReadUInt16BigEndian(common[6..]));
            Assert.Equal(48000, ReadExtendedSampleRate(common[8..18]));
            Assert.Equal(0u, BinaryPrimitives.ReadUInt32BigEndian(sound));
            Assert.Equal(0u, BinaryPrimitives.ReadUInt32BigEndian(sound[4..]));
            Assert.InRange(ReadPcm24BigEndian(sound[8..11]) / 8388608f, buffer.InterleavedSamples[0] - 0.000001f, buffer.InterleavedSamples[0] + 0.000001f);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void FixedDitherSeedProducesBitIdenticalAudioAndVerifiableReceipt()
    {
        var root = TemporaryDirectory();
        try
        {
            var firstPath = Path.Combine(root, "first.wav");
            var secondPath = Path.Combine(root, "second.wav");
            var buffer = TestBuffer();
            var first = StudioMasterExporter.Export(
                Request(firstPath, StudioExportContainer.BroadcastWave, StudioSampleEncoding.Pcm24, DitherMode.Tpdf),
                buffer);
            var second = StudioMasterExporter.Export(
                Request(secondPath, StudioExportContainer.BroadcastWave, StudioSampleEncoding.Pcm24, DitherMode.Tpdf),
                buffer);

            Assert.Equal(File.ReadAllBytes(firstPath), File.ReadAllBytes(secondPath));
            Assert.Equal(first.Sha256, second.Sha256);
            Assert.Equal(first.SizeBytes, new FileInfo(firstPath).Length);
            Assert.True(File.Exists(first.ReceiptPath));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void UnsafeOrMislabeledExportChoicesAreRejectedBeforeWriting()
    {
        var root = TemporaryDirectory();
        try
        {
            var overloaded = TestBuffer(overload: true);
            var ordinary = TestBuffer();
            Assert.Throws<InvalidOperationException>(() => StudioMasterExporter.Export(
                Request(Path.Combine(root, "clipped.wav"), StudioExportContainer.BroadcastWave, StudioSampleEncoding.Pcm24, DitherMode.Tpdf),
                overloaded));
            Assert.Throws<ArgumentException>(() => StudioMasterExporter.Export(
                Request(Path.Combine(root, "float.wav"), StudioExportContainer.BroadcastWave, StudioSampleEncoding.Float32, DitherMode.None),
                ordinary));
            Assert.Throws<ArgumentException>(() => StudioMasterExporter.Export(
                Request(Path.Combine(root, "dither.wav"), StudioExportContainer.Wave, StudioSampleEncoding.Float32, DitherMode.Tpdf),
                ordinary));
            Assert.Throws<ArgumentException>(() => StudioMasterExporter.Export(
                Request(Path.Combine(root, "wrong.aiff"), StudioExportContainer.Wave, StudioSampleEncoding.Pcm24, DitherMode.None),
                ordinary));
            Assert.Empty(Directory.EnumerateFiles(root));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task VerifiedMasterAndReceiptEnterTheProjectAsImmutableAssets()
    {
        var root = TemporaryDirectory();
        var project = Path.Combine(root, "studio-import.pulseproj");
        try
        {
            var scene = SampleScenes.CreateFlyby();
            await ProjectStore.SaveAsync(scene, project);
            var request = Request(
                Path.Combine(root, "master.wav"),
                StudioExportContainer.BroadcastWave,
                StudioSampleEncoding.Pcm24,
                DitherMode.Tpdf) with
            {
                ProjectId = scene.ProjectId,
                ProjectName = scene.Name,
                SceneRevisionSha256 = ProjectStore.ComputeSceneRevisionSha256(scene)
            };
            var exported = StudioMasterExporter.Export(request, TestBuffer());

            var imported = await StudioExportProjectImporter.ImportAsync(scene, project, request, exported);
            var reopened = await ProjectStore.LoadAsync(project);

            Assert.Equal(2, imported.Scene.Assets.Count);
            Assert.Equal(2, reopened.Assets.Count);
            Assert.Equal("application/vnd.pulsegen.studio-export-receipt+json", imported.ReceiptAsset.MediaType);
            Assert.Equal(2, imported.AudioAsset.Channels);
            Assert.Equal(48000, imported.AudioAsset.SampleRate);
            Assert.Equal(exported.Sha256, imported.AudioAsset.Sha256);
            Assert.Equal(
                exported.Sha256,
                await ProjectStore.ComputeSha256Async(ProjectStore.ResolveAssetPath(project, imported.AudioAsset.Sha256)));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ChangedExternalMasterIsRejectedWithoutManifestMutation()
    {
        var root = TemporaryDirectory();
        var project = Path.Combine(root, "studio-tamper.pulseproj");
        try
        {
            var scene = SampleScenes.CreateFlyby();
            await ProjectStore.SaveAsync(scene, project);
            var manifestBefore = await ProjectStore.ComputeSha256Async(ProjectStore.ResolveManifestPath(project));
            var request = Request(
                Path.Combine(root, "master.wav"),
                StudioExportContainer.Wave,
                StudioSampleEncoding.Float32,
                DitherMode.None) with
            {
                ProjectId = scene.ProjectId,
                ProjectName = scene.Name,
                SceneRevisionSha256 = ProjectStore.ComputeSceneRevisionSha256(scene)
            };
            var exported = StudioMasterExporter.Export(request, TestBuffer());
            await File.AppendAllTextAsync(exported.OutputPath, "tampered");

            var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
                StudioExportProjectImporter.ImportAsync(scene, project, request, exported));

            Assert.Contains("changed", error.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(manifestBefore, await ProjectStore.ComputeSha256Async(ProjectStore.ResolveManifestPath(project)));
            Assert.Empty((await ProjectStore.LoadAsync(project)).Assets);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ChangedReceiptProvenanceIsRejectedWithoutManifestMutation()
    {
        var root = TemporaryDirectory();
        var project = Path.Combine(root, "studio-receipt-tamper.pulseproj");
        try
        {
            var scene = SampleScenes.CreateFlyby();
            await ProjectStore.SaveAsync(scene, project);
            var manifestBefore = await ProjectStore.ComputeSha256Async(ProjectStore.ResolveManifestPath(project));
            var request = Request(
                Path.Combine(root, "master.wav"),
                StudioExportContainer.BroadcastWave,
                StudioSampleEncoding.Pcm24,
                DitherMode.Tpdf) with
            {
                ProjectId = scene.ProjectId,
                ProjectName = scene.Name,
                SceneRevisionSha256 = ProjectStore.ComputeSceneRevisionSha256(scene)
            };
            var exported = StudioMasterExporter.Export(request, TestBuffer());
            var receipt = JsonNode.Parse(await File.ReadAllTextAsync(exported.ReceiptPath))!;
            receipt["ditherSeed"] = 1;
            await File.WriteAllTextAsync(exported.ReceiptPath, receipt.ToJsonString());

            var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
                StudioExportProjectImporter.ImportAsync(scene, project, request, exported));

            Assert.Contains("receipt", error.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(manifestBefore, await ProjectStore.ComputeSha256Async(ProjectStore.ResolveManifestPath(project)));
            Assert.Empty((await ProjectStore.LoadAsync(project)).Assets);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static StudioExportRequest Request(
        string path,
        StudioExportContainer container,
        StudioSampleEncoding encoding,
        DitherMode dither) => new()
        {
            DestinationPath = path,
            Container = container,
            Encoding = encoding,
            Dither = dither,
            DitherSeed = 0x123456789ABCDEF0,
            ProjectId = Guid.Parse("66f97d3b-f6d5-4e66-858c-caf203f5abc1"),
            ProjectName = "Export verification",
            SceneRevisionSha256 = new string('a', 64),
            TakeIds = [Guid.Parse("165f78f1-347f-45c2-8396-51dc4ade5811")],
            CreatedAtUtc = new DateTimeOffset(2026, 8, 30, 12, 34, 56, TimeSpan.Zero),
            EngineVersion = "studio-export-tests"
        };

    private static FloatWaveBuffer TestBuffer(bool overload = false)
    {
        const int frames = 4800;
        var samples = new float[frames * 2];
        for (var frame = 0; frame < frames; frame++)
        {
            samples[frame * 2] = (float)(0.31 * Math.Sin(frame * 0.17));
            samples[(frame * 2) + 1] = (float)(0.27 * Math.Cos(frame * 0.13));
        }
        if (overload)
        {
            samples[37] = 1.125f;
        }
        return new FloatWaveBuffer(48000, 2, samples);
    }

    private static string TemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"pulsegen-studio-export-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static ReadOnlySpan<byte> FindRiffChunk(byte[] bytes, string id)
    {
        var header = FindChunkHeader(bytes, id, 12);
        var size = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(header + 4, 4)));
        return bytes.AsSpan(header + 8, size);
    }

    private static int FindChunkHeader(byte[] bytes, string id, int start)
    {
        for (var cursor = start; cursor <= bytes.Length - 8;)
        {
            if (Ascii(bytes, cursor) == id)
            {
                return cursor;
            }
            var size = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(cursor + 4, 4));
            if (size == uint.MaxValue)
            {
                throw new InvalidDataException($"Cannot skip sentinel-sized chunk while finding '{id}'.");
            }
            cursor = checked(cursor + 8 + (int)size + ((int)size & 1));
        }
        throw new InvalidDataException($"Chunk '{id}' was not found.");
    }

    private static ReadOnlySpan<byte> FindAiffChunk(byte[] bytes, string id)
    {
        for (var cursor = 12; cursor <= bytes.Length - 8;)
        {
            var size = checked((int)BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(cursor + 4, 4)));
            if (Ascii(bytes, cursor) == id)
            {
                return bytes.AsSpan(cursor + 8, size);
            }
            cursor = checked(cursor + 8 + size + (size & 1));
        }
        throw new InvalidDataException($"AIFF chunk '{id}' was not found.");
    }

    private static string Ascii(byte[] bytes, int offset) => System.Text.Encoding.ASCII.GetString(bytes, offset, 4);

    private static int ReadExtendedSampleRate(ReadOnlySpan<byte> value)
    {
        var exponent = (BinaryPrimitives.ReadUInt16BigEndian(value) & 0x7FFF) - 16383;
        var mantissa = BinaryPrimitives.ReadUInt64BigEndian(value[2..]);
        return checked((int)Math.Round(mantissa * Math.Pow(2, exponent - 63)));
    }

    private static int ReadPcm24BigEndian(ReadOnlySpan<byte> value)
    {
        var result = (value[0] << 16) | (value[1] << 8) | value[2];
        return (result & 0x800000) == 0 ? result : result | unchecked((int)0xFF000000);
    }
}
