using PulseGen.Contracts;
using Xunit;

namespace PulseGen.Contracts.Tests;

public sealed class AudioAssetRoleTests
{
    [Theory]
    [InlineData(AssetKind.Audio)]
    [InlineData(AssetKind.Render)]
    public void OneAudioHashCanBeBothASampleSourceAndAnUntouchedReceiverTake(AssetKind kind)
    {
        var scene = CreateSharedAudioScene(kind);
        Assert.Empty(SceneValidator.Validate(scene));
        var restored = ProjectStore.DeserializeManifest(ProjectStore.SerializeManifest(scene));
        var sampleHash = restored.Emitters[0].SignalGraph.Nodes[0].SamplePlayback!.AssetSha256;
        Assert.Equal(restored.CaptureTakes[0].SourceAssetHash, sampleHash);
        Assert.Single(restored.Assets, asset => asset.Sha256 == sampleHash);
        Assert.Equal(kind, restored.Assets[0].Kind);
    }

    [Theory]
    [InlineData(AssetKind.Audio)]
    [InlineData(AssetKind.Render)]
    public void SharedAudioRolesDoNotPermitAnAudioAssetToBecomeAGeometryMesh(AssetKind kind)
    {
        var scene = CreateSharedAudioScene(kind);
        scene.Geometry.Add(new AcousticGeometry
        {
            Id = Guid.NewGuid(), Name = "Not a mesh", Kind = GeometryKind.ImportedMesh,
            MeshAssetHash = scene.Assets[0].Sha256, MaterialId = scene.Materials[0].Id
        });
        AssertError(scene, "/meshAssetHash");
    }

    [Theory]
    [InlineData(AssetKind.Mesh, "audio/wav")]
    [InlineData(AssetKind.Audio, "application/json")]
    [InlineData(AssetKind.Render, "application/vnd.pulsegen.render-receipt+json")]
    [InlineData(AssetKind.Audio, "audio/")]
    public void SharedAudioReferencesStillRequireAnAudioRoleAndMediaType(AssetKind kind, string mediaType)
    {
        var scene = CreateSharedAudioScene(AssetKind.Audio);
        scene.Assets[0] = scene.Assets[0] with { Kind = kind, MediaType = mediaType };
        AssertError(scene, "/samplePlayback/assetSha256");
        AssertError(scene, "/sourceAssetHash");

        scene.Assets[1] = scene.Assets[1] with { Kind = kind, MediaType = mediaType };
        scene.Assets[2] = scene.Assets[2] with { Kind = kind, MediaType = mediaType };
        AssertError(scene, "/leftAssetHash");
        AssertError(scene, "/rightAssetHash");
    }

    [Theory]
    [InlineData(AssetKind.Audio, "application/vnd.pulsegen.render-receipt+json")]
    [InlineData(AssetKind.Render, "audio/wav")]
    [InlineData(AssetKind.Mesh, "application/vnd.pulsegen.render-receipt+json")]
    public void ReceiptStillRequiresRenderRoleAndExactReceiptMediaType(AssetKind kind, string mediaType)
    {
        var scene = CreateSharedAudioScene(AssetKind.Audio);
        scene.Assets[3] = scene.Assets[3] with { Kind = kind, MediaType = mediaType };
        AssertError(scene, "/renderReceiptHash");
    }

    private static void AssertError(SceneDocument scene, string suffix) =>
        Assert.Contains(SceneValidator.Validate(scene), issue => issue.Severity == ValidationSeverity.Error &&
            issue.Path.EndsWith(suffix, StringComparison.Ordinal));

    private static SceneDocument CreateSharedAudioScene(AssetKind kind)
    {
        var scene = SampleScenes.CreateFlyby();
        var sourceHash = new string('a', 64);
        var leftHash = new string('b', 64);
        var rightHash = new string('c', 64);
        var receiptHash = new string('d', 64);
        var audio = new AssetRef
        {
            Sha256 = sourceHash, Kind = kind, FileName = "recording.wav", MediaType = "audio/wav",
            SizeBytes = 384044, SampleRate = 48000, Channels = 2, DurationSeconds = 1
        };
        scene.Assets.AddRange(
        [
            audio,
            audio with { Sha256 = leftHash, FileName = "left.wav", Channels = 1, SizeBytes = 192044 },
            audio with { Sha256 = rightHash, FileName = "right.wav", Channels = 1, SizeBytes = 192044 },
            new AssetRef
            {
                Sha256 = receiptHash, Kind = AssetKind.Render, FileName = "receipt.json",
                MediaType = "application/vnd.pulsegen.render-receipt+json", SizeBytes = 100
            }
        ]);
        var emitter = scene.Emitters[0];
        emitter.SignalGraph.Nodes[0] = emitter.SignalGraph.Nodes[0] with
        {
            Kind = SignalNodeKind.Sample, Waveform = WaveformKind.Sample,
            SamplePlayback = new SamplePlayback { AssetSha256 = sourceHash }, NumericParameters = []
        };
        scene.Emitters[0] = emitter with { ChannelMapping = SourceChannelMapping.NonSpatialStereoBed };
        scene.CaptureTakes.Add(new CaptureTake
        {
            Id = Guid.NewGuid(), ReceiverRigId = scene.ReceiverRigs[0].Id, Name = "Identical stereo source",
            SourceAssetHash = sourceHash, LeftAssetHash = leftHash, RightAssetHash = rightHash,
            RenderReceiptHash = receiptHash
        });
        return scene;
    }
}
