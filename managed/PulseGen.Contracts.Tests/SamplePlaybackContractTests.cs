using System.Text.Json;
using PulseGen.Contracts;
using Xunit;

namespace PulseGen.Contracts.Tests;

public sealed class SamplePlaybackContractTests
{
    [Theory]
    [InlineData(SourceChannelMapping.MonoPoint, 1)]
    [InlineData(SourceChannelMapping.ExplicitMonoFoldDown, 2)]
    [InlineData(SourceChannelMapping.NonSpatialStereoBed, 2)]
    [InlineData(SourceChannelMapping.LinkedDualEmitters, 2)]
    public async Task SampleTimingAndExplicitMappingSurviveProjectPersistence(SourceChannelMapping mapping, int channels)
    {
        var scene = CreateScene(mapping, channels);
        var directory = Path.Combine(Path.GetTempPath(), $"pulse-sample-contract-{Guid.NewGuid():N}.pulseproj");
        try
        {
            await ProjectStore.SaveAsync(scene, directory);
            var loaded = await ProjectStore.LoadAsync(directory);

            Assert.Equal(ContractVersions.SceneDocument, loaded.SchemaVersion);
            Assert.Equal(scene.Assets[0], loaded.Assets[0]);
            Assert.Equal(scene.Emitters.Count, loaded.Emitters.Count);
            foreach (var (original, restored) in scene.Emitters.Zip(loaded.Emitters))
            {
                Assert.Equal(original.SignalGraph.Nodes[0].SamplePlayback, restored.SignalGraph.Nodes[0].SamplePlayback);
                Assert.Equal(original.ChannelMapping, restored.ChannelMapping);
                Assert.Equal(original.LinkedRightEmitterId, restored.LinkedRightEmitterId);
                Assert.Equal(original.MotionPathId, restored.MotionPathId);
            }
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void LegacyPrimitiveManifestDoesNotGainSampleFields()
    {
        var json = ProjectStore.SerializeManifest(SampleScenes.CreateFlyby());
        Assert.DoesNotContain("samplePlayback", json, StringComparison.Ordinal);
        Assert.All(ProjectStore.DeserializeManifest(json).Emitters.SelectMany(emitter => emitter.SignalGraph.Nodes),
            node => Assert.Null(node.SamplePlayback));
    }

    [Fact]
    public void PlaybackTimingIsIndependentOfTheMotionPathEnd()
    {
        var scene = CreateScene();
        var end = scene.MotionPaths[0].Keyframes[^1].TimeSeconds;
        SetPlayback(scene, Playback(scene) with { StartTimeSeconds = end + 5, OffsetSeconds = 0, Loop = false });
        Assert.Empty(SceneValidator.Validate(scene));
    }

    [Fact]
    public void SampleRequiresTypedSettingsAndDoesNotAllowPrimitiveWaveformDisguises()
    {
        var scene = CreateScene();
        scene.Emitters[0].SignalGraph.Nodes[0] = scene.Emitters[0].SignalGraph.Nodes[0] with { SamplePlayback = null };
        AssertError(scene, "/samplePlayback");

        scene = CreateScene();
        scene.Emitters[0].SignalGraph.Nodes[0] = scene.Emitters[0].SignalGraph.Nodes[0] with { Kind = SignalNodeKind.Oscillator };
        AssertError(scene, "/samplePlayback");

        scene = CreateScene();
        scene.Emitters[0].SignalGraph.Nodes[0] = scene.Emitters[0].SignalGraph.Nodes[0] with { Waveform = WaveformKind.Sine };
        AssertError(scene, "/waveform");
    }

    [Fact]
    public void SampleReferenceRequiresAnExistingAudioAssetAndDecodeMetadata()
    {
        var scene = CreateScene();
        SetPlayback(scene, Playback(scene) with { AssetSha256 = new string('b', 64) });
        AssertError(scene, "/assetSha256");

        scene = CreateScene();
        scene.Assets[0] = scene.Assets[0] with { Kind = AssetKind.Render };
        AssertError(scene, "/assetSha256");

        foreach (var replacement in new[]
        {
            CreateScene().Assets[0] with { SampleRate = null },
            CreateScene().Assets[0] with { SampleRate = 0 },
            CreateScene().Assets[0] with { SampleRate = 7999 },
            CreateScene().Assets[0] with { SampleRate = 192001 },
            CreateScene().Assets[0] with { Channels = null },
            CreateScene().Assets[0] with { Channels = 3 },
            CreateScene().Assets[0] with { DurationSeconds = null },
            CreateScene().Assets[0] with { DurationSeconds = 0 },
            CreateScene().Assets[0] with { DurationSeconds = double.NaN },
            CreateScene().Assets[0] with { DurationSeconds = 1000 }
        })
        {
            scene = CreateScene();
            scene.Assets[0] = replacement;
            AssertError(scene, "/assetSha256");
        }
    }

    [Theory]
    [InlineData(-1, 0, "startTimeSeconds")]
    [InlineData(double.NaN, 0, "startTimeSeconds")]
    [InlineData(double.PositiveInfinity, 0, "startTimeSeconds")]
    [InlineData(0, -1, "offsetSeconds")]
    [InlineData(0, double.NaN, "offsetSeconds")]
    [InlineData(0, double.PositiveInfinity, "offsetSeconds")]
    [InlineData(0, 4, "offsetSeconds")]
    public void SampleTimesRejectNonFiniteNegativeAndExhaustedOffsets(double start, double offset, string field)
    {
        var scene = CreateScene();
        SetPlayback(scene, Playback(scene) with { StartTimeSeconds = start, OffsetSeconds = offset });
        AssertError(scene, $"/{field}");
    }

    [Theory]
    [InlineData(SourceChannelMapping.MonoPoint, 2)]
    [InlineData(SourceChannelMapping.ExplicitMonoFoldDown, 1)]
    [InlineData(SourceChannelMapping.NonSpatialStereoBed, 1)]
    [InlineData(SourceChannelMapping.LinkedDualEmitters, 1)]
    [InlineData((SourceChannelMapping)100, 2)]
    public void MappingMustMatchTheActualSourceChannels(SourceChannelMapping mapping, int channels)
    {
        AssertError(CreateScene(mapping, channels), "/channelMapping");
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1)]
    [InlineData(2)]
    public void NonPairedSourceCannotSilentlySelectOneStereoChannel(int channel)
    {
        var scene = CreateScene(SourceChannelMapping.NonSpatialStereoBed, 2);
        SetPlayback(scene, Playback(scene) with { ChannelIndex = channel });
        AssertError(scene, "/channelIndex");
    }

    [Theory]
    [InlineData("missing-link")]
    [InlineData("self-link")]
    [InlineData("one-way-link")]
    [InlineData("wrong-mapping")]
    [InlineData("same-channel")]
    [InlineData("different-asset")]
    [InlineData("different-start")]
    [InlineData("different-offset")]
    [InlineData("different-loop")]
    public void LinkedPairMustRetainReciprocalIdentityAndAlignedPlayback(string corruption)
    {
        var scene = CreateScene(SourceChannelMapping.LinkedDualEmitters, 2);
        var right = scene.Emitters[1];
        var playback = Playback(scene, 1);
        switch (corruption)
        {
            case "missing-link": scene.Emitters[0] = scene.Emitters[0] with { LinkedRightEmitterId = Guid.NewGuid() }; break;
            case "self-link": scene.Emitters[0] = scene.Emitters[0] with { LinkedRightEmitterId = scene.Emitters[0].Id }; break;
            case "one-way-link": scene.Emitters[1] = right with { LinkedRightEmitterId = null }; break;
            case "wrong-mapping": scene.Emitters[1] = right with { ChannelMapping = SourceChannelMapping.NonSpatialStereoBed }; break;
            case "same-channel": SetPlayback(scene, playback with { ChannelIndex = 0 }, 1); break;
            case "different-asset":
                scene.Assets.Add(scene.Assets[0] with { Sha256 = new string('b', 64) });
                SetPlayback(scene, playback with { AssetSha256 = scene.Assets[1].Sha256 }, 1);
                break;
            case "different-start": SetPlayback(scene, playback with { StartTimeSeconds = 2 }, 1); break;
            case "different-offset": SetPlayback(scene, playback with { OffsetSeconds = 1 }, 1); break;
            case "different-loop": SetPlayback(scene, playback with { Loop = false }, 1); break;
        }
        AssertError(scene, "/linkedRightEmitterId");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void LinkedPairNeedsIndependentlyEditablePaths(bool shared)
    {
        var scene = CreateScene(SourceChannelMapping.LinkedDualEmitters, 2);
        scene.Emitters[1] = scene.Emitters[1] with { MotionPathId = shared ? scene.Emitters[0].MotionPathId : null };
        AssertError(scene, "/motionPathId");
    }

    [Theory]
    [InlineData("disconnected")]
    [InlineData("mixed")]
    [InlineData("multiple-samples")]
    [InlineData("wrong-port")]
    [InlineData("reversed")]
    public void UnsupportedSampleGraphDoesNotSilentlyDiscardSourcesOrProcessing(string corruption)
    {
        var scene = CreateScene();
        var graph = scene.Emitters[0].SignalGraph;
        switch (corruption)
        {
            case "disconnected": graph.Connections.Clear(); break;
            case "mixed": graph.Nodes.Add(new SignalNode { Id = Guid.NewGuid(), Name = "Extra oscillator", Kind = SignalNodeKind.Oscillator }); break;
            case "multiple-samples": graph.Nodes.Add(graph.Nodes[0] with { Id = Guid.NewGuid() }); break;
            case "wrong-port": graph.Connections[0] = graph.Connections[0] with { FromPort = "ignored" }; break;
            case "reversed": graph.Connections[0] = graph.Connections[0] with { FromNodeId = graph.Nodes[1].Id, ToNodeId = graph.Nodes[0].Id }; break;
        }
        AssertError(scene, "/signalGraph");
        Assert.Throws<SceneValidationException>(() => ProjectStore.SerializeManifest(scene));
    }

    [Fact]
    public void SampleSchemaDeclaresRequiredHashAndOptionalBackwardCompatibleSettings()
    {
        using var schema = JsonDocument.Parse(SchemaCatalog.ReadSceneSchema());
        var definition = schema.RootElement.GetProperty("$defs").GetProperty("samplePlayback");
        Assert.Equal("assetSha256", Assert.Single(definition.GetProperty("required").EnumerateArray()).GetString());
        Assert.False(definition.GetProperty("additionalProperties").GetBoolean());
        Assert.Equal(0, definition.GetProperty("properties").GetProperty("channelIndex").GetProperty("default").GetInt32());
    }

    [Fact]
    public void ActiveSampleCacheAcceptsExactBudgetAndRejectsOneMoreFrame()
    {
        var scene = CreateScene();
        scene.Assets[0] = scene.Assets[0] with { DurationSeconds = 8_388_608d / 48000 };
        var secondAsset = scene.Assets[0] with { Sha256 = new string('b', 64) };
        AddAnotherMonoSource(scene, secondAsset);
        Assert.Empty(SceneValidator.Validate(scene));

        scene.Assets[1] = secondAsset with { DurationSeconds = 8_388_609d / 48000 };
        AssertError(scene, "emitters/sampleCache");
        Assert.Throws<SceneValidationException>(() => ProjectStore.SerializeManifest(scene));
    }

    [Fact]
    public void DisabledSourcesDoNotConsumeActiveCacheButEnablingThemIsValidated()
    {
        var scene = CreateScene();
        scene.Assets[0] = scene.Assets[0] with { DurationSeconds = 12_000_000d / 48000 };
        AddAnotherMonoSource(scene, scene.Assets[0] with { Sha256 = new string('b', 64) });
        scene.Emitters[1] = scene.Emitters[1] with { Enabled = false };
        Assert.Empty(SceneValidator.Validate(scene));

        scene.Emitters[1] = scene.Emitters[1] with { Enabled = true };
        AssertError(scene, "emitters/sampleCache");
    }

    [Fact]
    public void LinkedStereoChannelsShareOneDecodedCacheEntry()
    {
        var scene = CreateScene(SourceChannelMapping.LinkedDualEmitters, 2);
        scene.Assets[0] = scene.Assets[0] with { DurationSeconds = 8_388_608d / 48000 };
        SetPlayback(scene, Playback(scene, 1) with { AssetSha256 = scene.Assets[0].Sha256.ToUpperInvariant() }, 1);
        Assert.Empty(SceneValidator.Validate(scene));
    }

    private static void AddAnotherMonoSource(SceneDocument scene, AssetRef asset)
    {
        scene.Assets.Add(asset);
        var original = scene.Emitters[0];
        scene.Emitters.Add(original with
        {
            Id = Guid.NewGuid(), Name = "Another sampled source",
            SignalGraph = original.SignalGraph with
            {
                Nodes = original.SignalGraph.Nodes.Select(node => node.SamplePlayback is not { } sample ? node : node with
                { SamplePlayback = sample with { AssetSha256 = asset.Sha256 } }).ToList()
            }
        });
    }

    private static void AssertError(SceneDocument scene, string suffix) =>
        Assert.Contains(SceneValidator.Validate(scene), issue => issue.Severity == ValidationSeverity.Error &&
            issue.Path.EndsWith(suffix, StringComparison.Ordinal));

    private static SamplePlayback Playback(SceneDocument scene, int emitter = 0) => scene.Emitters[emitter].SignalGraph.Nodes[0].SamplePlayback!;

    private static void SetPlayback(SceneDocument scene, SamplePlayback playback, int emitter = 0) =>
        scene.Emitters[emitter].SignalGraph.Nodes[0] = scene.Emitters[emitter].SignalGraph.Nodes[0] with { SamplePlayback = playback };

    private static SceneDocument CreateScene(SourceChannelMapping mapping = SourceChannelMapping.MonoPoint, int channels = 1)
    {
        var scene = SampleScenes.CreateFlyby();
        var asset = new AssetRef
        {
            Sha256 = new string('a', 64), Kind = AssetKind.Audio, FileName = "sample.wav", MediaType = "audio/wav",
            SizeBytes = 768044, SampleRate = 48000, Channels = channels, DurationSeconds = 4
        };
        scene.Assets.Add(asset);
        var emitter = scene.Emitters[0];
        var source = emitter.SignalGraph.Nodes[0] with
        {
            Kind = SignalNodeKind.Sample, Waveform = WaveformKind.Sample, NumericParameters = [],
            SamplePlayback = new SamplePlayback { AssetSha256 = asset.Sha256, StartTimeSeconds = 0.5, OffsetSeconds = 0.25, Loop = true }
        };
        emitter.SignalGraph.Nodes[0] = source;
        scene.Emitters[0] = emitter with { ChannelMapping = mapping };
        if (mapping == SourceChannelMapping.LinkedDualEmitters)
        {
            var partnerId = Guid.NewGuid();
            var partnerPath = scene.MotionPaths[0] with { Id = Guid.NewGuid(), Name = "Right channel path" };
            scene.MotionPaths.Add(partnerPath);
            var rightSource = source with { Id = Guid.NewGuid(), SamplePlayback = source.SamplePlayback with { ChannelIndex = 1 } };
            var rightOutput = emitter.SignalGraph.Nodes[1] with { Id = Guid.NewGuid() };
            scene.Emitters[0] = scene.Emitters[0] with { LinkedRightEmitterId = partnerId };
            scene.Emitters.Add(emitter with
            {
                Id = partnerId, Name = "Right channel", ChannelMapping = mapping, LinkedRightEmitterId = emitter.Id,
                MotionPathId = partnerPath.Id,
                SignalGraph = new SignalGraph
                {
                    Nodes = [rightSource, rightOutput],
                    Connections = [new SignalConnection { FromNodeId = rightSource.Id, FromPort = "out", ToNodeId = rightOutput.Id, ToPort = "in" }]
                }
            });
        }
        return scene;
    }
}
