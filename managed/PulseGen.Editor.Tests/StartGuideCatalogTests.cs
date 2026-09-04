using Xunit;

namespace PulseGen.Editor.Tests;

public sealed class StartGuideCatalogTests
{
    [Fact]
    public void StarterCopiesReceiveFreshProjectIdentities()
    {
        var created = new DateTimeOffset(2026, 9, 4, 12, 0, 0, TimeSpan.Zero);
        var first = StartGuideCatalog.CreateStarterScene(StartGuideAction.ExploreFlyby, created);
        var second = StartGuideCatalog.CreateStarterScene(StartGuideAction.ExploreFlyby, created);

        Assert.NotEqual(first.ProjectId, second.ProjectId);
        Assert.Equal(created, first.CreatedAtUtc);
        Assert.Equal("Physics Flyby", first.Name);
        Assert.Equal(2, first.ReceiverRigs.Count);
        Assert.Empty(first.CaptureTakes);
    }

    [Fact]
    public void SurfaceEchoStarterRetainsItsAcousticComparison()
    {
        var scene = StartGuideCatalog.CreateStarterScene(StartGuideAction.ExploreSurfaceEcho);

        Assert.Equal("Surface Echo Study", scene.Name);
        Assert.True(scene.Geometry.Count >= 3);
        Assert.True(scene.Materials.Count >= 3);
        Assert.NotEmpty(scene.ReceiverRigs);
    }

    [Fact]
    public void CapabilityCopySeparatesWorkingAndGatedFeatures()
    {
        Assert.All(StartGuideCatalog.ReadyFeatures, feature => Assert.Equal("READY", feature.Status));
        Assert.All(StartGuideCatalog.GatedFeatures, feature => Assert.NotEqual("READY", feature.Status));
        Assert.Contains(StartGuideCatalog.GatedFeatures, feature => feature.Name == "Measured ear profiles");
        Assert.Contains(StartGuideCatalog.GatedFeatures, feature => feature.Name == "Automatic source isolation");
    }
}

