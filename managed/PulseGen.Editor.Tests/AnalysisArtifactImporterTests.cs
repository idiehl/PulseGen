using PulseGen.Contracts;
using PulseGen.ModelHost.Client;
using System.Text.Json;
using Xunit;

namespace PulseGen.Editor.Tests;

public sealed class AnalysisArtifactImporterTests
{
    [Fact]
    public async Task RealModelHostResultPassesTheProjectImportBoundary()
    {
        var root = Path.Combine(Path.GetTempPath(), $"pulsegen-analysis-e2e-{Guid.NewGuid():N}");
        var project = Path.Combine(root, "spectral-test.pulseproj");
        var output = Path.Combine(root, "analysis");
        var source = Path.Combine(root, "Independent-Pair.wav");
        Directory.CreateDirectory(root);
        try
        {
            WriteStereoPcm16(source, 48000, 8192);
            var scene = SampleScenes.CreateFlyby();
            await ProjectStore.SaveAsync(scene, project);
            await using (var host = await ModelHostClient.StartAsync())
            {
                _ = await host.AnalyzeAsync(
                    source,
                    output,
                    ModelHostChannelMode.IndependentChannels,
                    maxPartials: 10);
            }

            var imported = await AnalysisArtifactImporter.ImportAsync(
                scene,
                project,
                output,
                source,
                ModelHostChannelMode.IndependentChannels,
                10);

            Assert.Equal(5, imported.DeclaredAssetCount);
            Assert.Equal(ModelHostChannelMode.IndependentChannels, imported.ChannelMode);
            Assert.True(imported.PartialTrackCount > 0);
            Assert.Equal(5, (await ProjectStore.LoadAsync(project)).Assets.Count);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task VerifiedAnalysisBecomesImmutableProjectAssetsInOneManifestCommit()
    {
        var root = Path.Combine(Path.GetTempPath(), $"pulsegen-analysis-import-{Guid.NewGuid():N}");
        var project = Path.Combine(root, "spectral-test.pulseproj");
        var output = Path.Combine(root, "analysis");
        var source = Path.Combine(root, "Head-Rig.wav");
        Directory.CreateDirectory(root);
        try
        {
            await File.WriteAllBytesAsync(source, [0x52, 0x49, 0x46, 0x46, 0x50, 0x47, 0x01]);
            var scene = SampleScenes.CreateFlyby();
            await ProjectStore.SaveAsync(scene, project);
            await CreateAnalysisOutputAsync(output, source);

            var result = await AnalysisArtifactImporter.ImportAsync(
                scene,
                project,
                output,
                source,
                ModelHostChannelMode.MidSide,
                18);

            Assert.Equal(5, result.DeclaredAssetCount);
            Assert.Equal(0, result.ReusedAssetCount);
            Assert.Equal(1, result.PartialTrackCount);
            Assert.Equal(ModelHostChannelMode.MidSide, result.ChannelMode);
            Assert.Equal(18, result.MaxPartials);
            Assert.InRange(result.RmsNull, 0, AnalysisArtifactImporter.MaximumReconstructionError);

            var loaded = await ProjectStore.LoadAsync(project);
            Assert.Equal(scene.Assets.Count + 5, loaded.Assets.Count);
            var audioLayers = loaded.Assets.Where(asset => asset.MediaType == "audio/wav").ToList();
            Assert.Equal(3, audioLayers.Count);
            Assert.All(audioLayers, asset =>
            {
                Assert.Equal(48000, asset.SampleRate);
                Assert.Equal(2, asset.Channels);
                Assert.Equal(1, asset.DurationSeconds);
                Assert.Contains("mode=mid-side", asset.Provenance, StringComparison.Ordinal);
                Assert.True(File.Exists(ProjectStore.ResolveAssetPath(project, asset.Sha256)));
            });
            Assert.Single(loaded.Assets, asset => asset.MediaType == "application/vnd.pulsegen.partial-tracks+json");
            Assert.Single(loaded.Assets, asset => asset.MediaType == "application/vnd.pulsegen.analysis+json");
            Assert.All(loaded.Assets, asset => Assert.Equal(AssetKind.ModelOutput, asset.Kind));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task TamperedLayerIsRejectedWithoutChangingProjectManifest()
    {
        var root = Path.Combine(Path.GetTempPath(), $"pulsegen-analysis-tamper-{Guid.NewGuid():N}");
        var project = Path.Combine(root, "spectral-test.pulseproj");
        var output = Path.Combine(root, "analysis");
        var source = Path.Combine(root, "Microphone-Pair.wav");
        Directory.CreateDirectory(root);
        try
        {
            await File.WriteAllBytesAsync(source, [0x52, 0x49, 0x46, 0x46, 0x50, 0x47, 0x02]);
            var scene = SampleScenes.CreateFlyby();
            await ProjectStore.SaveAsync(scene, project);
            var manifestHashBefore = await ProjectStore.ComputeSha256Async(ProjectStore.ResolveManifestPath(project));
            await CreateAnalysisOutputAsync(output, source);
            await File.AppendAllTextAsync(Path.Combine(output, "partials.wav"), "tampered");

            var error = await Assert.ThrowsAsync<InvalidDataException>(() => AnalysisArtifactImporter.ImportAsync(
                scene,
                project,
                output,
                source,
                ModelHostChannelMode.MidSide,
                18));

            Assert.Contains("SHA-256", error.Message, StringComparison.Ordinal);
            var manifestHashAfter = await ProjectStore.ComputeSha256Async(ProjectStore.ResolveManifestPath(project));
            Assert.Equal(manifestHashBefore, manifestHashAfter);
            Assert.Empty((await ProjectStore.LoadAsync(project)).Assets);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static async Task CreateAnalysisOutputAsync(string output, string source)
    {
        Directory.CreateDirectory(output);
        var partialsPath = Path.Combine(output, "partials.wav");
        var transientsPath = Path.Combine(output, "transients.wav");
        var stochasticPath = Path.Combine(output, "stochastic-residual.wav");
        await File.WriteAllBytesAsync(partialsPath, [0x52, 0x49, 0x46, 0x46, 0x01]);
        await File.WriteAllBytesAsync(transientsPath, [0x52, 0x49, 0x46, 0x46, 0x02]);
        await File.WriteAllBytesAsync(stochasticPath, [0x52, 0x49, 0x46, 0x46, 0x03]);

        var tracks = new
        {
            schemaVersion = 1,
            representation = "mid-side",
            tracks = new[]
            {
                new
                {
                    id = "ch0-partial0",
                    channel = 0,
                    points = new[]
                    {
                        new { timeSeconds = 0.0, frequencyHz = 440.0, amplitude = 0.5, phaseRadians = 0.0 },
                        new { timeSeconds = 0.1, frequencyHz = 440.0, amplitude = 0.5, phaseRadians = 0.1 }
                    }
                }
            }
        };
        await File.WriteAllTextAsync(
            Path.Combine(output, "partial-tracks.json"),
            JsonSerializer.Serialize(tracks, new JsonSerializerOptions { WriteIndented = true }));

        var manifest = new
        {
            schemaVersion = 1,
            jobId = Guid.NewGuid().ToString("N"),
            source = new
            {
                path = Path.GetFullPath(source),
                sha256 = await ProjectStore.ComputeSha256Async(source),
                sampleRate = 48000,
                channels = 2,
                frames = 48000,
                durationSeconds = 1.0,
                untouched = true
            },
            analysis = new
            {
                kind = "multi-resolution-stft-partial-transient-stochastic",
                channelMode = "mid-side",
                windowSizes = new[] { 1024, 4096, 16384 },
                synthesisWindow = 2048,
                maxPartialsPerFrame = 18
            },
            layers = new
            {
                partials = new { file = "partials.wav", sha256 = await ProjectStore.ComputeSha256Async(partialsPath) },
                transients = new { file = "transients.wav", sha256 = await ProjectStore.ComputeSha256Async(transientsPath) },
                stochasticResidual = new { file = "stochastic-residual.wav", sha256 = await ProjectStore.ComputeSha256Async(stochasticPath) }
            },
            partialTracks = new { file = "partial-tracks.json", count = 1 },
            reconstruction = new
            {
                maxAbsoluteError = 2.0e-8,
                rmsNull = 7.0e-9,
                timingAndChannelAligned = true
            },
            model = (object?)null,
            provenance = new { hostVersion = "0.1.0-test", localOnly = true, elapsedSeconds = 0.25 }
        };
        await File.WriteAllTextAsync(
            Path.Combine(output, "analysis.json"),
            JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static void WriteStereoPcm16(string path, int sampleRate, int frames)
    {
        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream);
        const short channels = 2;
        const short bits = 16;
        var dataBytes = frames * channels * (bits / 8);
        writer.Write("RIFF"u8);
        writer.Write(36 + dataBytes);
        writer.Write("WAVE"u8);
        writer.Write("fmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write(channels);
        writer.Write(sampleRate);
        writer.Write(sampleRate * channels * (bits / 8));
        writer.Write((short)(channels * (bits / 8)));
        writer.Write(bits);
        writer.Write("data"u8);
        writer.Write(dataBytes);
        for (var frame = 0; frame < frames; frame++)
        {
            var time = (double)frame / sampleRate;
            writer.Write((short)(Math.Sin(2 * Math.PI * 330 * time) * 10000));
            writer.Write((short)(Math.Sin((2 * Math.PI * 880 * time) + 0.35) * 8000));
        }
    }
}
