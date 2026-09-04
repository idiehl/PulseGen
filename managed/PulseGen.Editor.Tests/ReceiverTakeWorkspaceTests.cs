using PulseGen.Contracts;
using PulseGen.Engine.Client;
using System.Buffers.Binary;
using System.Text.Json;
using Xunit;

namespace PulseGen.Editor.Tests;

public sealed class ReceiverTakeWorkspaceTests
{
    [Fact]
    public async Task UnchangedStereoBackgroundReusesSourceAudioWithoutLosingAttribution()
    {
        var root = Path.Combine(Path.GetTempPath(), $"pulsegen-shared-source-{Guid.NewGuid():N}");
        var project = Path.Combine(root, "shared.pulseproj");
        try
        {
            var scene = SampleScenes.CreateFlyby();
            await ProjectStore.SaveAsync(scene, project);
            var source = Path.Combine(root, "original.wav");
            var samples = new float[2400];
            for (var frame = 0; frame < 1200; frame++) samples[frame * 2] = 0.25f;
            FloatWaveFile.WriteAtomic(source, new FloatWaveBuffer(48000, 2, samples));
            var audio = await ProjectStore.ImportAssetAsync(project, source, AssetKind.Audio, "audio/wav");
            audio = audio with { SampleRate = 48000, Channels = 2, DurationSeconds = 0.025,
                License = "CC-BY-4.0", Provenance = "Original recording attribution" };
            scene = SampleSourceAuthoring.Add(scene, audio, new(SourceChannelMapping.NonSpatialStereoBed, true)).Scene;
            scene = scene with { Timeline = scene.Timeline with { DurationSeconds = 0.025 } };
            await ProjectStore.SaveAsync(scene, project);
            var render = NativeSceneRenderer.Render(scene, Path.Combine(root, "renders"), projectDirectory: project);
            var captured = await CaptureTakeImporter.ImportAsync(scene, project, render.RenderReceiptPath);
            Assert.Equal(2, captured.ImportedTakeCount);
            Assert.All(captured.Scene.CaptureTakes, take => Assert.Equal(audio.Sha256, take.SourceAssetHash));
            var reused = Assert.Single(captured.Scene.Assets, item => item.Sha256 == audio.Sha256);
            Assert.Equal(AssetKind.Audio, reused.Kind);
            Assert.Equal(audio.License, reused.License);
            Assert.Equal(audio.Provenance, reused.Provenance);
            Assert.Equal(samples, SampleAudioReader.ReadVerified(project, reused).Samples);
            // The same immutable audio remains valid if a render was its first role.
            Assert.Equal(samples, SampleAudioReader.ReadVerified(project, reused with { Kind = AssetKind.Render }).Samples);
            Assert.Equal(2, (await ReceiverTakeCatalog.LoadAsync(captured.Scene, project)).Count);
            var rerender = NativeSceneRenderer.Render(captured.Scene, Path.Combine(root, "rerender"), projectDirectory: project);
            Assert.Equal(render.PeakLeft, rerender.PeakLeft);
            Assert.Equal(0, rerender.PeakRight);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task NativeReceiptImportsThroughTheRealReceiverTakeBoundary()
    {
        var root = Path.Combine(Path.GetTempPath(), $"pulsegen-native-takes-{Guid.NewGuid():N}");
        var project = Path.Combine(root, "native-takes.pulseproj");
        var render = Path.Combine(root, "render");
        try
        {
            var scene = SampleScenes.CreateFlyby() with { Timeline = new EventTimeline { DurationSeconds = 0.02 } };
            scene.ReceiverRigs[0] = scene.ReceiverRigs[0] with { Name = "Duplicate/receiver" };
            scene.ReceiverRigs[1] = scene.ReceiverRigs[1] with { Name = "Duplicate?receiver" };
            scene.RenderProfiles[0] = scene.RenderProfiles[0] with
            {
                ReflectionOrder = 0,
                RayCount = 0,
                ImpulseResponseSeconds = 0
            };
            await ProjectStore.SaveAsync(scene, project);

            var receipt = NativeSceneRenderer.Render(scene, render, scene.RenderProfiles[0], project);
            var imported = await CaptureTakeImporter.ImportAsync(scene, project, receipt.RenderReceiptPath);

            using var receiptDocument = JsonDocument.Parse(await File.ReadAllTextAsync(receipt.RenderReceiptPath));
            var renderedNames = receiptDocument.RootElement.GetProperty("takes").EnumerateArray()
                .Select(take => take.GetProperty("fileName").GetString())
                .ToArray();

            Assert.Equal(receipt.FilesWritten, (uint)imported.ImportedTakeCount);
            Assert.Equal(renderedNames.Length, renderedNames.Distinct(StringComparer.Ordinal).Count());
            Assert.Equal(scene.ReceiverRigs.Count(receiver => receiver.Enabled), imported.Scene.CaptureTakes.Count);
            Assert.All(imported.Scene.CaptureTakes, take => Assert.EndsWith(" take 1", take.Name, StringComparison.Ordinal));
            Assert.All(await ReceiverTakeCatalog.LoadAsync(imported.Scene, project), session => Assert.Equal(960, (int)Math.Round(session.DurationSeconds * session.SampleRate)));
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
    public async Task VerifiedRenderBecomesEditableChannelsAndDeterministicMaster()
    {
        var root = Path.Combine(Path.GetTempPath(), $"pulsegen-takes-{Guid.NewGuid():N}");
        var project = Path.Combine(root, "takes.pulseproj");
        var render = Path.Combine(root, "render");
        Directory.CreateDirectory(render);
        try
        {
            var scene = SampleScenes.CreateFlyby() with { Timeline = new EventTimeline { DurationSeconds = 0.01 } };
            await ProjectStore.SaveAsync(scene, project);
            var receiptPath = await CreateReceiptAsync(scene, render, frames: 480);

            var imported = await CaptureTakeImporter.ImportAsync(scene, project, receiptPath);

            Assert.Equal(2, imported.ImportedTakeCount);
            Assert.Equal(7, imported.AddedAssetCount);
            Assert.Equal(2, imported.Scene.CaptureTakes.Count);
            Assert.Equal(7, imported.Scene.Assets.Count);
            Assert.All(imported.Scene.CaptureTakes, take =>
            {
                Assert.NotNull(take.SourceAssetHash);
                Assert.NotNull(take.LeftAssetHash);
                Assert.NotNull(take.RightAssetHash);
                Assert.NotNull(take.RenderReceiptHash);
            });

            var sessions = await ReceiverTakeCatalog.LoadAsync(imported.Scene, project);
            Assert.Equal(2, sessions.Count);
            Assert.All(sessions, session =>
            {
                Assert.Equal(48000, session.SampleRate);
                Assert.Equal(0.01, session.DurationSeconds, 6);
                Assert.Equal(192, session.LeftEnvelope.Count);
                Assert.Equal(192, session.RightEnvelope.Count);
            });

            var first = sessions[0] with
            {
                Take = sessions[0].Take with
                {
                    TimelineOffsetSeconds = 0.01,
                    TrimStartSeconds = 0.002,
                    TrimEndSeconds = 0.008,
                    LeftGainLinear = 0.5,
                    RightGainLinear = 0.75
                }
            };
            var second = sessions[1] with
            {
                Take = sessions[1].Take with { TimelineOffsetSeconds = 0 }
            };
            var arranged = new[] { first, second };
            var masterA = await ReceiverTakeMixer.BuildMasterAsync(arranged);
            var masterB = await ReceiverTakeMixer.BuildMasterAsync(arranged);
            Assert.Equal(768, masterA.Buffer.Frames);
            Assert.Equal(masterA.Buffer.InterleavedSamples, masterB.Buffer.InterleavedSamples);
            Assert.True(masterA.Peak > 0);
            Assert.Equal(2, masterA.TakeIds.Count);

            var left = await ReceiverTakeMixer.RenderChannelAsync(first, leftChannel: true, includeTimelineOffset: true);
            Assert.Equal(768, left.Frames);
            Assert.Equal(1, left.Channels);
            Assert.All(left.InterleavedSamples.Take(480), sample => Assert.Equal(0, sample));

            var wavePath = Path.Combine(root, "master.wav");
            FloatWaveFile.WriteAtomic(wavePath, masterA.Buffer);
            Assert.Equal(masterA.Buffer.InterleavedSamples, FloatWaveFile.Read(wavePath).InterleavedSamples);
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
    public async Task TamperedRenderedTakeIsRejectedBeforeManifestMutation()
    {
        var root = Path.Combine(Path.GetTempPath(), $"pulsegen-take-tamper-{Guid.NewGuid():N}");
        var project = Path.Combine(root, "takes.pulseproj");
        var render = Path.Combine(root, "render");
        Directory.CreateDirectory(render);
        try
        {
            var scene = SampleScenes.CreateFlyby() with { Timeline = new EventTimeline { DurationSeconds = 0.01 } };
            await ProjectStore.SaveAsync(scene, project);
            var receiptPath = await CreateReceiptAsync(scene, render, frames: 480);
            var manifestBefore = await ProjectStore.ComputeSha256Async(ProjectStore.ResolveManifestPath(project));
            var firstWave = Directory.EnumerateFiles(render, "*.wav").Order(StringComparer.Ordinal).First();
            await File.AppendAllTextAsync(firstWave, "tampered");

            var error = await Assert.ThrowsAsync<InvalidDataException>(() => CaptureTakeImporter.ImportAsync(scene, project, receiptPath));

            Assert.Contains("size or SHA-256", error.Message, StringComparison.Ordinal);
            Assert.Equal(manifestBefore, await ProjectStore.ComputeSha256Async(ProjectStore.ResolveManifestPath(project)));
            Assert.Empty((await ProjectStore.LoadAsync(project)).CaptureTakes);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Theory]
    [InlineData(16)]
    [InlineData(24)]
    public void IntegerPcmReceiverRendersDecodeToFiniteFloatSamples(int bitsPerSample)
    {
        var path = Path.Combine(Path.GetTempPath(), $"pulsegen-pcm-{bitsPerSample}-{Guid.NewGuid():N}.wav");
        try
        {
            WriteIntegerPcmWave(path, bitsPerSample);
            var buffer = FloatWaveFile.Read(path);
            Assert.Equal(2, buffer.Channels);
            Assert.Equal(48000, buffer.SampleRate);
            Assert.Equal(3, buffer.Frames);
            Assert.All(buffer.InterleavedSamples, sample => Assert.True(float.IsFinite(sample)));
            Assert.True(buffer.InterleavedSamples.Max() > 0.9f);
            Assert.True(buffer.InterleavedSamples.Min() < -0.9f);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static async Task<string> CreateReceiptAsync(SceneDocument scene, string renderDirectory, int frames)
    {
        var takes = new List<object>();
        for (var receiverIndex = 0; receiverIndex < scene.ReceiverRigs.Count; receiverIndex++)
        {
            var receiver = scene.ReceiverRigs[receiverIndex];
            var samples = new float[frames * 2];
            for (var frame = 0; frame < frames; frame++)
            {
                samples[frame * 2] = (float)(0.1 * (receiverIndex + 1) * Math.Sin(frame * 0.07));
                samples[(frame * 2) + 1] = (float)(0.13 * (receiverIndex + 1) * Math.Cos(frame * 0.09));
            }
            var fileName = $"take-{receiverIndex + 1}.wav";
            var path = Path.Combine(renderDirectory, fileName);
            FloatWaveFile.WriteAtomic(path, new FloatWaveBuffer(48000, 2, samples));
            takes.Add(new
            {
                receiverRigId = receiver.Id,
                receiverName = receiver.Name,
                fileName,
                sizeBytes = new FileInfo(path).Length,
                sha256 = await ProjectStore.ComputeSha256Async(path)
            });
        }
        var receipt = new
        {
            receiptVersion = 8,
            projectId = scene.ProjectId,
            sceneRevisionSha256 = ProjectStore.ComputeSceneRevisionSha256(scene),
            renderProfileId = scene.RenderProfiles[0].Id,
            sampleRate = 48000,
            framesRendered = frames,
            filesWritten = takes.Count,
            takes
        };
        var receiptPath = Path.Combine(renderDirectory, "render-receipt.json");
        await File.WriteAllTextAsync(receiptPath, JsonSerializer.Serialize(receipt, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        return receiptPath;
    }

    private static void WriteIntegerPcmWave(string path, int bitsPerSample)
    {
        var bytesPerSample = bitsPerSample / 8;
        const int channels = 2;
        var values = new[] { -1.0, -0.5, 0.0, 0.25, 0.75, 0.999 };
        var dataBytes = values.Length * bytesPerSample;
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        Span<byte> header = stackalloc byte[44];
        "RIFF"u8.CopyTo(header);
        BinaryPrimitives.WriteUInt32LittleEndian(header[4..], checked((uint)(36 + dataBytes)));
        "WAVE"u8.CopyTo(header[8..]);
        "fmt "u8.CopyTo(header[12..]);
        BinaryPrimitives.WriteUInt32LittleEndian(header[16..], 16);
        BinaryPrimitives.WriteUInt16LittleEndian(header[20..], 1);
        BinaryPrimitives.WriteUInt16LittleEndian(header[22..], channels);
        BinaryPrimitives.WriteUInt32LittleEndian(header[24..], 48000);
        BinaryPrimitives.WriteUInt32LittleEndian(header[28..], checked((uint)(48000 * channels * bytesPerSample)));
        BinaryPrimitives.WriteUInt16LittleEndian(header[32..], checked((ushort)(channels * bytesPerSample)));
        BinaryPrimitives.WriteUInt16LittleEndian(header[34..], checked((ushort)bitsPerSample));
        "data"u8.CopyTo(header[36..]);
        BinaryPrimitives.WriteUInt32LittleEndian(header[40..], checked((uint)dataBytes));
        stream.Write(header);
        Span<byte> sample16 = stackalloc byte[2];
        foreach (var value in values)
        {
            if (bitsPerSample == 16)
            {
                BinaryPrimitives.WriteInt16LittleEndian(sample16, checked((short)Math.Round(value * 32767)));
                stream.Write(sample16);
            }
            else
            {
                var encoded = checked((int)Math.Round(value * 8388607));
                stream.WriteByte((byte)encoded);
                stream.WriteByte((byte)(encoded >> 8));
                stream.WriteByte((byte)(encoded >> 16));
            }
        }
    }
}
