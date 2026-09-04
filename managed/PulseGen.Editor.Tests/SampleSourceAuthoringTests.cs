using PulseGen.Contracts;
using PulseGen.Engine.Client;
using Xunit;

namespace PulseGen.Editor.Tests;

public sealed class SampleSourceAuthoringTests
{
    [Fact]
    public void LinkedImportCopiesThePathAndKeepsChannelEditsIndependent()
    {
        var original = SampleScenes.CreateFlyby();
        var before = ProjectStore.SerializeManifest(original);
        var imported = SampleSourceAuthoring.Add(original, Asset(2), new(SourceChannelMapping.LinkedDualEmitters, true), original.MotionPaths[0].Id);
        var left = imported.Scene.Emitters.Single(item => item.Id == imported.SelectedEmitterId);
        var right = imported.Scene.Emitters.Single(item => item.Id == left.LinkedRightEmitterId);
        Assert.Equal(before, ProjectStore.SerializeManifest(original));
        Assert.False(imported.Scene.Emitters[0].Enabled);
        Assert.Equal(left.Id, right.LinkedRightEmitterId);
        Assert.NotEqual(left.MotionPathId, right.MotionPathId);
        var leftPath = imported.Scene.MotionPaths.Single(item => item.Id == left.MotionPathId);
        var rightPath = imported.Scene.MotionPaths.Single(item => item.Id == right.MotionPathId);
        Assert.InRange(rightPath.Keyframes[0].PositionMetres.X - leftPath.Keyframes[0].PositionMetres.X, 0.19999, 0.20001);
        leftPath.Keyframes[0] = leftPath.Keyframes[0] with { PositionMetres = Vector3d.Zero };
        Assert.NotEqual(Vector3d.Zero, original.MotionPaths[0].Keyframes[0].PositionMetres);
        Assert.NotEqual(Vector3d.Zero, rightPath.Keyframes[0].PositionMetres);
    }

    [Fact]
    public void LinkedPlaybackTimingUpdatesBothChannelsWithoutMutatingOriginal()
    {
        var source = SampleSourceAuthoring.Add(SampleScenes.CreateFlyby(), Asset(2), new(SourceChannelMapping.LinkedDualEmitters, false));
        var updated = SampleSourceAuthoring.UpdatePlayback(source.Scene, source.SelectedEmitterId, 1, 0.25, true);
        var settings = updated.Emitters.SelectMany(item => item.SignalGraph.Nodes).Where(item => item.SamplePlayback is not null).Select(item => item.SamplePlayback!).ToArray();
        Assert.Equal(2, settings.Length);
        Assert.All(settings, item => { Assert.Equal(1, item.StartTimeSeconds); Assert.Equal(0.25, item.OffsetSeconds); Assert.True(item.Loop); });
        Assert.All(source.Scene.Emitters.SelectMany(item => item.SignalGraph.Nodes).Where(item => item.SamplePlayback is not null), item => Assert.False(item.SamplePlayback!.Loop));
        var before = ProjectStore.SerializeManifest(updated);
        Assert.Throws<SceneValidationException>(() => SampleSourceAuthoring.UpdatePlayback(updated, source.SelectedEmitterId, 1, 4, true));
        Assert.Equal(before, ProjectStore.SerializeManifest(updated));
    }

    [Fact]
    public void AmbiguousStereoImportCannotMuteOrChangeTheScene()
    {
        var source = SampleScenes.CreateFlyby();
        var before = ProjectStore.SerializeManifest(source);
        Assert.Throws<InvalidDataException>(() => SampleSourceAuthoring.Add(source, Asset(2), new(SourceChannelMapping.MonoPoint, true)));
        Assert.Equal(before, ProjectStore.SerializeManifest(source));
    }

    [Fact]
    public async Task ImportedRecordingSurvivesPortableProjectRoundTripWithOriginalBytes()
    {
        var root = Path.Combine(Path.GetTempPath(), "pulsegen-sample-authoring-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var project = Path.Combine(root, "source.pulseproj");
            var scene = SampleScenes.CreateFlyby();
            await ProjectStore.SaveAsync(scene, project);
            var wav = Path.Combine(root, "left-only.wav");
            var samples = new float[4800];
            for (var frame = 0; frame < samples.Length / 2; frame++) samples[frame * 2] = 1.25f;
            FloatWaveFile.WriteAtomic(wav, new FloatWaveBuffer(48000, 2, samples));
            var importedAsset = await ProjectStore.ImportAssetAsync(project, wav, AssetKind.Audio, "audio/wav");
            var decoded = SampleAudioReader.Read(wav);
            var asset = importedAsset with { Channels = decoded.Channels, SampleRate = decoded.SampleRate, DurationSeconds = decoded.DurationSeconds };
            var imported = SampleSourceAuthoring.Add(scene, asset, new(SourceChannelMapping.NonSpatialStereoBed, true));
            await ProjectStore.SaveAsync(imported.Scene, project);
            var archive = Path.Combine(root, "source.pulsepack");
            await ProjectStore.PackAsync(project, archive);
            var restored = Path.Combine(root, "restored.pulseproj");
            await ProjectStore.UnpackAsync(archive, restored);
            var loaded = await ProjectStore.LoadAsync(restored);
            var restoredAsset = loaded.Assets.Single(item => item.Sha256 == asset.Sha256);
            Assert.Equal(await File.ReadAllBytesAsync(wav), await File.ReadAllBytesAsync(ProjectStore.ResolveAssetPath(restored, asset.Sha256)));
            Assert.Equal(samples, SampleAudioReader.ReadVerified(restored, restoredAsset).Samples);
            Assert.Equal(SourceChannelMapping.NonSpatialStereoBed, loaded.Emitters.Single(item => item.Id == imported.SelectedEmitterId).ChannelMapping);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void ReimportingAudioPreservesItsExistingLicenseAndAttribution()
    {
        var scene = SampleScenes.CreateFlyby();
        scene.Assets.Add(Asset(1) with { License = "CC-BY-4.0", Provenance = "Original field recording by Example" });
        var imported = SampleSourceAuthoring.Add(scene, Asset(1), new(SourceChannelMapping.MonoPoint, false));
        Assert.Single(imported.Scene.Assets);
        Assert.Equal("CC-BY-4.0", imported.Scene.Assets[0].License);
        Assert.Equal("Original field recording by Example", imported.Scene.Assets[0].Provenance);
    }

    [Fact]
    public async Task CancellingPreparedImportLeavesTheProjectAndOriginalUntouched()
    {
        var root = Path.Combine(Path.GetTempPath(), "pulsegen-sample-stage-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var project = Path.Combine(root, "scene.pulseproj");
            await ProjectStore.SaveAsync(SampleScenes.CreateFlyby(), project);
            var source = Path.Combine(root, "source.wav");
            FloatWaveFile.WriteAtomic(source, new FloatWaveBuffer(48000, 1, new float[100]));
            var original = await File.ReadAllBytesAsync(source);
            string staged;
            using (var prepared = await PreparedAudioImport.CreateAsync(source, CancellationToken.None))
            {
                staged = prepared.FilePath;
                Assert.True(File.Exists(staged));
                Assert.Equal("source.wav", prepared.Asset.FileName);
                Assert.Empty(Directory.GetFiles(Path.Combine(project, "assets"), "*", SearchOption.AllDirectories));
            }
            Assert.False(File.Exists(staged));
            Assert.Equal(original, await File.ReadAllBytesAsync(source));
            Assert.Empty(Directory.GetFiles(Path.Combine(project, "assets"), "*", SearchOption.AllDirectories));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task LockedStagingFileDoesNotTurnSuccessfulPreparationIntoAnImportFailure()
    {
        var root = Path.Combine(Path.GetTempPath(), "pulsegen-sample-cleanup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        PreparedAudioImport? prepared = null;
        try
        {
            var source = Path.Combine(root, "source.wav");
            FloatWaveFile.WriteAtomic(source, new FloatWaveBuffer(48000, 1, new float[100]));
            prepared = await PreparedAudioImport.CreateAsync(source, CancellationToken.None);
            var stagingDirectory = Path.GetDirectoryName(prepared.FilePath)!;
            var asset = prepared.Asset;
            using (var heldFile = new FileStream(prepared.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var error = Record.Exception(prepared.Dispose);
                Assert.Null(error);
                Assert.True(File.Exists(prepared.FilePath));
                Assert.Equal(asset, prepared.Asset);
                Assert.Equal(48000, prepared.Asset.SampleRate);
            }

            prepared.Dispose();
            Assert.False(File.Exists(prepared.FilePath));
            Assert.False(Directory.Exists(stagingDirectory));
            Assert.True(File.Exists(source));
        }
        finally
        {
            prepared?.Dispose();
            Directory.Delete(root, recursive: true);
        }
    }

    private static AssetRef Asset(int channels) => new()
    {
        Sha256 = new string('a', 64), Kind = AssetKind.Audio, FileName = "recording.wav", MediaType = "audio/wav",
        Channels = channels, SampleRate = 48000, DurationSeconds = 2, SizeBytes = 48000 * channels * 2 * 4 + 44
    };
}
