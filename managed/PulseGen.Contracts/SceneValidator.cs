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
            ValidateSamplePlayback(scene, emitter, issues);
        }
        ValidateSampleCacheBudget(scene, issues);

        foreach (var path in scene.MotionPaths)
        {
            if (path.Keyframes.Count < 2)
            {
                issues.Add(new($"motionPaths/{path.Id}/keyframes", "A motion path needs at least two keyframes."));
                continue;
            }

            for (var index = 0; index < path.Keyframes.Count; index++)
            {
                var keyframe = path.Keyframes[index];
                if (!double.IsFinite(keyframe.TimeSeconds) || keyframe.TimeSeconds < 0 || !Finite(keyframe.PositionMetres) ||
                    (keyframe.InTangentMetres is { } inTangent && !Finite(inTangent)) ||
                    (keyframe.OutTangentMetres is { } outTangent && !Finite(outTangent)))
                {
                    issues.Add(new($"motionPaths/{path.Id}/keyframes/{index}", "Keyframe time, position, and optional tangents must be finite; time may not be negative."));
                    continue;
                }
                if (index == 0)
                {
                    continue;
                }
                if (keyframe.TimeSeconds <= path.Keyframes[index - 1].TimeSeconds)
                {
                    issues.Add(new($"motionPaths/{path.Id}/keyframes/{index}/timeSeconds", "Keyframe times must increase strictly."));
                }
                else if (Finite(path.Keyframes[index - 1].PositionMetres) && double.IsFinite(path.Keyframes[index - 1].TimeSeconds))
                {
                    var distance = (keyframe.PositionMetres - path.Keyframes[index - 1].PositionMetres).Length;
                    var elapsed = keyframe.TimeSeconds - path.Keyframes[index - 1].TimeSeconds;
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

            for (var index = 0; index < path.VelocityProfile.Count; index++)
            {
                var control = path.VelocityProfile[index];
                if (!double.IsFinite(control.TimeSeconds) || control.TimeSeconds < 0 ||
                    !double.IsFinite(control.SpeedMetresPerSecond) || control.SpeedMetresPerSecond < 0 ||
                    (index > 0 && control.TimeSeconds <= path.VelocityProfile[index - 1].TimeSeconds))
                {
                    issues.Add(new($"motionPaths/{path.Id}/velocityProfile/{index}", "Velocity controls require finite increasing times and finite non-negative speeds."));
                }
            }
        }

        if (scene.ReceiverRigs.Count is 0 or > 16)
        {
            issues.Add(new("receiverRigs", "A renderable scene requires between 1 and 16 stereo receiver rigs."));
        }
        else if (!scene.ReceiverRigs.Any(rig => rig.Enabled))
        {
            issues.Add(new("receiverRigs", "At least one stereo receiver rig must be enabled."));
        }

        foreach (var rig in scene.ReceiverRigs)
        {
            if (string.IsNullOrWhiteSpace(rig.Name))
            {
                issues.Add(new($"receiverRigs/{rig.Id}/name", "Receiver name is required."));
            }
            if (!Finite(rig.Transform.PositionMetres) || !Finite(rig.Transform.RotationDegrees))
            {
                issues.Add(new($"receiverRigs/{rig.Id}/transform", "Receiver position and orientation must be finite."));
            }
            if (!double.IsFinite(rig.SpacingMetres) || rig.SpacingMetres is < 0.01 or > 5)
            {
                issues.Add(new($"receiverRigs/{rig.Id}/spacingMetres", "Receiver spacing must be between 0.01 m and 5 m."));
            }

            if (rig.Kind == ReceiverRigKind.BinauralHead &&
                (!double.IsFinite(rig.HeadRadiusMetres) || rig.HeadRadiusMetres is < 0.04 or > 0.2))
            {
                issues.Add(new($"receiverRigs/{rig.Id}/headRadiusMetres", "Binaural head radius is outside the bounded parametric range."));
            }
            if (!double.IsFinite(rig.AngleDegrees) || !double.IsFinite(rig.SensitivityDb) || !double.IsFinite(rig.SelfNoiseDbSpl))
            {
                issues.Add(new($"receiverRigs/{rig.Id}", "Receiver angle, sensitivity, and self-noise must be finite."));
            }
            else if (rig.SelfNoiseDbSpl is < 0 or > 200)
            {
                issues.Add(new($"receiverRigs/{rig.Id}/selfNoiseDbSpl", "Receiver self-noise must remain between 0 and 200 dB SPL."));
            }
            if (!Finite(rig.HeadDimensionsMetres) || rig.HeadDimensionsMetres.X <= 0 ||
                rig.HeadDimensionsMetres.Y <= 0 || rig.HeadDimensionsMetres.Z <= 0)
            {
                issues.Add(new($"receiverRigs/{rig.Id}/headDimensionsMetres", "Receiver head dimensions must be finite and positive."));
            }
            if (rig.Kind == ReceiverRigKind.BinauralHead && rig.HrtfMode == HrtfMode.ExactSofa && string.IsNullOrWhiteSpace(rig.SofaAssetHash))
            {
                issues.Add(new($"receiverRigs/{rig.Id}/sofaAssetHash", "Exact SOFA mode requires an imported dataset asset."));
            }
            if (!string.IsNullOrWhiteSpace(rig.SofaAssetHash) && !scene.Assets.Any(asset =>
                    string.Equals(asset.Sha256, rig.SofaAssetHash, StringComparison.OrdinalIgnoreCase) &&
                    asset.Kind == AssetKind.Hrtf))
            {
                issues.Add(new($"receiverRigs/{rig.Id}/sofaAssetHash", "The selected SOFA dataset must reference a declared HRTF asset."));
            }
            if (rig.Kind == ReceiverRigKind.StereoMicrophone)
            {
                if ((rig.LeftCapsule is null) != (rig.RightCapsule is null))
                {
                    issues.Add(new($"receiverRigs/{rig.Id}", "A stereo microphone rig must define both independent capsules or use the linked-pair fallback."));
                }
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

        var assetsByHash = ValidateAssets(scene.Assets, issues);
        ValidateCaptureTakes(scene.CaptureTakes, scene.ReceiverRigs, scene.Assets, issues);

        if (scene.Geometry.Count(geometry => geometry.Kind is GeometryKind.Plane or GeometryKind.Box) > 256)
        {
            issues.Add(new("geometry", "The bounded native acoustic world supports at most 256 plane and box primitives."));
        }
        if (scene.Geometry.Count(geometry => geometry.Kind == GeometryKind.ImportedMesh) > 64)
        {
            issues.Add(new("geometry", "The bounded native acoustic world supports at most 64 imported meshes."));
        }
        var materialIds = scene.Materials.Select(material => material.Id).ToHashSet();
        foreach (var geometry in scene.Geometry)
        {
            ValidateGeometry(geometry, materialIds, assetsByHash, issues);
        }

        if (scene.RenderProfiles.Count == 0)
        {
            issues.Add(new("renderProfiles", "At least one render profile is required for preview and export."));
        }

        foreach (var profile in scene.RenderProfiles)
        {
            if (string.IsNullOrWhiteSpace(profile.Name))
            {
                issues.Add(new($"renderProfiles/{profile.Id}/name", "Render profile name is required."));
            }
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
            if (profile.ReflectionOrder is < 0 or > 2)
            {
                issues.Add(new($"renderProfiles/{profile.Id}/reflectionOrder", "The current engine accepts room reflection depth 0, 1, or 2."));
            }
            if (profile.RayCount is < 0 or > 1_000_000)
            {
                issues.Add(new($"renderProfiles/{profile.Id}/rayCount", "Diffuse ray count must be in 0..1,000,000."));
            }
            if (!double.IsFinite(profile.ImpulseResponseSeconds) || profile.ImpulseResponseSeconds is < 0 or > 30)
            {
                issues.Add(new($"renderProfiles/{profile.Id}/impulseResponseSeconds", "Echo fade time must be a finite value in 0..30 seconds."));
            }
            if (profile.AtmosphereIntegrationSegments is < 1 or > 4096)
            {
                issues.Add(new($"renderProfiles/{profile.Id}/atmosphereIntegrationSegments", "Atmosphere integration segment count must be in 1..4096."));
            }
            if (profile.Seed < 0)
            {
                issues.Add(new($"renderProfiles/{profile.Id}/seed", "The deterministic render seed may not be negative."));
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
        if (!double.IsFinite(atmosphere.TemperatureCelsius) || atmosphere.TemperatureCelsius is < -80 or > 80)
        {
            issues.Add(new("atmosphere/temperatureCelsius", "Temperature is outside the supported acoustic range."));
        }

        if (!double.IsFinite(atmosphere.RelativeHumidityPercent) || atmosphere.RelativeHumidityPercent is < 0 or > 100)
        {
            issues.Add(new("atmosphere/relativeHumidityPercent", "Humidity must be between 0 and 100 percent."));
        }

        if (!double.IsFinite(atmosphere.PressureKilopascals) || atmosphere.PressureKilopascals is < 20 or > 120)
        {
            issues.Add(new("atmosphere/pressureKilopascals", "Pressure is outside the supported range."));
        }

        if (atmosphere.Layers.Count > 64)
        {
            issues.Add(new("atmosphere/layers", "The bounded native field supports at most 64 atmosphere layers."));
        }
        if (atmosphere.WindZones.Count > 64)
        {
            issues.Add(new("atmosphere/windZones", "The bounded native field supports at most 64 wind zones."));
        }

        var minimumSoundSpeed = EffectiveSpeedOfSound(
            atmosphere.TemperatureCelsius,
            atmosphere.RelativeHumidityPercent);
        foreach (var layer in atmosphere.Layers)
        {
            if (double.IsFinite(layer.TemperatureCelsius) && double.IsFinite(layer.RelativeHumidityPercent))
            {
                minimumSoundSpeed = Math.Min(
                    minimumSoundSpeed,
                    EffectiveSpeedOfSound(layer.TemperatureCelsius, layer.RelativeHumidityPercent));
            }
        }

        foreach (var zone in atmosphere.WindZones)
        {
            if (!Finite(zone.Transform.PositionMetres) || !Finite(zone.Transform.RotationDegrees) || !Finite(zone.Transform.Scale) ||
                zone.Transform.Scale.X <= 0 || zone.Transform.Scale.Y <= 0 || zone.Transform.Scale.Z <= 0)
            {
                issues.Add(new($"atmosphere/windZones/{zone.Id}/transform", "Wind-zone transforms must be finite and use positive scale."));
            }
            if (!Finite(zone.DimensionsMetres) || zone.DimensionsMetres.X <= 0 ||
                zone.DimensionsMetres.Y <= 0 || zone.DimensionsMetres.Z <= 0)
            {
                issues.Add(new($"atmosphere/windZones/{zone.Id}/dimensionsMetres", "Wind zones require finite positive dimensions."));
            }
            if (!Finite(zone.VelocityMetresPerSecond) || zone.VelocityMetresPerSecond.Length >= 0.8 * minimumSoundSpeed)
            {
                issues.Add(new($"atmosphere/windZones/{zone.Id}/velocity", "Wind velocity must remain below 80% of local sound speed."));
            }

            if (!double.IsFinite(zone.Turbulence) || zone.Turbulence is < 0 or > 1)
            {
                issues.Add(new($"atmosphere/windZones/{zone.Id}/turbulence", "Turbulence must be normalized to 0..1."));
            }
            if (!double.IsFinite(zone.NoiseSeed) || zone.NoiseSeed < 0 ||
                zone.NoiseSeed > 9_007_199_254_740_991d || zone.NoiseSeed != Math.Truncate(zone.NoiseSeed))
            {
                issues.Add(new($"atmosphere/windZones/{zone.Id}/noiseSeed", "Wind-zone noise seeds must be non-negative whole numbers with exact numeric representation."));
            }
        }

        var orderedLayers = atmosphere.Layers.OrderBy(layer => layer.BaseAltitudeMetres).ToList();
        for (var index = 0; index < orderedLayers.Count; index++)
        {
            var layer = orderedLayers[index];
            if (!double.IsFinite(layer.BaseAltitudeMetres) || !double.IsFinite(layer.TopAltitudeMetres) ||
                layer.TopAltitudeMetres <= layer.BaseAltitudeMetres ||
                !double.IsFinite(layer.TemperatureCelsius) || layer.TemperatureCelsius is < -80 or > 80 ||
                !double.IsFinite(layer.RelativeHumidityPercent) || layer.RelativeHumidityPercent is < 0 or > 100 ||
                !double.IsFinite(layer.PressureKilopascals) || layer.PressureKilopascals is < 20 or > 120)
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
                .Concat(scene.Atmosphere.WindZones.Select(value => (value.Id, "atmosphere/windZones")))
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

    private static void ValidateSamplePlayback(SceneDocument scene, Emitter emitter, List<ValidationIssue> issues)
    {
        var emitterPath = $"emitters/{emitter.Id}";
        var graph = emitter.SignalGraph;
        var samples = graph.Nodes.Where(node => node.Kind == SignalNodeKind.Sample).ToArray();
        foreach (var node in graph.Nodes.Where(node => node.Kind != SignalNodeKind.Sample &&
                     (node.SamplePlayback is not null || node.Waveform == WaveformKind.Sample)))
        {
            issues.Add(new($"{emitterPath}/signalGraph/nodes/{node.Id}/samplePlayback",
                "Sample playback belongs on a Sample node, not a primitive or processing node."));
        }

        if (!Enum.IsDefined(emitter.ChannelMapping))
        {
            issues.Add(new($"{emitterPath}/channelMapping", "Unknown source channel mapping."));
        }
        if (samples.Length == 0)
        {
            if (emitter.ChannelMapping != SourceChannelMapping.MonoPoint || emitter.LinkedRightEmitterId is not null)
            {
                issues.Add(new($"{emitterPath}/channelMapping", "Primitive sources use mono point mapping without a linked sample emitter."));
            }
            return;
        }

        var outputs = graph.Nodes.Where(node => node.Kind == SignalNodeKind.Output).ToArray();
        if (samples.Length != 1 || outputs.Length != 1 || graph.Nodes.Count != 2 || graph.Connections.Count != 1 ||
            graph.Connections[0].FromNodeId != samples[0].Id || graph.Connections[0].ToNodeId != outputs[0].Id ||
            graph.Connections[0].FromPort != "out" || graph.Connections[0].ToPort != "in")
        {
            issues.Add(new($"{emitterPath}/signalGraph",
                "A sampled source requires one Sample node connected directly from out to the Output node's in port; mixed or disconnected graphs are not supported."));
        }

        foreach (var node in samples)
        {
            var path = $"{emitterPath}/signalGraph/nodes/{node.Id}/samplePlayback";
            if (node.Waveform is not null and not WaveformKind.Sample)
            {
                issues.Add(new($"{emitterPath}/signalGraph/nodes/{node.Id}/waveform", "A Sample node cannot declare a primitive waveform."));
            }
            if (node.SamplePlayback is not { } playback)
            {
                issues.Add(new(path, "A Sample node requires typed sample playback settings."));
                continue;
            }
            if (!double.IsFinite(playback.StartTimeSeconds) || playback.StartTimeSeconds < 0)
            {
                issues.Add(new($"{path}/startTimeSeconds", "Sample start time must be finite and non-negative."));
            }
            if (!double.IsFinite(playback.OffsetSeconds) || playback.OffsetSeconds < 0)
            {
                issues.Add(new($"{path}/offsetSeconds", "Sample offset must be finite and non-negative."));
            }
            if (playback.ChannelIndex is < 0 or > 1 ||
                (emitter.ChannelMapping != SourceChannelMapping.LinkedDualEmitters && playback.ChannelIndex != 0))
            {
                issues.Add(new($"{path}/channelIndex", "Channel selection is 0 except for a linked stereo pair, which explicitly selects channels 0 and 1."));
            }

            var asset = scene.Assets.FirstOrDefault(candidate =>
                string.Equals(candidate.Sha256, playback.AssetSha256, StringComparison.OrdinalIgnoreCase));
            if (string.IsNullOrWhiteSpace(playback.AssetSha256) || playback.AssetSha256.Length != 64 ||
                !playback.AssetSha256.All(Uri.IsHexDigit) || asset is null || asset.Kind != AssetKind.Audio)
            {
                issues.Add(new($"{path}/assetSha256", "Sample playback must reference a declared Audio asset by its SHA-256 identity."));
            }
            else
            {
                if (asset.SampleRate is null or < 8000 or > 192000 || asset.Channels is not (1 or 2) ||
                    asset.DurationSeconds is not { } duration || !double.IsFinite(duration) || duration <= 0)
                {
                    issues.Add(new($"{path}/assetSha256", "The audio asset must declare its actual sample rate in 8000..192000 Hz, positive duration, and one or two channels."));
                }
                else if (duration * asset.SampleRate.Value * asset.Channels.Value > 16_777_216 + 0.5)
                {
                    issues.Add(new($"{path}/assetSha256", "Sample playback is limited to 16,777,216 decoded samples per asset (64 MiB of float audio)."));
                }
                if (asset.DurationSeconds is { } assetDuration && playback.OffsetSeconds >= assetDuration)
                {
                    issues.Add(new($"{path}/offsetSeconds", "Sample offset must be earlier than the end of the source audio."));
                }
                if ((emitter.ChannelMapping == SourceChannelMapping.MonoPoint && asset.Channels != 1) ||
                    (emitter.ChannelMapping is SourceChannelMapping.LinkedDualEmitters or SourceChannelMapping.NonSpatialStereoBed or SourceChannelMapping.ExplicitMonoFoldDown && asset.Channels != 2))
                {
                    issues.Add(new($"{emitterPath}/channelMapping", "Mono point mapping requires mono audio; stereo audio requires an explicit stereo bed, linked pair, or mono fold-down mapping."));
                }
            }
        }

        if (emitter.ChannelMapping == SourceChannelMapping.LinkedDualEmitters)
        {
            var partner = scene.Emitters.FirstOrDefault(candidate => candidate.Id == emitter.LinkedRightEmitterId);
            if (partner is null || partner.Id == emitter.Id || partner.ChannelMapping != SourceChannelMapping.LinkedDualEmitters ||
                partner.LinkedRightEmitterId != emitter.Id)
            {
                issues.Add(new($"{emitterPath}/linkedRightEmitterId", "Linked stereo playback requires a distinct counterpart with a reciprocal emitter link and linked-pair mapping."));
                return;
            }
            if (emitter.MotionPathId is null || partner.MotionPathId is null || emitter.MotionPathId == partner.MotionPathId)
            {
                issues.Add(new($"{emitterPath}/motionPathId", "Each linked stereo emitter requires its own editable motion path."));
            }
            var partnerSamples = partner.SignalGraph.Nodes.Where(node => node.Kind == SignalNodeKind.Sample).ToArray();
            if (samples.Length != 1 || samples[0].SamplePlayback is not { } left ||
                partnerSamples.Length != 1 || partnerSamples[0].SamplePlayback is not { } right ||
                !string.Equals(left.AssetSha256, right.AssetSha256, StringComparison.OrdinalIgnoreCase) ||
                left.StartTimeSeconds != right.StartTimeSeconds || left.OffsetSeconds != right.OffsetSeconds || left.Loop != right.Loop ||
                left.ChannelIndex is < 0 or > 1 || right.ChannelIndex != 1 - left.ChannelIndex)
            {
                issues.Add(new($"{emitterPath}/linkedRightEmitterId", "Linked stereo emitters must select opposite channels of the same audio asset with matching start time, source offset, and looping."));
            }
        }
        else if (emitter.LinkedRightEmitterId is not null)
        {
            issues.Add(new($"{emitterPath}/linkedRightEmitterId", "Only linked stereo emitters may declare a counterpart."));
        }
    }

    private static void ValidateSampleCacheBudget(SceneDocument scene, List<ValidationIssue> issues)
    {
        var referencedHashes = scene.Emitters.Where(emitter => emitter.Enabled)
            .SelectMany(emitter => emitter.SignalGraph.Nodes)
            .Where(node => node.Kind == SignalNodeKind.Sample && node.SamplePlayback is not null)
            .Select(node => node.SamplePlayback!.AssetSha256)
            .Where(hash => !string.IsNullOrWhiteSpace(hash))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var countedHashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        double decodedSamples = 0;
        foreach (var asset in scene.Assets)
        {
            if (!referencedHashes.Contains(asset.Sha256) || !countedHashes.Add(asset.Sha256) ||
                asset.Kind != AssetKind.Audio || asset.Channels is not (1 or 2) ||
                asset.SampleRate is null or < 8000 or > 192000 ||
                asset.DurationSeconds is not { } duration || !double.IsFinite(duration) || duration <= 0)
            {
                continue;
            }
            // Duration metadata describes an integral number of source frames; round away
            // representation error before counting interleaved samples in the decoder cache.
            decodedSamples += Math.Round(duration * asset.SampleRate.Value) * asset.Channels.Value;
            if (decodedSamples > 16_777_216)
            {
                issues.Add(new("emitters/sampleCache", "Enabled sample sources exceed the shared 64 MiB decoded-audio cache. Disable some sources or use shorter recordings; linked channels sharing one asset count once."));
                return;
            }
        }
    }

    private static void ValidateMaterial(AcousticMaterial material, List<ValidationIssue> issues)
    {
        if (string.IsNullOrWhiteSpace(material.Name))
        {
            issues.Add(new($"materials/{material.Id}/name", "Acoustic material name is required."));
        }

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

    private static IReadOnlyDictionary<string, AssetKind> ValidateAssets(
        IReadOnlyList<AssetRef> assets,
        List<ValidationIssue> issues)
    {
        var result = new Dictionary<string, AssetKind>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < assets.Count; index++)
        {
            var asset = assets[index];
            if (string.IsNullOrEmpty(asset.Sha256) || asset.Sha256.Length != 64 || !asset.Sha256.All(Uri.IsHexDigit))
            {
                issues.Add(new($"assets/{index}/sha256", "Asset identity must be a 64-character SHA-256 value."));
            }
            else if (!result.TryAdd(asset.Sha256, asset.Kind))
            {
                issues.Add(new($"assets/{index}/sha256", "Asset identities must be unique within a project manifest."));
            }
            if (asset.SizeBytes < 0)
            {
                issues.Add(new($"assets/{index}/sizeBytes", "Asset size may not be negative."));
            }
            if (string.IsNullOrWhiteSpace(asset.FileName) || string.IsNullOrWhiteSpace(asset.MediaType))
            {
                issues.Add(new($"assets/{index}", "Asset filename and media type are required."));
            }
        }
        return result;
    }

    private static void ValidateCaptureTakes(
        IReadOnlyList<CaptureTake> takes,
        IReadOnlyList<ReceiverRig> receivers,
        IReadOnlyList<AssetRef> assets,
        List<ValidationIssue> issues)
    {
        if (takes.Count > 4096)
        {
            issues.Add(new("captureTakes", "A project may retain at most 4,096 receiver takes."));
        }

        var receiverIds = receivers.Select(receiver => receiver.Id).ToHashSet();
        var assetsByHash = assets
            .Where(asset => !string.IsNullOrWhiteSpace(asset.Sha256))
            .GroupBy(asset => asset.Sha256, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < takes.Count; index++)
        {
            var take = takes[index];
            var path = $"captureTakes/{index}";
            if (string.IsNullOrWhiteSpace(take.Name))
            {
                issues.Add(new($"{path}/name", "Receiver take name is required."));
            }
            if (!receiverIds.Contains(take.ReceiverRigId))
            {
                issues.Add(new($"{path}/receiverRigId", "Receiver take must reference a receiver rig in this scene."));
            }
            if (!double.IsFinite(take.LeftGainLinear) || take.LeftGainLinear is < 0 or > 4 ||
                !double.IsFinite(take.RightGainLinear) || take.RightGainLinear is < 0 or > 4)
            {
                issues.Add(new($"{path}/gain", "Receiver take channel gains must be finite values from 0 to 4."));
            }
            if (!double.IsFinite(take.TimelineOffsetSeconds) || take.TimelineOffsetSeconds is < 0 or > 3600)
            {
                issues.Add(new($"{path}/timelineOffsetSeconds", "Receiver take start time must be a finite value from 0 to 3,600 seconds."));
            }
            if (!double.IsFinite(take.TrimStartSeconds) || take.TrimStartSeconds is < 0 or > 3600 ||
                (take.TrimEndSeconds is { } trimEnd && (!double.IsFinite(trimEnd) || trimEnd <= take.TrimStartSeconds || trimEnd > 3600)))
            {
                issues.Add(new($"{path}/trim", "Receiver take trim points must define a finite, increasing range within 0 to 3,600 seconds."));
            }

            var source = RequireTakeAsset(take.SourceAssetHash, "sourceAssetHash", path, assetsByHash, issues);
            var left = RequireTakeAsset(take.LeftAssetHash, "leftAssetHash", path, assetsByHash, issues);
            var right = RequireTakeAsset(take.RightAssetHash, "rightAssetHash", path, assetsByHash, issues);
            var receipt = RequireTakeAsset(take.RenderReceiptHash, "renderReceiptHash", path, assetsByHash, issues);
            if (source is not null && source.Channels != 2)
            {
                issues.Add(new($"{path}/sourceAssetHash", "The untouched receiver source must declare two audio channels."));
            }
            if (left is not null && left.Channels != 1)
            {
                issues.Add(new($"{path}/leftAssetHash", "The isolated left asset must declare one audio channel."));
            }
            if (right is not null && right.Channels != 1)
            {
                issues.Add(new($"{path}/rightAssetHash", "The isolated right asset must declare one audio channel."));
            }
            if (receipt is not null && !string.Equals(receipt.MediaType, "application/vnd.pulsegen.render-receipt+json", StringComparison.Ordinal))
            {
                issues.Add(new($"{path}/renderReceiptHash", "Receiver take receipt must use the PulseGen render-receipt media type."));
            }
            if (left?.SampleRate is { } leftRate && right?.SampleRate is { } rightRate && leftRate != rightRate)
            {
                issues.Add(new($"{path}/channelSampleRate", "Left and right receiver assets must use the same sample rate."));
            }
            if (left?.DurationSeconds is { } leftDuration && right?.DurationSeconds is { } rightDuration &&
                Math.Abs(leftDuration - rightDuration) > 0.5 / Math.Max(left.SampleRate ?? 1, right.SampleRate ?? 1))
            {
                issues.Add(new($"{path}/channelDuration", "Left and right receiver assets must contain the same duration."));
            }
            var duration = Math.Min(left?.DurationSeconds ?? double.PositiveInfinity, right?.DurationSeconds ?? double.PositiveInfinity);
            if (double.IsFinite(duration) &&
                (take.TrimStartSeconds >= duration || take.TrimEndSeconds is { } boundedTrimEnd && boundedTrimEnd > duration + 1e-9))
            {
                issues.Add(new($"{path}/trim", "Receiver take trim points must remain inside the captured audio."));
            }
        }
    }

    private static AssetRef? RequireTakeAsset(
        string? hash,
        string field,
        string takePath,
        IReadOnlyDictionary<string, AssetRef> assetsByHash,
        List<ValidationIssue> issues)
    {
        if (string.IsNullOrWhiteSpace(hash) || !assetsByHash.TryGetValue(hash, out var asset) || asset.Kind != AssetKind.Render)
        {
            issues.Add(new($"{takePath}/{field}", "Receiver take must reference a declared immutable Render asset."));
            return null;
        }
        return asset;
    }

    private static void ValidateGeometry(
        AcousticGeometry geometry,
        IReadOnlySet<Guid> materialIds,
        IReadOnlyDictionary<string, AssetKind> assetsByHash,
        List<ValidationIssue> issues)
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
        else if (geometry.Kind == GeometryKind.ImportedMesh &&
            (!assetsByHash.TryGetValue(geometry.MeshAssetHash!, out var assetKind) || assetKind != AssetKind.Mesh))
        {
            issues.Add(new($"geometry/{geometry.Id}/meshAssetHash", "Imported mesh geometry must reference a declared Mesh asset."));
        }
        if (geometry.Kind != GeometryKind.ImportedMesh && geometry.MaterialBindings.Count > 0)
        {
            issues.Add(new($"geometry/{geometry.Id}/materialBindings", "Only imported mesh geometry can bind source material regions."));
        }
        if (geometry.MaterialBindings.Count > 4096)
        {
            issues.Add(new($"geometry/{geometry.Id}/materialBindings", "Imported mesh material bindings exceed the 4,096-region safety cap."));
        }
        var boundSourceMeshes = new HashSet<uint>();
        for (var index = 0; index < geometry.MaterialBindings.Count; index++)
        {
            var binding = geometry.MaterialBindings[index];
            var path = $"geometry/{geometry.Id}/materialBindings/{index}";
            if (!boundSourceMeshes.Add(binding.SourceMeshIndex))
            {
                issues.Add(new($"{path}/sourceMeshIndex", "Each imported source submesh can have only one acoustic material binding."));
            }
            if (!materialIds.Contains(binding.MaterialId))
            {
                issues.Add(new($"{path}/materialId", "Imported mesh material binding references a material that is not in the scene."));
            }
            if (string.IsNullOrWhiteSpace(binding.SourceMeshName) || string.IsNullOrWhiteSpace(binding.SourceMaterialName))
            {
                issues.Add(new(path, "Imported mesh material bindings retain non-empty source mesh and source material names."));
            }
        }
        if (geometry.IsDynamic && (geometry.Occludes || geometry.Reflects))
        {
            issues.Add(new($"geometry/{geometry.Id}/isDynamic", "Dynamic acoustic geometry is retained in the project but requires a future motion adapter.", ValidationSeverity.Warning));
        }
        if (geometry.Kind is not (GeometryKind.Plane or GeometryKind.Box or GeometryKind.ParametricRoom or GeometryKind.ImportedMesh) && (geometry.Occludes || geometry.Reflects))
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
        else if (capsule.ResponseCentresHz.Zip(capsule.ResponseCentresHz.Skip(1), (left, right) => right > left).Any(increasing => !increasing))
        {
            issues.Add(new(path, "Microphone response frequencies must increase strictly."));
        }
        if (!Finite(capsule.RelativeTransform.PositionMetres) || !Finite(capsule.RelativeTransform.RotationDegrees) ||
            !double.IsFinite(capsule.SensitivityDb) || !double.IsFinite(capsule.SelfNoiseDbSpl))
        {
            issues.Add(new(path, "Microphone capsule transform, sensitivity, and self-noise must be finite."));
        }
        else if (capsule.RelativeTransform.PositionMetres.Length > 5)
        {
            issues.Add(new(path, "Microphone capsule offsets must remain within 5 m of the receiver centre."));
        }
        else if (capsule.SelfNoiseDbSpl is < 0 or > 200)
        {
            issues.Add(new(path, "Microphone capsule self-noise must remain between 0 and 200 dB SPL."));
        }
    }

    public static double SpeedOfSound(double temperatureCelsius) => 331.3 + (0.606 * temperatureCelsius);

    public static double EffectiveSpeedOfSound(double temperatureCelsius, double relativeHumidityPercent) =>
        SpeedOfSound(temperatureCelsius) + (Math.Clamp(relativeHumidityPercent, 0, 100) * 0.0124);
}

public sealed class SceneValidationException(IReadOnlyList<ValidationIssue> issues)
    : Exception(string.Join(Environment.NewLine, issues.Select(issue => $"{issue.Path}: {issue.Message}")))
{
    public IReadOnlyList<ValidationIssue> Issues { get; } = issues;
}
