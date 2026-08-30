using PulseGen.Contracts;
using Xunit;

namespace PulseGen.Editor.Tests;

public sealed class SceneAuthoringTests
{
    [Fact]
    public void SelectionResolverTracksTheSelectedSourceAndItsOwnPath()
    {
        var scene = SampleScenes.CreateFlyby();
        var movingEmitter = scene.Emitters[0];
        var movingSelection = SceneSelection.Emitter(movingEmitter.Id);
        var staticEmitter = movingEmitter with
        {
            Id = Guid.NewGuid(),
            Name = "Static comparison source",
            MotionPathId = null
        };
        scene.Emitters.Add(staticEmitter);

        Assert.True(SceneSelectionResolver.Exists(scene, movingSelection));
        Assert.Equal(movingEmitter.Id, SceneSelectionResolver.Emitter(scene, movingSelection)?.Id);
        Assert.Equal(movingEmitter.MotionPathId, SceneSelectionResolver.MotionPath(scene, movingSelection)?.Id);
        Assert.Null(SceneSelectionResolver.MotionPath(scene, SceneSelection.Emitter(staticEmitter.Id)));
        Assert.False(SceneSelectionResolver.Exists(scene, SceneSelection.Geometry(Guid.NewGuid())));
    }

    [Fact]
    public void UniqueNamesAreStableAndCaseInsensitive()
    {
        var existing = new[] { "Wind zone", "wind ZONE 2", "Wind zone 3" };

        Assert.Equal("Reflective plane", SceneSelectionResolver.UniqueName("Reflective plane", existing));
        Assert.Equal("Wind zone 4", SceneSelectionResolver.UniqueName("Wind zone", existing));
    }

    [Fact]
    public void InvalidGeometryAndWindEditsRollBackAtomically()
    {
        var scene = SampleScenes.CreateFlyby();
        var geometry = scene.Geometry[0];
        var wind = scene.Atmosphere.WindZones[0];

        var geometryAccepted = SceneAuthoringOperations.TryReplaceGeometry(
            scene,
            geometry.Id,
            geometry with { Transform = geometry.Transform with { Scale = new Vector3d(1, 0, 1) } },
            out var geometryError);
        var windAccepted = SceneAuthoringOperations.TryReplaceWindZone(
            scene,
            wind.Id,
            wind with { Turbulence = 1.5 },
            out var windError);

        Assert.False(geometryAccepted);
        Assert.Contains("positive scale", geometryError?.Message, StringComparison.Ordinal);
        Assert.Equal(geometry, scene.Geometry[0]);
        Assert.False(windAccepted);
        Assert.Contains("0..1", windError?.Message, StringComparison.Ordinal);
        Assert.Equal(wind, scene.Atmosphere.WindZones[0]);
    }

    [Fact]
    public void ValidPhysicsEditsCommitAndRemainRenderable()
    {
        var scene = SampleScenes.CreateFlyby();
        var geometry = scene.Geometry[0];
        var wind = scene.Atmosphere.WindZones[0];

        var geometryAccepted = SceneAuthoringOperations.TryReplaceGeometry(
            scene,
            geometry.Id,
            geometry with { Transform = geometry.Transform with { PositionMetres = new Vector3d(3, 2.5, -4) } },
            out var geometryError);
        var windAccepted = SceneAuthoringOperations.TryReplaceWindZone(
            scene,
            wind.Id,
            wind with { VelocityMetresPerSecond = new Vector3d(7, 0, 1), Turbulence = 0.25 },
            out var windError);

        Assert.True(geometryAccepted, geometryError?.Message);
        Assert.True(windAccepted, windError?.Message);
        Assert.Equal(new Vector3d(3, 2.5, -4), scene.Geometry[0].Transform.PositionMetres);
        Assert.Equal(new Vector3d(7, 0, 1), scene.Atmosphere.WindZones[0].VelocityMetresPerSecond);
        Assert.DoesNotContain(
            SceneValidator.Validate(scene),
            issue => issue.Severity == ValidationSeverity.Error);
    }

    [Fact]
    public void InvalidAtmosphereEditsPreserveTheOriginalField()
    {
        var scene = SampleScenes.CreateAtmosphereField();
        var originalLayer = scene.Atmosphere.Layers[1];

        var layerAccepted = SceneAuthoringOperations.TryReplaceAtmosphereLayer(
            scene,
            1,
            originalLayer with { BaseAltitudeMetres = scene.Atmosphere.Layers[0].BaseAltitudeMetres },
            out var layerError);
        var baseAccepted = SceneAuthoringOperations.TryReplaceBaseAtmosphere(
            scene,
            scene.Atmosphere with { PressureKilopascals = 1 },
            out var updatedScene,
            out var baseError);

        Assert.False(layerAccepted);
        Assert.Contains("overlap", layerError?.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(originalLayer, scene.Atmosphere.Layers[1]);
        Assert.False(baseAccepted);
        Assert.Contains("pressure", baseError?.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Same(scene, updatedScene);
    }

    [Fact]
    public void InvalidSourceAndMotionEditsPreserveTheRenderableScene()
    {
        var scene = SampleScenes.CreateFlyby();
        var emitter = scene.Emitters[0];
        var path = scene.MotionPaths.First(item => item.Id == emitter.MotionPathId);
        var keyframes = path.Keyframes.ToList();
        keyframes[1] = keyframes[1] with { PositionMetres = new Vector3d(100_000, 0, 0) };

        var emitterAccepted = SceneAuthoringOperations.TryReplaceEmitter(
            scene,
            emitter.Id,
            emitter with { GainLinear = -1 },
            out var emitterError);
        var pathAccepted = SceneAuthoringOperations.TryReplaceMotionPath(
            scene,
            path.Id,
            path with { Keyframes = keyframes },
            out var pathError);

        Assert.False(emitterAccepted);
        Assert.Contains("non-negative", emitterError?.Message, StringComparison.Ordinal);
        Assert.Equal(emitter, scene.Emitters[0]);
        Assert.False(pathAccepted);
        Assert.Contains("sonic", pathError?.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(path, scene.MotionPaths.First(item => item.Id == path.Id));
    }

    [Fact]
    public void InvalidAddsDoNotLeavePartialSceneObjectsBehind()
    {
        var scene = SampleScenes.CreateSurfaceEcho();
        var originalCount = scene.Geometry.Count;
        var duplicateId = scene.Geometry[0].Id;
        var materialId = scene.Materials[0].Id;
        var duplicate = new AcousticGeometry
        {
            Id = duplicateId,
            Name = "Duplicate",
            Kind = GeometryKind.Box,
            MaterialId = materialId,
            DimensionsMetres = new Vector3d(1, 1, 1)
        };

        var accepted = SceneAuthoringOperations.TryAddGeometry(scene, duplicate, out var error);

        Assert.False(accepted);
        Assert.Contains("unique", error?.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(originalCount, scene.Geometry.Count);
    }
}
