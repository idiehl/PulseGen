using PulseGen.Contracts;
using Xunit;
using System.Text.Json;

namespace PulseGen.Contracts.Tests;

public sealed class SceneContractTests
{
    [Fact]
    public void FlybySceneIsValid()
    {
        var issues = SceneValidator.Validate(SampleScenes.CreateFlyby());
        Assert.Empty(issues);
    }

    [Fact]
    public void SurfaceEchoSceneIsValid()
    {
        var issues = SceneValidator.Validate(SampleScenes.CreateSurfaceEcho());
        Assert.Empty(issues);
    }

    [Fact]
    public void UnsupportedUnitsAreRejected()
    {
        var scene = SampleScenes.CreateFlyby() with { Units = "feet" };
        var issues = SceneValidator.Validate(scene);
        Assert.Contains(issues, issue => issue.Path == "units");
    }

    [Fact]
    public void DanglingMotionPathIsRejected()
    {
        var scene = SampleScenes.CreateFlyby();
        scene.Emitters[0] = scene.Emitters[0] with { MotionPathId = Guid.NewGuid() };
        var issues = SceneValidator.Validate(scene);
        Assert.Contains(issues, issue => issue.Path.EndsWith("motionPathId", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ProjectRoundTripsAndProducesStableHash()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"pulsegen-{Guid.NewGuid():N}.pulseproj");
        try
        {
            var scene = SampleScenes.CreateFlyby();
            var firstHash = await ProjectStore.SaveAsync(scene, directory);
            var loaded = await ProjectStore.LoadAsync(directory);
            var secondHash = await ProjectStore.SaveAsync(loaded, directory);

            Assert.Equal(scene.ProjectId, loaded.ProjectId);
            Assert.Equal(scene.ReceiverRigs.Count, loaded.ReceiverRigs.Count);
            Assert.Equal(firstHash, secondHash);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task CheckedInSurfaceEchoProjectMatchesItsFactoryScene()
    {
        var fixtureDirectory = Path.Combine(AppContext.BaseDirectory, "Fixtures", "SurfaceEcho.pulseproj");
        var generatedDirectory = Path.Combine(Path.GetTempPath(), $"pulsegen-surface-{Guid.NewGuid():N}.pulseproj");
        try
        {
            var loaded = await ProjectStore.LoadAsync(fixtureDirectory);
            var fixtureHash = await ProjectStore.ComputeSha256Async(ProjectStore.ResolveManifestPath(fixtureDirectory));
            var generatedHash = await ProjectStore.SaveAsync(SampleScenes.CreateSurfaceEcho(), generatedDirectory);

            Assert.Equal("Surface Echo Study", loaded.Name);
            Assert.Equal(fixtureHash, generatedHash);
        }
        finally
        {
            if (Directory.Exists(generatedDirectory))
            {
                Directory.Delete(generatedDirectory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task PulsePackRoundTripsWithoutOverwritingDestination()
    {
        var root = Path.Combine(Path.GetTempPath(), $"pulsepack-{Guid.NewGuid():N}");
        var project = Path.Combine(root, "Source.pulseproj");
        var archive = Path.Combine(root, "Portable.pulsepack");
        var imported = Path.Combine(root, "Imported.pulseproj");
        try
        {
            await ProjectStore.SaveAsync(SampleScenes.CreateFlyby(), project);
            var archiveHash = await ProjectStore.PackAsync(project, archive);
            var loaded = await ProjectStore.UnpackAsync(archive, imported);

            Assert.Equal(64, archiveHash.Length);
            Assert.Equal(SampleScenes.CreateFlyby().ProjectId, loaded.ProjectId);
            await Assert.ThrowsAsync<IOException>(() => ProjectStore.UnpackAsync(archive, imported));
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
    [InlineData(20, 343.42)]
    [InlineData(0, 331.3)]
    public void SpeedOfSoundUsesTemperature(double temperature, double expected)
    {
        Assert.Equal(expected, SceneValidator.SpeedOfSound(temperature), precision: 6);
    }

    [Fact]
    public void EmbeddedSchemasAreWellFormedJson()
    {
        using var scene = JsonDocument.Parse(SchemaCatalog.ReadSceneSchema());
        using var packs = JsonDocument.Parse(SchemaCatalog.ReadModelPackSchema());
        Assert.Equal("PulseGen SceneDocument v1", scene.RootElement.GetProperty("title").GetString());
        Assert.Equal("PulseGen ModelPackManifest v1", packs.RootElement.GetProperty("title").GetString());
    }

    [Fact]
    public void NearSonicMotionWarnsButSupersonicMotionFails()
    {
        var scene = SampleScenes.CreateFlyby();
        var path = scene.MotionPaths[0];
        path.Keyframes[1] = path.Keyframes[1] with { TimeSeconds = 0.1, PositionMetres = new(30, 1.7, 4) };
        var issues = SceneValidator.Validate(scene);
        Assert.Contains(issues, issue => issue.Severity == ValidationSeverity.Error && issue.Message.Contains("supersonic", StringComparison.Ordinal));
        Assert.Throws<SceneValidationException>(() => SceneValidator.EnsureValid(scene));

        path.Keyframes[1] = path.Keyframes[1] with { TimeSeconds = 0.2, PositionMetres = new(27, 1.7, 4) };
        issues = SceneValidator.Validate(scene);
        Assert.Contains(issues, issue => issue.Severity == ValidationSeverity.Warning && issue.Message.Contains("near sonic", StringComparison.Ordinal));
        SceneValidator.EnsureValid(scene);
    }

    [Fact]
    public void ExactSofaModeCannotSilentlyUseNoDataset()
    {
        var scene = SampleScenes.CreateFlyby();
        scene.ReceiverRigs[0] = scene.ReceiverRigs[0] with { HrtfMode = HrtfMode.ExactSofa, SofaAssetHash = null };
        var issues = SceneValidator.Validate(scene);
        Assert.Contains(issues, issue => issue.Path.EndsWith("sofaAssetHash", StringComparison.Ordinal));
    }

    [Fact]
    public void IntegerAndFloatRenderProfilesRequireExplicitCompatibleDither()
    {
        var scene = SampleScenes.CreateFlyby();
        scene.RenderProfiles[0] = scene.RenderProfiles[0] with { SampleFormat = RenderSampleFormat.Pcm24, Dither = DitherMode.None };
        Assert.Contains(SceneValidator.Validate(scene), issue => issue.Path.EndsWith("/dither", StringComparison.Ordinal));

        scene.RenderProfiles[0] = scene.RenderProfiles[0] with { Dither = DitherMode.Tpdf };
        Assert.DoesNotContain(SceneValidator.Validate(scene), issue => issue.Path.EndsWith("/dither", StringComparison.Ordinal));

        scene.RenderProfiles[0] = scene.RenderProfiles[0] with { SampleFormat = RenderSampleFormat.Float32, Dither = DitherMode.Tpdf };
        Assert.Contains(SceneValidator.Validate(scene), issue => issue.Path.EndsWith("/dither", StringComparison.Ordinal));
    }

    [Fact]
    public void MaterialBandsMustBeOrderedAndConserveEnergy()
    {
        var scene = SampleScenes.CreateFlyby();
        var material = scene.Materials[0];
        material.OctaveBandCentresHz[2] = material.OctaveBandCentresHz[1];
        material.Absorption[0] = 0.9;
        material.Transmission[0] = 0.2;

        var issues = SceneValidator.Validate(scene);

        Assert.Contains(issues, issue => issue.Path.EndsWith("/octaveBandCentresHz", StringComparison.Ordinal));
        Assert.Contains(issues, issue => issue.Path == $"materials/{material.Id}" && issue.Message.Contains("incident energy", StringComparison.Ordinal));
    }

    [Fact]
    public void AcousticGeometryRequiresMaterialAndPhysicalDimensions()
    {
        var scene = SampleScenes.CreateFlyby();
        var geometryId = Guid.NewGuid();
        scene.Geometry.Add(new AcousticGeometry
        {
            Id = geometryId,
            Name = "Invalid obstacle",
            Kind = GeometryKind.Box,
            MaterialId = Guid.NewGuid(),
            DimensionsMetres = new(2, -1, 0.2),
            Transform = new Transform { Scale = new(1, 0, 1) }
        });

        var issues = SceneValidator.Validate(scene);

        Assert.Contains(issues, issue => issue.Path == $"geometry/{geometryId}/materialId");
        Assert.Contains(issues, issue => issue.Path == $"geometry/{geometryId}/dimensionsMetres");
        Assert.Contains(issues, issue => issue.Path == $"geometry/{geometryId}/transform");
    }

    [Fact]
    public void ImportedMeshRequiresDeclaredMeshAssetAndLabelsReflectionBoundary()
    {
        var scene = SampleScenes.CreateFlyby();
        var geometryId = Guid.NewGuid();
        var hash = new string('a', 64);
        scene.Geometry.Add(new AcousticGeometry
        {
            Id = geometryId,
            Name = "Imported wall",
            Kind = GeometryKind.ImportedMesh,
            MaterialId = scene.Materials[0].Id,
            MeshAssetHash = hash,
            Reflects = false
        });

        Assert.Contains(SceneValidator.Validate(scene), issue =>
            issue.Path == $"geometry/{geometryId}/meshAssetHash" && issue.Severity == ValidationSeverity.Error);

        scene.Assets.Add(new AssetRef
        {
            Sha256 = hash,
            Kind = AssetKind.Audio,
            FileName = "wall.obj",
            MediaType = "model/obj",
            SizeBytes = 100
        });
        Assert.Contains(SceneValidator.Validate(scene), issue =>
            issue.Path == $"geometry/{geometryId}/meshAssetHash" && issue.Severity == ValidationSeverity.Error);

        scene.Assets[0] = scene.Assets[0] with { Kind = AssetKind.Mesh };
        Assert.DoesNotContain(SceneValidator.Validate(scene), issue => issue.Severity == ValidationSeverity.Error);
        scene.Geometry[^1] = scene.Geometry[^1] with { Reflects = true };
        Assert.Contains(SceneValidator.Validate(scene), issue =>
            issue.Path == $"geometry/{geometryId}/reflects" &&
            issue.Severity == ValidationSeverity.Warning &&
            issue.Message.Contains("reflection backend", StringComparison.Ordinal));
    }

    [Fact]
    public void AssetHashesMustBeValidUniqueAndNonNegative()
    {
        var scene = SampleScenes.CreateFlyby();
        scene.Assets.Add(new AssetRef
        {
            Sha256 = "not-a-sha256",
            Kind = AssetKind.Mesh,
            FileName = "bad.obj",
            MediaType = "model/obj",
            SizeBytes = -1
        });
        var validHash = new string('b', 64);
        scene.Assets.Add(new AssetRef
        {
            Sha256 = validHash,
            Kind = AssetKind.Mesh,
            FileName = "first.obj",
            MediaType = "model/obj",
            SizeBytes = 1
        });
        scene.Assets.Add(scene.Assets[^1] with { FileName = "duplicate.obj" });

        var issues = SceneValidator.Validate(scene);

        Assert.Contains(issues, issue => issue.Path == "assets/0/sha256");
        Assert.Contains(issues, issue => issue.Path == "assets/0/sizeBytes");
        Assert.Contains(issues, issue => issue.Path == "assets/2/sha256" && issue.Message.Contains("unique", StringComparison.Ordinal));
    }
}
