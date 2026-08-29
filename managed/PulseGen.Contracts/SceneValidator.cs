namespace PulseGen.Contracts;

public enum ValidationSeverity
{
    Warning,
    Error
}

public sealed record ValidationIssue(string Path, string Message, ValidationSeverity Severity = ValidationSeverity.Error);

public static class SceneValidator
{
    private static readonly HashSet<int> SupportedSampleRates = [44100, 48000, 88200, 96000, 192000];

    public static IReadOnlyList<ValidationIssue> Validate(SceneDocument scene)
    {
        ArgumentNullException.ThrowIfNull(scene);
        var issues = new List<ValidationIssue>();

        if (scene.SchemaVersion != ContractVersions.SceneDocument)
        {
            issues.Add(new("schemaVersion", $"Expected {ContractVersions.SceneDocument}."));
        }

        if (string.IsNullOrWhiteSpace(scene.Name))
        {
            issues.Add(new("name", "Project name is required."));
        }

        if (!string.Equals(scene.Units, "SI", StringComparison.Ordinal))
        {
            issues.Add(new("units", "PulseGen scene coordinates must use SI units."));
        }

        if (!SupportedSampleRates.Contains(scene.DefaultSampleRate))
        {
            issues.Add(new("defaultSampleRate", "Unsupported project sample rate."));
        }

        ValidateAtmosphere(scene.Atmosphere, issues);
        ValidateUniqueIds(scene, issues);

        var pathIds = scene.MotionPaths.Select(path => path.Id).ToHashSet();
        foreach (var emitter in scene.Emitters)
        {
            if (emitter.MotionPathId is { } pathId && !pathIds.Contains(pathId))
            {
                issues.Add(new($"emitters/{emitter.Id}/motionPathId", "Referenced motion path does not exist."));
            }

            if (emitter.GainLinear < 0 || !double.IsFinite(emitter.GainLinear))
            {
                issues.Add(new($"emitters/{emitter.Id}/gainLinear", "Gain must be finite and non-negative."));
            }
            if (!double.IsFinite(emitter.Directivity) || emitter.Directivity is < 0 or > 1)
            {
                issues.Add(new($"emitters/{emitter.Id}/directivity", "Emitter directivity must be a finite value in 0..1."));
            }

            ValidateSignalGraph(emitter, issues);
            if (emitter.ChannelMapping == SourceChannelMapping.LinkedDualEmitters &&
                (emitter.LinkedRightEmitterId is null || !scene.Emitters.Any(candidate => candidate.Id == emitter.LinkedRightEmitterId)))
            {
                issues.Add(new($"emitters/{emitter.Id}/linkedRightEmitterId", "Linked dual-emitter mapping requires a valid right emitter."));
            }
        }

        foreach (var path in scene.MotionPaths)
        {
            if (path.Keyframes.Count < 2)
            {
                issues.Add(new($"motionPaths/{path.Id}/keyframes", "A motion path needs at least two keyframes."));
                continue;
            }

            for (var index = 1; index < path.Keyframes.Count; index++)
            {
                if (path.Keyframes[index].TimeSeconds <= path.Keyframes[index - 1].TimeSeconds)
                {
                    issues.Add(new($"motionPaths/{path.Id}/keyframes/{index}/timeSeconds", "Keyframe times must increase strictly."));
                }
                else
                {
                    var distance = (path.Keyframes[index].PositionMetres - path.Keyframes[index - 1].PositionMetres).Length;
                    var elapsed = path.Keyframes[index].TimeSeconds - path.Keyframes[index - 1].TimeSeconds;
                    var speed = distance / elapsed;
                    var localSoundSpeed = SpeedOfSound(scene.Atmosphere.TemperatureCelsius);
                    if (speed >= localSoundSpeed)
                    {
                        issues.Add(new($"motionPaths/{path.Id}/keyframes/{index}", $"Segment speed {speed:0.##} m/s is sonic or supersonic and cannot be rendered by the bounded retarded-time solver."));
                    }
                    else if (speed >= 0.8 * localSoundSpeed)
                    {
                        issues.Add(new($"motionPaths/{path.Id}/keyframes/{index}", $"Segment speed {speed:0.##} m/s is near sonic; final render will clamp solver discontinuities.", ValidationSeverity.Warning));
                    }
                }
            }

            for (var index = 1; index < path.VelocityProfile.Count; index++)
            {
                if (path.VelocityProfile[index].TimeSeconds <= path.VelocityProfile[index - 1].TimeSeconds || path.VelocityProfile[index].SpeedMetresPerSecond < 0)
                {
                    issues.Add(new($"motionPaths/{path.Id}/velocityProfile/{index}", "Velocity controls require increasing times and non-negative speeds."));
                }
            }
        }

        foreach (var rig in scene.ReceiverRigs)
        {
            if (rig.SpacingMetres is < 0.01 or > 5)
            {
                issues.Add(new($"receiverRigs/{rig.Id}/spacingMetres", "Receiver spacing must be between 0.01 m and 5 m."));
            }

            if (rig.Kind == ReceiverRigKind.BinauralHead && rig.HeadRadiusMetres is < 0.04 or > 0.2)
            {
                issues.Add(new($"receiverRigs/{rig.Id}/headRadiusMetres", "Binaural head radius is outside the bounded parametric range."));
            }
            if (rig.Kind == ReceiverRigKind.BinauralHead && rig.HrtfMode == HrtfMode.ExactSofa && string.IsNullOrWhiteSpace(rig.SofaAssetHash))
            {
                issues.Add(new($"receiverRigs/{rig.Id}/sofaAssetHash", "Exact SOFA mode requires an imported dataset asset."));
            }
            if (rig.Kind == ReceiverRigKind.StereoMicrophone)
            {
                ValidateCapsule(rig.LeftCapsule, $"receiverRigs/{rig.Id}/leftCapsule", issues);
                ValidateCapsule(rig.RightCapsule, $"receiverRigs/{rig.Id}/rightCapsule", issues);
            }

            if (rig.MotionPathId is { } pathId && !pathIds.Contains(pathId))
            {
                issues.Add(new($"receiverRigs/{rig.Id}/motionPathId", "Referenced motion path does not exist."));
            }
        }

        if (scene.Materials.Count > 256)
        {
            issues.Add(new("materials", "The bounded native acoustic world supports at most 256 materials."));
        }
        foreach (var material in scene.Materials)
        {
            ValidateMaterial(material, issues);
        }

        if (scene.Geometry.Count(geometry => geometry.Kind is GeometryKind.Plane or GeometryKind.Box) > 256)
        {
            issues.Add(new("geometry", "The bounded native acoustic world supports at most 256 plane and box primitives."));
        }
        var materialIds = scene.Materials.Select(material => material.Id).ToHashSet();
        foreach (var geometry in scene.Geometry)
        {
            ValidateGeometry(geometry, materialIds, issues);
        }

        foreach (var profile in scene.RenderProfiles)
        {
            if (!SupportedSampleRates.Contains(profile.SampleRate))
            {
                issues.Add(new($"renderProfiles/{profile.Id}/sampleRate", "Unsupported render sample rate."));
            }
            if (profile.SampleFormat == RenderSampleFormat.Float32 && profile.Dither != DitherMode.None)
            {
                issues.Add(new($"renderProfiles/{profile.Id}/dither", "Floating-point renders must not be dithered."));
            }
            if (profile.SampleFormat != RenderSampleFormat.Float32 && profile.Dither != DitherMode.Tpdf)
            {
                issues.Add(new($"renderProfiles/{profile.Id}/dither", "Integer PCM renders require an explicit TPDF dither selection."));
            }

            if (profile.BufferFrames is < 64 or > 8192)
            {
                issues.Add(new($"renderProfiles/{profile.Id}/bufferFrames", "Buffer size must be between 64 and 8192 frames."));
            }
            if (profile.AtmosphereIntegrationSegments is < 1 or > 4096)
            {
                issues.Add(new($"renderProfiles/{profile.Id}/atmosphereIntegrationSegments", "Atmosphere integration segment count must be in 1..4096."));
            }
        }

        return issues;
    }

    public static void EnsureValid(SceneDocument scene)
    {
        var issues = Validate(scene);
        if (issues.Any(issue => issue.Severity == ValidationSeverity.Error))
        {
            throw new SceneValidationException(issues);
        }
    }

    private static void ValidateAtmosphere(AtmosphereField atmosphere, List<ValidationIssue> issues)
    {
        if (atmosphere.TemperatureCelsius is < -80 or > 80)
        {
            issues.Add(new("atmosphere/temperatureCelsius", "Temperature is outside the supported acoustic range."));
        }

        if (atmosphere.RelativeHumidityPercent is < 0 or > 100)
        {
            issues.Add(new("atmosphere/relativeHumidityPercent", "Humidity must be between 0 and 100 percent."));
        }

        if (atmosphere.PressureKilopascals is < 20 or > 120)
        {
            issues.Add(new("atmosphere/pressureKilopascals", "Pressure is outside the supported range."));
        }

        foreach (var zone in atmosphere.WindZones)
        {
            if (zone.VelocityMetresPerSecond.Length >= 0.8 * SpeedOfSound(atmosphere.TemperatureCelsius))
            {
                issues.Add(new($"atmosphere/windZones/{zone.Id}/velocity", "Wind velocity must remain below 80% of local sound speed."));
            }

            if (zone.Turbulence is < 0 or > 1)
            {
                issues.Add(new($"atmosphere/windZones/{zone.Id}/turbulence", "Turbulence must be normalized to 0..1."));
            }
        }

        var orderedLayers = atmosphere.Layers.OrderBy(layer => layer.BaseAltitudeMetres).ToList();
        for (var index = 0; index < orderedLayers.Count; index++)
        {
            var layer = orderedLayers[index];
            if (layer.TopAltitudeMetres <= layer.BaseAltitudeMetres || layer.TemperatureCelsius is < -80 or > 80 ||
                layer.RelativeHumidityPercent is < 0 or > 100 || layer.PressureKilopascals is < 20 or > 120)
            {
                issues.Add(new($"atmosphere/layers/{index}", "Atmosphere layers require increasing altitude and bounded temperature, humidity, and pressure."));
            }
            if (index > 0 && layer.BaseAltitudeMetres < orderedLayers[index - 1].TopAltitudeMetres)
            {
                issues.Add(new($"atmosphere/layers/{index}", "Atmosphere layers may touch but may not overlap."));
            }
        }
    }

    private static void ValidateUniqueIds(SceneDocument scene, List<ValidationIssue> issues)
    {
        var ids = new HashSet<Guid>();
        IEnumerable<(Guid Id, string Path)> all =
            scene.Emitters.Select(value => (value.Id, "emitters"))
                .Concat(scene.MotionPaths.Select(value => (value.Id, "motionPaths")))
                .Concat(scene.Materials.Select(value => (value.Id, "materials")))
                .Concat(scene.Geometry.Select(value => (value.Id, "geometry")))
                .Concat(scene.ReceiverRigs.Select(value => (value.Id, "receiverRigs")))
                .Concat(scene.RenderProfiles.Select(value => (value.Id, "renderProfiles")))
                .Concat(scene.CaptureTakes.Select(value => (value.Id, "captureTakes")));

        foreach (var (id, path) in all)
        {
            if (id == Guid.Empty || !ids.Add(id))
            {
                issues.Add(new($"{path}/{id}", "Entity IDs must be non-empty and globally unique."));
            }
        }
    }

    private static void ValidateSignalGraph(Emitter emitter, List<ValidationIssue> issues)
    {
        var nodeIds = emitter.SignalGraph.Nodes.Select(node => node.Id).ToHashSet();
        if (nodeIds.Count != emitter.SignalGraph.Nodes.Count)
        {
            issues.Add(new($"emitters/{emitter.Id}/signalGraph/nodes", "Signal node IDs must be unique."));
        }

        foreach (var connection in emitter.SignalGraph.Connections)
        {
            if (!nodeIds.Contains(connection.FromNodeId) || !nodeIds.Contains(connection.ToNodeId))
            {
                issues.Add(new($"emitters/{emitter.Id}/signalGraph/connections", "Signal connection references a missing node."));
            }
        }

        if (!emitter.SignalGraph.Nodes.Any(node => node.Kind == SignalNodeKind.Output))
        {
            issues.Add(new($"emitters/{emitter.Id}/signalGraph", "A signal graph requires an output node."));
        }
    }

    private static void ValidateMaterial(AcousticMaterial material, List<ValidationIssue> issues)
    {
        var count = material.OctaveBandCentresHz.Length;
        if (count == 0 || material.Absorption.Length != count || material.Scattering.Length != count || material.Transmission.Length != count)
        {
            issues.Add(new($"materials/{material.Id}", "All material coefficient arrays must match octave-band centres."));
            return;
        }

        static bool CoefficientsValid(IEnumerable<double> values) => values.All(value => value is >= 0 and <= 1 && double.IsFinite(value));
        if (!CoefficientsValid(material.Absorption) || !CoefficientsValid(material.Scattering) || !CoefficientsValid(material.Transmission))
        {
            issues.Add(new($"materials/{material.Id}", "Acoustic coefficients must be finite values in 0..1."));
        }
        if (material.OctaveBandCentresHz.Any(frequency => !double.IsFinite(frequency) || frequency <= 0) ||
            material.OctaveBandCentresHz.Zip(material.OctaveBandCentresHz.Skip(1), (left, right) => right > left).Any(increasing => !increasing))
        {
            issues.Add(new($"materials/{material.Id}/octaveBandCentresHz", "Octave-band centres must be finite, positive, and strictly increasing."));
        }
        if (!double.IsFinite(material.Roughness) || material.Roughness is < 0 or > 1)
        {
            issues.Add(new($"materials/{material.Id}/roughness", "Material roughness must be a finite value in 0..1."));
        }
        if (material.Absorption.Zip(material.Transmission, (absorption, transmission) => absorption + transmission).Any(total => total > 1 + 1e-12))
        {
            issues.Add(new($"materials/{material.Id}", "Absorption plus transmission may not exceed the incident energy in any band."));
        }
    }

    private static void ValidateGeometry(AcousticGeometry geometry, IReadOnlySet<Guid> materialIds, List<ValidationIssue> issues)
    {
        if ((geometry.Occludes || geometry.Reflects) && (geometry.MaterialId is null || !materialIds.Contains(geometry.MaterialId.Value)))
        {
            issues.Add(new($"geometry/{geometry.Id}/materialId", "Acoustic geometry requires a valid material."));
        }
        if (!Finite(geometry.Transform.PositionMetres) || !Finite(geometry.Transform.RotationDegrees) || !Finite(geometry.Transform.Scale) ||
            geometry.Transform.Scale.X <= 0 || geometry.Transform.Scale.Y <= 0 || geometry.Transform.Scale.Z <= 0)
        {
            issues.Add(new($"geometry/{geometry.Id}/transform", "Geometry transforms must be finite and use positive scale."));
        }

        var dimensions = geometry.DimensionsMetres;
        var dimensionsValid = Finite(dimensions) && (geometry.Kind switch
        {
            GeometryKind.Plane => dimensions.X > 0 && dimensions.Y > 0 && dimensions.Z >= 0,
            _ => dimensions.X > 0 && dimensions.Y > 0 && dimensions.Z > 0
        });
        if (!dimensionsValid)
        {
            issues.Add(new($"geometry/{geometry.Id}/dimensionsMetres", "Geometry dimensions must be finite and positive; plane depth may be zero."));
        }
        if (geometry.Kind == GeometryKind.ImportedMesh && string.IsNullOrWhiteSpace(geometry.MeshAssetHash))
        {
            issues.Add(new($"geometry/{geometry.Id}/meshAssetHash", "Imported mesh geometry requires a content-addressed mesh asset."));
        }
        if (geometry.IsDynamic && (geometry.Occludes || geometry.Reflects))
        {
            issues.Add(new($"geometry/{geometry.Id}/isDynamic", "Dynamic acoustic geometry is retained in the project but requires a future motion adapter.", ValidationSeverity.Warning));
        }
        if (geometry.Kind is not (GeometryKind.Plane or GeometryKind.Box or GeometryKind.ParametricRoom) && (geometry.Occludes || geometry.Reflects))
        {
            issues.Add(new($"geometry/{geometry.Id}/kind", $"{geometry.Kind} acoustic behavior requires an adapter that is not active yet.", ValidationSeverity.Warning));
        }
    }

    private static bool Finite(Vector3d value) =>
        double.IsFinite(value.X) && double.IsFinite(value.Y) && double.IsFinite(value.Z);

    private static void ValidateCapsule(MicrophoneCapsule? capsule, string path, List<ValidationIssue> issues)
    {
        if (capsule is null)
        {
            return;
        }
        if (capsule.ResponseCentresHz.Length == 0 || capsule.ResponseCentresHz.Length != capsule.ResponseDb.Length ||
            capsule.ResponseCentresHz.Any(frequency => frequency <= 0 || !double.IsFinite(frequency)) ||
            capsule.ResponseDb.Any(value => !double.IsFinite(value)))
        {
            issues.Add(new(path, "Microphone response frequency and gain arrays must be finite, non-empty, and equal length."));
        }
    }

    public static double SpeedOfSound(double temperatureCelsius) => 331.3 + (0.606 * temperatureCelsius);
}

public sealed class SceneValidationException(IReadOnlyList<ValidationIssue> issues)
    : Exception(string.Join(Environment.NewLine, issues.Select(issue => $"{issue.Path}: {issue.Message}")))
{
    public IReadOnlyList<ValidationIssue> Issues { get; } = issues;
}
