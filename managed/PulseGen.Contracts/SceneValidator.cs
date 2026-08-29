namespace PulseGen.Contracts;

public sealed record ValidationIssue(string Path, string Message);

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

            ValidateSignalGraph(emitter, issues);
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

            if (rig.MotionPathId is { } pathId && !pathIds.Contains(pathId))
            {
                issues.Add(new($"receiverRigs/{rig.Id}/motionPathId", "Referenced motion path does not exist."));
            }
        }

        foreach (var material in scene.Materials)
        {
            ValidateMaterial(material, issues);
        }

        foreach (var profile in scene.RenderProfiles)
        {
            if (!SupportedSampleRates.Contains(profile.SampleRate))
            {
                issues.Add(new($"renderProfiles/{profile.Id}/sampleRate", "Unsupported render sample rate."));
            }

            if (profile.BufferFrames is < 64 or > 8192)
            {
                issues.Add(new($"renderProfiles/{profile.Id}/bufferFrames", "Buffer size must be between 64 and 8192 frames."));
            }
        }

        return issues;
    }

    public static void EnsureValid(SceneDocument scene)
    {
        var issues = Validate(scene);
        if (issues.Count > 0)
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
    }

    public static double SpeedOfSound(double temperatureCelsius) => 331.3 + (0.606 * temperatureCelsius);
}

public sealed class SceneValidationException(IReadOnlyList<ValidationIssue> issues)
    : Exception(string.Join(Environment.NewLine, issues.Select(issue => $"{issue.Path}: {issue.Message}")))
{
    public IReadOnlyList<ValidationIssue> Issues { get; } = issues;
}

