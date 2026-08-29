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
}
