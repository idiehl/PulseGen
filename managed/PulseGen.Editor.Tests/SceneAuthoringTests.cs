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
        Assert.True(SceneSelectionResolver.Exists(scene, SceneSelection.Material(scene.Materials[0].Id)));
        Assert.True(SceneSelectionResolver.Exists(scene, SceneSelection.RenderProfile(scene.RenderProfiles[0].Id)));
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

    [Fact]
    public void MaterialPresetsConserveEnergyAndCreateIndependentCoefficientArrays()
    {
        foreach (var preset in AcousticMaterialPresets.All)
        {
            var first = preset.Create(Guid.NewGuid());
            var second = preset.Create(Guid.NewGuid());
            var summary = AcousticMaterialPhysics.Average(first);

            Assert.Equal(AcousticMaterialPresets.StandardBandCentresHz.Length, first.Absorption.Length);
            Assert.InRange(Math.Abs(summary.Total - 1), 0, 1e-12);
            Assert.DoesNotContain(
                SceneValidator.Validate(SampleScenes.CreateFlyby() with
                {
                    Materials = [first],
                    Geometry = []
                }),
                issue => issue.Severity == ValidationSeverity.Error);

            first.Absorption[0] = Math.Min(1, first.Absorption[0] + 0.01);
            Assert.NotEqual(first.Absorption[0], second.Absorption[0]);
        }
    }

    [Fact]
    public void MaterialEditsRollBackInvalidEnergyAndCommitValidPresets()
    {
        var scene = SampleScenes.CreateSurfaceEcho();
        var original = scene.Materials[0];
        var invalidAbsorption = original.Absorption.ToArray();
        var invalidTransmission = original.Transmission.ToArray();
        invalidAbsorption[0] = 0.9;
        invalidTransmission[0] = 0.2;

        var invalidAccepted = SceneAuthoringOperations.TryReplaceMaterial(
            scene,
            original.Id,
            original with
            {
                Absorption = invalidAbsorption,
                Transmission = invalidTransmission
            },
            out var invalidError);

        Assert.False(invalidAccepted);
        Assert.Contains("incident energy", invalidError?.Message, StringComparison.Ordinal);
        Assert.Same(original, scene.Materials[0]);

        var replacement = AcousticMaterialPresets.All.First(item => item.Name == "Heavy curtain")
            .ApplyTo(original) with
        { Name = "Curtained stone wall" };
        var validAccepted = SceneAuthoringOperations.TryReplaceMaterial(
            scene,
            original.Id,
            replacement,
            out var validError);

        Assert.True(validAccepted, validError?.Message);
        Assert.Equal("Curtained stone wall", scene.Materials[0].Name);
        Assert.Equal(0.96, scene.Materials[0].Absorption[^1]);
    }

    [Fact]
    public void MaterialRemovalProtectsSurfaceAssignmentsAndTheLastStartingPoint()
    {
        var scene = SampleScenes.CreateSurfaceEcho();
        var used = scene.Materials[0];

        var usedAccepted = SceneAuthoringOperations.TryRemoveMaterial(scene, used.Id, out var usedError);

        Assert.False(usedAccepted);
        Assert.Contains(scene.Geometry[0].Name, usedError?.Message, StringComparison.Ordinal);
        Assert.Contains(used, scene.Materials);

        var spare = AcousticMaterialPresets.Default.Create(Guid.NewGuid(), "Unused comparison");
        Assert.True(SceneAuthoringOperations.TryAddMaterial(scene, spare, out var addError), addError?.Message);
        Assert.True(SceneAuthoringOperations.TryRemoveMaterial(scene, spare.Id, out var removeError), removeError?.Message);
        Assert.DoesNotContain(scene.Materials, item => item.Id == spare.Id);

        var minimal = SampleScenes.CreateFlyby();
        minimal.Geometry.Clear();
        var last = minimal.Materials[0];
        Assert.False(SceneAuthoringOperations.TryRemoveMaterial(minimal, last.Id, out var lastError));
        Assert.Contains("at least one", lastError?.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Same(last, minimal.Materials[0]);
    }

    [Fact]
    public void ReceiverEditsRollBackInvalidCapsulesAndProtectTheLastEnabledTake()
    {
        var scene = SampleScenes.CreateFlyby();
        var microphones = scene.ReceiverRigs.First(item => item.Kind == ReceiverRigKind.StereoMicrophone);
        var (left, right) = ReceiverRigGeometry.EffectiveMicrophoneCapsules(microphones);
        var invalidCapsules = microphones with
        {
            LeftCapsule = left with
            {
                RelativeTransform = left.RelativeTransform with { PositionMetres = new Vector3d(6, 0, 0) }
            },
            RightCapsule = right
        };

        var capsuleAccepted = SceneAuthoringOperations.TryReplaceReceiverRig(
            scene,
            microphones.Id,
            invalidCapsules,
            out var capsuleError);

        Assert.False(capsuleAccepted);
        Assert.Contains("within 5 m", capsuleError?.Message, StringComparison.Ordinal);
        Assert.Equal(microphones, scene.ReceiverRigs.First(item => item.Id == microphones.Id));

        scene.ReceiverRigs[1] = scene.ReceiverRigs[1] with { Enabled = false };
        var listener = scene.ReceiverRigs[0];
        var disableAccepted = SceneAuthoringOperations.TryReplaceReceiverRig(
            scene,
            listener.Id,
            listener with { Enabled = false },
            out var disableError);

        Assert.False(disableAccepted);
        Assert.Contains("one receiver", disableError?.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(scene.ReceiverRigs[0].Enabled);
    }

    [Fact]
    public void IndependentMicrophoneEditsCommitWithMotionAndPreserveCalibrationCurves()
    {
        var scene = SampleScenes.CreateFlyby();
        var microphones = scene.ReceiverRigs.First(item => item.Kind == ReceiverRigKind.StereoMicrophone);
        var (originalLeft, originalRight) = ReceiverRigGeometry.EffectiveMicrophoneCapsules(microphones);
        var left = originalLeft with
        {
            RelativeTransform = originalLeft.RelativeTransform with
            {
                PositionMetres = new Vector3d(-0.12, 0.02, 0.03),
                RotationDegrees = new Vector3d(5, -35, 0)
            },
            PolarPattern = PolarPattern.Supercardioid,
            SensitivityDb = -8
        };
        var right = originalRight with
        {
            RelativeTransform = originalRight.RelativeTransform with
            {
                PositionMetres = new Vector3d(0.2, -0.01, -0.04),
                RotationDegrees = new Vector3d(-3, 52, 1)
            },
            PolarPattern = PolarPattern.FigureEight,
            SensitivityDb = -3
        };
        var replacement = microphones with
        {
            Name = "Asymmetric field pair",
            Transform = microphones.Transform with { RotationDegrees = new Vector3d(0, 90, 0) },
            SpacingMetres = (right.RelativeTransform.PositionMetres - left.RelativeTransform.PositionMetres).Length,
            LeftCapsule = left,
            RightCapsule = right,
            MotionPathId = scene.MotionPaths[0].Id
        };

        var accepted = SceneAuthoringOperations.TryReplaceReceiverRig(
            scene,
            microphones.Id,
            replacement,
            out var error);

        Assert.True(accepted, error?.Message);
        var committed = scene.ReceiverRigs.First(item => item.Id == microphones.Id);
        Assert.Equal("Asymmetric field pair", committed.Name);
        Assert.Equal(PolarPattern.Supercardioid, committed.LeftCapsule?.PolarPattern);
        Assert.Equal(PolarPattern.FigureEight, committed.RightCapsule?.PolarPattern);
        Assert.Same(originalLeft.ResponseDb, committed.LeftCapsule?.ResponseDb);
        Assert.Equal(scene.MotionPaths[0].Id, SceneSelectionResolver.MotionPath(scene, SceneSelection.Receiver(committed.Id))?.Id);
        var worldPosition = ReceiverRigGeometry.CapsuleWorldPosition(
            committed,
            committed.LeftCapsule!,
            committed.Transform.PositionMetres);
        Assert.InRange(Math.Abs(worldPosition.X - 0.03), 0, 1e-12);
        Assert.InRange(Math.Abs(worldPosition.Y - 1.72), 0, 1e-12);
        Assert.InRange(Math.Abs(worldPosition.Z - -0.88), 0, 1e-12);
    }

    [Fact]
    public void MaterialAuditionsAreValidDeterministicScenesWithoutMutatingTheSourceMaterial()
    {
        var source = AcousticMaterialPresets.All.First(item => item.Name == "Dense timber").Create(Guid.NewGuid());
        var originalId = source.Id;
        var originalAbsorption = source.Absorption.ToArray();

        foreach (var band in MaterialAuditionBands.All)
        {
            var audition = MaterialAuditionScene.Create(source, band.FrequencyHz);

            Assert.Equal(1.5, audition.Timeline.DurationSeconds);
            Assert.Equal(band.FrequencyHz, audition.Emitters[0].SignalGraph.Nodes[0].NumericParameters["frequencyHz"]);
            Assert.Equal(4096, audition.RenderProfiles[0].RayCount);
            Assert.Equal(1.25, audition.RenderProfiles[0].ImpulseResponseSeconds);
            Assert.DoesNotContain(SceneValidator.Validate(audition), issue => issue.Severity == ValidationSeverity.Error);
        }

        Assert.Equal(originalId, source.Id);
        Assert.Equal(originalAbsorption, source.Absorption);
    }

    [Fact]
    public void RenderProfileEditsRollBackInvalidEchoQualityAndCommitValidSettings()
    {
        var scene = SampleScenes.CreateFlyby();
        var original = scene.RenderProfiles[0];

        var invalidAccepted = SceneAuthoringOperations.TryReplaceRenderProfile(
            scene,
            original.Id,
            original with { RayCount = -1, ImpulseResponseSeconds = 31 },
            out var invalidError);

        Assert.False(invalidAccepted);
        Assert.Contains("ray count", invalidError?.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Same(original, scene.RenderProfiles[0]);

        var replacement = original with
        {
            Name = "Dense room tail",
            ReflectionOrder = 1,
            RayCount = 4096,
            ImpulseResponseSeconds = 3.25,
            AtmosphereIntegrationSegments = 32,
            Seed = 8675309
        };
        var validAccepted = SceneAuthoringOperations.TryReplaceRenderProfile(
            scene,
            original.Id,
            replacement,
            out var validError);

        Assert.True(validAccepted, validError?.Message);
        Assert.Equal(replacement, scene.RenderProfiles[0]);
    }
}
