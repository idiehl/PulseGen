using PulseGen.Contracts;
using PulseGen.Workspace;
using Xunit;

namespace PulseGen.Workspace.Tests;

public sealed class WorkspaceIndexTests
{
    [Fact]
    public async Task AutosavesAndSyncJournalRemainDisposableLocalState()
    {
        var root = Path.Combine(Path.GetTempPath(), $"pulsegen-index-{Guid.NewGuid():N}");
        WorkspaceIndex? index = null;
        try
        {
            index = new WorkspaceIndex(Path.Combine(root, "index.sqlite3"));
            await index.InitializeAsync();
            var scene = SampleScenes.CreateFlyby();
            var first = await index.AppendAutosaveAsync(scene, keepLatest: 2);
            _ = await index.AppendAutosaveAsync(scene with { Name = "Changed once" }, keepLatest: 2);
            var latest = await index.AppendAutosaveAsync(scene with { Name = "Changed twice" }, keepLatest: 2);

            var recovered = await index.GetLatestAutosaveAsync(scene.ProjectId);
            Assert.NotNull(recovered);
            Assert.Equal(latest.RevisionId, recovered.RevisionId);
            Assert.NotEqual(first.RevisionId, recovered.RevisionId);

            var journalId = await index.EnqueueSyncAsync(scene.ProjectId, "base-17", "project.revision.create", new { scene.Name });
            await index.MarkSyncStateAsync(journalId, "conflict");
            await Assert.ThrowsAsync<KeyNotFoundException>(() => index.MarkSyncStateAsync(journalId + 99, "complete"));
        }
        finally
        {
            index?.Dispose();
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ContentAddressedAssetIsVerifiedBeforeCacheIndexing()
    {
        var root = Path.Combine(Path.GetTempPath(), $"pulsegen-assets-{Guid.NewGuid():N}");
        var project = Path.Combine(root, "Assets.pulseproj");
        WorkspaceIndex? index = null;
        try
        {
            await ProjectStore.SaveAsync(SampleScenes.CreateFlyby(), project);
            var source = Path.Combine(root, "impulse.bin");
            await File.WriteAllBytesAsync(source, [1, 2, 3, 4, 5]);
            var asset = await ProjectStore.ImportAssetAsync(project, source, AssetKind.ImpulseResponse, "application/octet-stream", "test fixture");
            var stored = ProjectStore.ResolveAssetPath(project, asset.Sha256);
            Assert.True(File.Exists(stored));

            index = new WorkspaceIndex(Path.Combine(root, "index.sqlite3"));
            await index.InitializeAsync();
            await index.UpsertAssetAsync(asset, stored);
            await File.WriteAllBytesAsync(stored, [9, 9]);
            await Assert.ThrowsAsync<InvalidDataException>(() => index.UpsertAssetAsync(asset, stored));
        }
        finally
        {
            index?.Dispose();
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
