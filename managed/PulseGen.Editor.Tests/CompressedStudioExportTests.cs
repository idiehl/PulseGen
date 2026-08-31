using System.Security.Cryptography;
using System.Text.Json;
using PulseGen.Contracts;
using Xunit;

namespace PulseGen.Editor.Tests;

public sealed class CompressedStudioExportTests
{
    [Fact]
    public void CodecRuntimeIsThePinnedOfficialBinary()
    {
        var runtime = Path.Combine(AppContext.BaseDirectory, "sndfile.dll");

        Assert.True(File.Exists(runtime));
        Assert.True(SndFileCodecAdapter.VerifyRuntimeFile(runtime));
        Assert.Equal(SndFileCodecAdapter.ExpectedBinarySha256, Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(runtime))));
        Assert.Contains("1.2.2", SndFileCodecAdapter.RuntimeVersion, StringComparison.Ordinal);

        var changed = Path.Combine(Path.GetTempPath(), $"pulsegen-sndfile-tamper-{Guid.NewGuid():N}.dll");
        try
        {
            File.Copy(runtime, changed);
            using (var stream = new FileStream(changed, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                stream.Position = 128;
                var original = stream.ReadByte();
                Assert.NotEqual(-1, original);
                stream.Position = 128;
                stream.WriteByte((byte)(original ^ 0xff));
            }
            Assert.False(SndFileCodecAdapter.VerifyRuntimeFile(changed));
        }
        finally
        {
            File.Delete(changed);
        }
    }

    [Theory]
    [InlineData(4, 1, ".flac", false, 24)]
    [InlineData(5, 4, ".mp3", true, null)]
    [InlineData(6, 4, ".ogg", true, null)]
    [InlineData(7, 4, ".opus", true, null)]
    public void EveryCompressedFormatReopensAsAlignedStereoWithProvenance(
        int containerValue,
        int encodingValue,
        string extension,
        bool lossy,
        int? bits)
    {
        var container = (StudioExportContainer)containerValue;
        var encoding = (StudioSampleEncoding)encodingValue;
        var root = TemporaryDirectory();
        try
        {
            var path = Path.Combine(root, "delivery" + extension);
            var source = TestBuffer(48000);
            var result = StudioMasterExporter.Export(Request(path, container, encoding), source);
            var reopened = SndFileCodecAdapter.Read(path);

            Assert.Equal(container, reopened.Container);
            Assert.Equal(2, reopened.Buffer.Channels);
            Assert.Equal(48000, reopened.Buffer.SampleRate);
            Assert.InRange(reopened.Buffer.Frames, source.Frames - 1, source.Frames + 2304);
            Assert.Equal(bits, reopened.BitsPerSample);
            Assert.Equal("Compressed export verification", reopened.Title);
            Assert.Contains(container == StudioExportContainer.Mp3 ? "LAME" : "PulseGen", reopened.Software, StringComparison.Ordinal);
            Assert.Contains("normalization=none", reopened.Comment, StringComparison.Ordinal);
            Assert.Equal(lossy, result.Lossy);
            Assert.Equal("libsndfile", result.EncoderName);
            Assert.Contains("1.2.2", result.EncoderVersion, StringComparison.Ordinal);
            Assert.Null(result.SampleRateConversion);
            Assert.Equal(bits, result.BitsPerSample);
            AssertChannelIdentity(source, reopened.Buffer);

            using var receipt = JsonDocument.Parse(File.ReadAllText(result.ReceiptPath));
            Assert.Equal(2, receipt.RootElement.GetProperty("receiptVersion").GetInt32());
            Assert.Equal(lossy, receipt.RootElement.GetProperty("lossy").GetBoolean());
            Assert.Equal("libsndfile", receipt.RootElement.GetProperty("encoderName").GetString());
            Assert.False(receipt.RootElement.GetProperty("normalizationApplied").GetBoolean());
            Assert.Equal(48000, receipt.RootElement.GetProperty("measurementSourceSampleRate").GetInt32());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData(4, 1, ".flac")]
    [InlineData(5, 4, ".mp3")]
    [InlineData(6, 4, ".ogg")]
    [InlineData(7, 4, ".opus")]
    public void FixedRequestProducesBitStableCompressedFiles(
        int containerValue,
        int encodingValue,
        string extension)
    {
        var container = (StudioExportContainer)containerValue;
        var encoding = (StudioSampleEncoding)encodingValue;
        var root = TemporaryDirectory();
        try
        {
            var firstPath = Path.Combine(root, "first" + extension);
            var secondPath = Path.Combine(root, "second" + extension);
            var source = TestBuffer(48000);
            var first = StudioMasterExporter.Export(Request(firstPath, container, encoding), source);
            var second = StudioMasterExporter.Export(Request(secondPath, container, encoding), source);

            Assert.Equal(first.Sha256, second.Sha256);
            Assert.Equal(File.ReadAllBytes(firstPath), File.ReadAllBytes(secondPath));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void FlacRoundTripsTheChosenPcmWordsExactly()
    {
        var root = TemporaryDirectory();
        try
        {
            var path = Path.Combine(root, "exact.flac");
            var source = TestBuffer(48000);
            var result = StudioMasterExporter.Export(
                Request(path, StudioExportContainer.Flac, StudioSampleEncoding.Pcm24) with { Dither = DitherMode.None },
                source);
            var decoded = SndFileCodecAdapter.Read(path).Buffer;

            Assert.Equal(source.InterleavedSamples.Length, decoded.InterleavedSamples.Length);
            for (var index = 0; index < source.InterleavedSamples.Length; index++)
            {
                var expectedWord = Math.Clamp(
                    Math.Round(source.InterleavedSamples[index] * 8388608d, MidpointRounding.AwayFromZero),
                    -8388608,
                    8388607);
                Assert.Equal((float)(expectedWord / 8388608d), decoded.InterleavedSamples[index]);
            }
            Assert.False(result.Lossy);
            Assert.Equal(0, result.QuantizerSaturationCount);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData(5, 5, 96000, 48000, ".mp3")]
    [InlineData(7, 5, 44100, 48000, ".opus")]
    public void DeliveryRateConversionIsExplicitAndDurationPreserving(
        int containerValue,
        int encodingValue,
        int sourceRate,
        int targetRate,
        string extension)
    {
        var container = (StudioExportContainer)containerValue;
        var encoding = (StudioSampleEncoding)encodingValue;
        var root = TemporaryDirectory();
        try
        {
            var source = TestBuffer(sourceRate);
            var result = StudioMasterExporter.Export(
                Request(Path.Combine(root, "converted" + extension), container, encoding),
                source);
            var decoded = SndFileCodecAdapter.Read(result.OutputPath).Buffer;

            Assert.Equal(sourceRate, result.MeasurementSourceSampleRate);
            Assert.Equal(targetRate, result.SampleRate);
            Assert.Equal(targetRate, decoded.SampleRate);
            Assert.NotNull(result.SampleRateConversion);
            Assert.Contains($"{sourceRate} Hz -> {targetRate} Hz", result.SampleRateConversion, StringComparison.Ordinal);
            Assert.InRange(Math.Abs(decoded.DurationSeconds - source.DurationSeconds), 0, 0.03);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void OfflineRateConversionPreservesThePassbandAndRejectsAnAliasingTone()
    {
        const int sourceRate = 96000;
        const int targetRate = 48000;
        var samples = new float[sourceRate / 5 * 2];
        for (var frame = 0; frame < samples.Length / 2; frame++)
        {
            var value = 0.45 * Math.Sin(2 * Math.PI * 1000 * frame / sourceRate) +
                0.45 * Math.Sin(2 * Math.PI * 30000 * frame / sourceRate);
            samples[frame * 2] = (float)value;
            samples[(frame * 2) + 1] = (float)value;
        }

        var converted = OfflineSampleRateConverter.Convert(new FloatWaveBuffer(sourceRate, 2, samples), targetRate, CancellationToken.None);
        var passband = ToneAmplitude(converted, 1000);
        var aliased = ToneAmplitude(converted, 18000);

        Assert.InRange(passband, 0.43, 0.47);
        Assert.InRange(aliased, 0, 0.015);
    }

    [Theory]
    [InlineData(5, ".mp3")]
    [InlineData(6, ".ogg")]
    [InlineData(7, ".opus")]
    public void HighQualityDeliveryPresetRetainsMoreDataThanCompactPreset(int containerValue, string extension)
    {
        var root = TemporaryDirectory();
        try
        {
            var container = (StudioExportContainer)containerValue;
            var source = TestBuffer(48000, 2);
            var high = StudioMasterExporter.Export(
                Request(Path.Combine(root, "high" + extension), container, StudioSampleEncoding.LossyHigh),
                source);
            var compact = StudioMasterExporter.Export(
                Request(Path.Combine(root, "compact" + extension), container, StudioSampleEncoding.LossyCompact),
                source);

            Assert.True(high.SizeBytes > compact.SizeBytes, $"{container} high-quality output was not larger than compact output.");
            Assert.NotEqual(high.EncoderSettings, compact.EncoderSettings);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void InvalidCompressedChoicesAndCancellationLeaveNoArtifacts()
    {
        var root = TemporaryDirectory();
        try
        {
            var source = TestBuffer(48000);
            Assert.Throws<ArgumentException>(() => StudioMasterExporter.Export(
                Request(Path.Combine(root, "wrong.flac"), StudioExportContainer.Flac, StudioSampleEncoding.LossyHigh),
                source));
            Assert.Throws<ArgumentException>(() => StudioMasterExporter.Export(
                Request(Path.Combine(root, "dithered.mp3"), StudioExportContainer.Mp3, StudioSampleEncoding.LossyHigh) with { Dither = DitherMode.Tpdf },
                source));

            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            Assert.Throws<OperationCanceledException>(() => StudioMasterExporter.Export(
                Request(Path.Combine(root, "cancelled.opus"), StudioExportContainer.Opus, StudioSampleEncoding.LossyHigh),
                source,
                cancellation.Token));
            Assert.Empty(Directory.EnumerateFiles(root));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task CompressedMasterAndReceiptEnterProjectAsImmutableAssets()
    {
        var root = TemporaryDirectory();
        var project = Path.Combine(root, "compressed-import.pulseproj");
        try
        {
            var scene = SampleScenes.CreateFlyby();
            await ProjectStore.SaveAsync(scene, project);
            var request = Request(
                Path.Combine(root, "master.opus"),
                StudioExportContainer.Opus,
                StudioSampleEncoding.LossyHigh) with
            {
                ProjectId = scene.ProjectId,
                ProjectName = scene.Name,
                SceneRevisionSha256 = ProjectStore.ComputeSceneRevisionSha256(scene)
            };
            var exported = StudioMasterExporter.Export(request, TestBuffer(48000));

            var imported = await StudioExportProjectImporter.ImportAsync(scene, project, request, exported);

            Assert.Equal("audio/opus", imported.AudioAsset.MediaType);
            Assert.Equal(exported.Sha256, imported.AudioAsset.Sha256);
            Assert.Equal(48000, imported.AudioAsset.SampleRate);
            Assert.Equal(2, imported.AudioAsset.Channels);
            Assert.Equal(2, imported.Scene.Assets.Count);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static StudioExportRequest Request(
        string path,
        StudioExportContainer container,
        StudioSampleEncoding encoding) => new()
        {
            DestinationPath = path,
            Container = container,
            Encoding = encoding,
            Dither = container == StudioExportContainer.Flac ? DitherMode.Tpdf : DitherMode.None,
            DitherSeed = 0xC0DEC0FFEE123456,
            ProjectId = Guid.Parse("a41e8ae7-b102-4a83-a9eb-aeb4ba09ecdc"),
            ProjectName = "Compressed export verification",
            SceneRevisionSha256 = new string('b', 64),
            TakeIds = [Guid.Parse("bdf098b0-2a10-43f3-aaed-584a1c673369")],
            CreatedAtUtc = new DateTimeOffset(2026, 8, 30, 18, 24, 0, TimeSpan.Zero),
            EngineVersion = "compressed-export-tests"
        };

    private static FloatWaveBuffer TestBuffer(int sampleRate, double durationSeconds = 0.2)
    {
        var frames = checked((int)Math.Round(sampleRate * durationSeconds, MidpointRounding.AwayFromZero));
        var samples = new float[frames * 2];
        for (var frame = 0; frame < frames; frame++)
        {
            samples[frame * 2] = (float)(0.37 * Math.Sin(2 * Math.PI * 431 * frame / sampleRate));
            samples[(frame * 2) + 1] = (float)(0.23 * Math.Cos(2 * Math.PI * 997 * frame / sampleRate));
        }
        return new FloatWaveBuffer(sampleRate, 2, samples);
    }

    private static void AssertChannelIdentity(FloatWaveBuffer source, FloatWaveBuffer decoded)
    {
        var frames = Math.Min(source.Frames, decoded.Frames);
        var leftToLeft = 0d;
        var leftToRight = 0d;
        var rightToRight = 0d;
        var rightToLeft = 0d;
        for (var frame = 0; frame < frames; frame++)
        {
            var sourceLeft = source.InterleavedSamples[frame * 2];
            var sourceRight = source.InterleavedSamples[(frame * 2) + 1];
            var decodedLeft = decoded.InterleavedSamples[frame * 2];
            var decodedRight = decoded.InterleavedSamples[(frame * 2) + 1];
            leftToLeft += sourceLeft * decodedLeft;
            leftToRight += sourceRight * decodedLeft;
            rightToRight += sourceRight * decodedRight;
            rightToLeft += sourceLeft * decodedRight;
        }
        Assert.True(leftToLeft > Math.Abs(leftToRight) * 4, "Decoded left channel no longer matches the source left channel.");
        Assert.True(rightToRight > Math.Abs(rightToLeft) * 4, "Decoded right channel no longer matches the source right channel.");
    }

    private static double ToneAmplitude(FloatWaveBuffer buffer, double frequencyHz)
    {
        var start = Math.Min(128, buffer.Frames / 8);
        var end = buffer.Frames - start;
        var sine = 0d;
        var cosine = 0d;
        for (var frame = start; frame < end; frame++)
        {
            var angle = 2 * Math.PI * frequencyHz * frame / buffer.SampleRate;
            var value = buffer.InterleavedSamples[frame * buffer.Channels];
            sine += value * Math.Sin(angle);
            cosine += value * Math.Cos(angle);
        }
        return 2 * Math.Sqrt((sine * sine) + (cosine * cosine)) / (end - start);
    }

    private static string TemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"pulsegen-compressed-export-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }
}
