using PulseGen.Contracts;

namespace PulseGen.Editor;

internal sealed record SampleImportChoice(SourceChannelMapping Mapping, bool MuteOtherSources);
internal sealed record SampleSourceImport(SceneDocument Scene, Guid SelectedEmitterId);

internal static class SampleSourceAuthoring
{
    public static SampleSourceImport Add(
        SceneDocument scene, AssetRef asset, SampleImportChoice choice, Guid? copyPathId = null)
    {
        if (asset.Kind != AssetKind.Audio || asset.Channels is not (1 or 2) ||
            asset.DurationSeconds is not > 0)
        {
            throw new InvalidDataException("The source must be a decoded mono or stereo audio asset.");
        }
        if ((asset.Channels == 1 && choice.Mapping != SourceChannelMapping.MonoPoint) ||
            (asset.Channels == 2 && choice.Mapping == SourceChannelMapping.MonoPoint))
        {
            throw new InvalidDataException("Choose how the file's channels will become sources.");
        }

        var assets = scene.Assets.ToList();
        var existingIndex = assets.FindIndex(item => string.Equals(item.Sha256, asset.Sha256, StringComparison.OrdinalIgnoreCase));
        if (existingIndex >= 0)
        {
            if (assets[existingIndex].Kind is not (AssetKind.Audio or AssetKind.Render) ||
                assets[existingIndex].MediaType?.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) != true ||
                assets[existingIndex].MediaType.Length <= 6)
                throw new InvalidDataException("This content is already declared as a different asset type.");
            var existing = assets[existingIndex];
            assets[existingIndex] = asset with
            {
                Kind = existing.Kind,
                License = existing.License ?? asset.License,
                Provenance = existing.Provenance ?? asset.Provenance
            };
        }
        else assets.Add(asset);

        var emitters = scene.Emitters.Select(item => choice.MuteOtherSources ? item with { Enabled = false } : item).ToList();
        var paths = scene.MotionPaths.ToList();
        var template = scene.MotionPaths.FirstOrDefault(path => path.Id == copyPathId);
        var duration = Math.Max(scene.Timeline.DurationSeconds, asset.DurationSeconds.Value);
        var stem = Path.GetFileNameWithoutExtension(asset.FileName);
        if (string.IsNullOrWhiteSpace(stem)) stem = "Audio source";
        var linked = choice.Mapping == SourceChannelMapping.LinkedDualEmitters;
        var leftId = Guid.NewGuid();
        var rightId = Guid.NewGuid();
        for (var channel = 0; channel < (linked ? 2 : 1); channel++)
        {
            var name = SceneSelectionResolver.UniqueName(stem + (linked ? channel == 0 ? " · L" : " · R" : ""), emitters.Select(item => item.Name));
            var offset = new Vector3d(linked ? channel == 0 ? -0.1 : 0.1 : 0, 0, 0);
            var path = template is null ? new MotionPath
            {
                Id = Guid.NewGuid(), Name = name + " path",
                Keyframes =
                [
                    new() { TimeSeconds = 0, PositionMetres = new Vector3d(0, 1.7, 2) + offset },
                    new() { TimeSeconds = Math.Max(0.001, duration), PositionMetres = new Vector3d(0, 1.7, 2) + offset }
                ]
            } : template with
            {
                Id = Guid.NewGuid(), Name = name + " path",
                Keyframes = template.Keyframes.Select(key => key with { PositionMetres = key.PositionMetres + offset }).ToList(),
                RawGesture = template.RawGesture.Select(key => key with { PositionMetres = key.PositionMetres + offset }).ToList(),
                VelocityProfile = [.. template.VelocityProfile]
            };
            paths.Add(path);
            var sampleNodeId = Guid.NewGuid();
            var outputId = Guid.NewGuid();
            emitters.Add(new Emitter
            {
                Id = channel == 0 ? leftId : rightId, Name = name,
                Transform = new Transform { PositionMetres = path.Keyframes[0].PositionMetres },
                MotionPathId = path.Id, ChannelMapping = choice.Mapping,
                LinkedRightEmitterId = linked ? channel == 0 ? rightId : leftId : null,
                GainLinear = 1,
                SignalGraph = new SignalGraph
                {
                    Nodes =
                    [
                        new() { Id = sampleNodeId, Name = asset.FileName, Kind = SignalNodeKind.Sample,
                            SamplePlayback = new SamplePlayback { AssetSha256 = asset.Sha256, ChannelIndex = channel } },
                        new() { Id = outputId, Name = "Source output", Kind = SignalNodeKind.Output }
                    ],
                    Connections = [new() { FromNodeId = sampleNodeId, FromPort = "out", ToNodeId = outputId, ToPort = "in" }]
                }
            });
        }
        var result = scene with
        {
            Assets = assets, Emitters = emitters, MotionPaths = paths,
            Timeline = scene.Timeline with { DurationSeconds = duration }, UpdatedAtUtc = DateTimeOffset.UtcNow
        };
        SceneValidator.EnsureValid(result);
        return new(result, leftId);
    }

    public static SceneDocument UpdatePlayback(SceneDocument scene, Guid emitterId, double start, double offset, bool loop)
    {
        var source = scene.Emitters.Single(item => item.Id == emitterId);
        var targets = new HashSet<Guid> { emitterId };
        if (source.ChannelMapping == SourceChannelMapping.LinkedDualEmitters && source.LinkedRightEmitterId is { } linked)
            targets.Add(linked);
        var result = scene with
        {
            Emitters = scene.Emitters.Select(item => !targets.Contains(item.Id) ? item : item with
            {
                SignalGraph = item.SignalGraph with
                {
                    Nodes = item.SignalGraph.Nodes.Select(node => node.SamplePlayback is not { } sample ? node : node with
                    {
                        SamplePlayback = sample with { StartTimeSeconds = start, OffsetSeconds = offset, Loop = loop }
                    }).ToList()
                }
            }).ToList()
        };
        SceneValidator.EnsureValid(result);
        return result;
    }

    public static string MappingName(Emitter emitter) => emitter.ChannelMapping switch
    {
        SourceChannelMapping.LinkedDualEmitters => "Separate sources · " +
            (emitter.SignalGraph.Nodes.FirstOrDefault(node => node.SamplePlayback is not null)?.SamplePlayback?.ChannelIndex == 1 ? "right" : "left"),
        SourceChannelMapping.NonSpatialStereoBed => "Stereo background",
        SourceChannelMapping.ExplicitMonoFoldDown => "Mono mix · (L + R) / 2",
        _ => "Mono point source"
    };
}
