using Microsoft.Data.Sqlite;
using PulseGen.Contracts;
using PulseGen.Workspace;
using Xunit;

namespace PulseGen.Editor.Tests;

public sealed class EditorRecoveryServiceTests
{
    [Fact]
    public async Task UnexpectedExitRestoresNewestSnapshotWithoutOverwritingTheProject()
    {
        var root = TemporaryDirectory();
        WorkspaceIndex? index = null;
        try
        {
            var project = Path.Combine(root, "Crash.pulseproj");
            var saved = SampleScenes.CreateFlyby() with { Name = "Explicit save" };
            await ProjectStore.SaveAsync(saved, project);
            index = new WorkspaceIndex(Path.Combine(root, "index.sqlite3"));

            var crashed = Service(index, 4101, _ => false);
            _ = await crashed.InitializeAsync(saved, project);
            var edited = saved with { Name = "Recovered edit", UpdatedAtUtc = saved.UpdatedAtUtc.AddMinutes(2) };
            var snapshot = await crashed.AutosaveAsync(edited, project);
            Assert.True(snapshot.Created);

            var restarted = Service(index, 4102, _ => false);
            var recovered = await restarted.InitializeAsync(SampleScenes.CreateSurfaceEcho(), null);

            Assert.True(recovered.Recovered);
            Assert.Equal(EditorRecoveryKind.UnexpectedExit, recovered.Kind);
            Assert.Equal("Recovered edit", recovered.Scene.Name);
            Assert.Equal(project, recovered.ProjectDirectory);
            Assert.Equal("Explicit save", (await ProjectStore.LoadAsync(project)).Name);
            await restarted.CompleteAsync(recovered.Scene, project, dirty: false);
        }
        finally
        {
            index?.Dispose();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task IntentionalCloseWithDirtyWorkRemainsRecoverable()
    {
        var root = TemporaryDirectory();
        WorkspaceIndex? index = null;
        try
        {
            index = new WorkspaceIndex(Path.Combine(root, "index.sqlite3"));
            var initial = SampleScenes.CreateFlyby();
            var first = Service(index, 4201, _ => false);
            _ = await first.InitializeAsync(initial, null);
            var edited = initial with { Name = "Keep after close" };
            await first.CompleteAsync(edited, null, dirty: true);

            var second = Service(index, 4202, _ => false);
            var recovered = await second.InitializeAsync(SampleScenes.CreateSurfaceEcho(), null);

            Assert.Equal(EditorRecoveryKind.UnsavedClose, recovered.Kind);
            Assert.Equal("Keep after close", recovered.Scene.Name);
            Assert.NotNull(recovered.AutosavedAtUtc);
            await second.CompleteAsync(recovered.Scene, null, dirty: false);
        }
        finally
        {
            index?.Dispose();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task SavedCleanSessionDoesNotOfferAStaleAutosave()
    {
        var root = TemporaryDirectory();
        WorkspaceIndex? index = null;
        try
        {
            var project = Path.Combine(root, "Clean.pulseproj");
            var initial = SampleScenes.CreateFlyby();
            await ProjectStore.SaveAsync(initial, project);
            index = new WorkspaceIndex(Path.Combine(root, "index.sqlite3"));
            var first = Service(index, 4301, _ => false);
            _ = await first.InitializeAsync(initial, project);
            var saved = initial with { Name = "Saved revision" };
            _ = await first.AutosaveAsync(saved, project);
            await ProjectStore.SaveAsync(saved, project);
            await first.MarkSavedAsync(saved, project);
            await first.CompleteAsync(saved, project, dirty: false);

            var fallback = SampleScenes.CreateSurfaceEcho();
            var second = Service(index, 4302, _ => false);
            var result = await second.InitializeAsync(fallback, null);

            Assert.False(result.Recovered);
            Assert.Equal(fallback.ProjectId, result.Scene.ProjectId);
            await second.CompleteAsync(result.Scene, null, dirty: false);
        }
        finally
        {
            index?.Dispose();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task NewerExplicitDiskSaveSupersedesAnOlderDifferentSnapshot()
    {
        var root = TemporaryDirectory();
        WorkspaceIndex? index = null;
        try
        {
            var project = Path.Combine(root, "Committed.pulseproj");
            var initial = SampleScenes.CreateFlyby();
            await ProjectStore.SaveAsync(initial, project);
            index = new WorkspaceIndex(Path.Combine(root, "index.sqlite3"));
            var first = Service(index, 4351, _ => false);
            _ = await first.InitializeAsync(initial, project);
            var autosaved = initial with { Name = "Autosave before explicit save" };
            _ = await first.AutosaveAsync(autosaved, project);
            var committed = initial with { Name = "Newer explicit disk save", UpdatedAtUtc = initial.UpdatedAtUtc.AddMinutes(1) };
            await ProjectStore.SaveAsync(committed, project);

            var fallback = SampleScenes.CreateSurfaceEcho();
            var second = Service(index, 4352, _ => false);
            var result = await second.InitializeAsync(fallback, null);

            Assert.False(result.Recovered);
            Assert.Equal(fallback.ProjectId, result.Scene.ProjectId);
            await second.CompleteAsync(result.Scene, null, dirty: false);
        }
        finally
        {
            index?.Dispose();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RunningEditorSessionIsNeverTakenOver()
    {
        var root = TemporaryDirectory();
        WorkspaceIndex? index = null;
        try
        {
            index = new WorkspaceIndex(Path.Combine(root, "index.sqlite3"));
            var initial = SampleScenes.CreateFlyby();
            var first = Service(index, 4401, processId => processId == 4401);
            _ = await first.InitializeAsync(initial, null);
            _ = await first.AutosaveAsync(initial with { Name = "Still open elsewhere" }, null);

            var fallback = SampleScenes.CreateSurfaceEcho();
            var second = Service(index, 4402, processId => processId == 4401);
            var result = await second.InitializeAsync(fallback, null);

            Assert.False(result.Recovered);
            Assert.Equal(fallback.ProjectId, result.Scene.ProjectId);
            await second.CompleteAsync(result.Scene, null, dirty: false);
        }
        finally
        {
            index?.Dispose();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ConcurrentRestartsCannotClaimTheSameSnapshotTwice()
    {
        var root = TemporaryDirectory();
        WorkspaceIndex? index = null;
        try
        {
            index = new WorkspaceIndex(Path.Combine(root, "index.sqlite3"));
            var initial = SampleScenes.CreateFlyby();
            var crashed = Service(index, 4451, _ => false);
            _ = await crashed.InitializeAsync(initial, null);
            _ = await crashed.AutosaveAsync(initial with { Name = "Claim only once" }, null);

            var firstRestart = Service(index, 4452, processId => processId is 4452 or 4453);
            var secondRestart = Service(index, 4453, processId => processId is 4452 or 4453);
            var results = await Task.WhenAll(
                firstRestart.InitializeAsync(SampleScenes.CreateSurfaceEcho(), null),
                secondRestart.InitializeAsync(SampleScenes.CreateMovingReceiver(), null));

            Assert.Single(results, result => result.Recovered);
            Assert.Equal("Claim only once", results.Single(result => result.Recovered).Scene.Name);
            await firstRestart.CompleteAsync(results[0].Scene, results[0].ProjectDirectory, dirty: false);
            await secondRestart.CompleteAsync(results[1].Scene, results[1].ProjectDirectory, dirty: false);
        }
        finally
        {
            index?.Dispose();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task CorruptSnapshotIsQuarantinedAndNeverLoaded()
    {
        var root = TemporaryDirectory();
        WorkspaceIndex? index = null;
        try
        {
            var database = Path.Combine(root, "index.sqlite3");
            index = new WorkspaceIndex(database);
            var initial = SampleScenes.CreateFlyby();
            var first = Service(index, 4501, _ => false);
            _ = await first.InitializeAsync(initial, null);
            var autosave = await first.AutosaveAsync(initial with { Name = "Will be corrupted" }, null);

            await using (var connection = new SqliteConnection($"Data Source={database};Pooling=False"))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = "UPDATE autosave_revision SET manifest_json = '{' WHERE revision_id = $revision;";
                command.Parameters.AddWithValue("$revision", autosave.RevisionId!);
                Assert.Equal(1, await command.ExecuteNonQueryAsync());
            }

            var fallback = SampleScenes.CreateSurfaceEcho();
            var second = Service(index, 4502, _ => false);
            var result = await second.InitializeAsync(fallback, null);

            Assert.False(result.Recovered);
            Assert.Equal(fallback.ProjectId, result.Scene.ProjectId);
            Assert.Single(await index.GetEditorRecoveryCandidatesAsync());
            await second.CompleteAsync(result.Scene, null, dirty: false);
        }
        finally
        {
            index?.Dispose();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task PreCancelledAutosaveDoesNotPublishAPartialRevision()
    {
        var root = TemporaryDirectory();
        WorkspaceIndex? index = null;
        try
        {
            index = new WorkspaceIndex(Path.Combine(root, "index.sqlite3"));
            var initial = SampleScenes.CreateFlyby();
            var service = Service(index, 4601, _ => false);
            _ = await service.InitializeAsync(initial, null);
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                service.AutosaveAsync(initial with { Name = "Cancelled" }, null, cancellationToken: cancellation.Token));
            Assert.Null(await index.GetLatestAutosaveAsync(initial.ProjectId));
            await service.CompleteAsync(initial, null, dirty: false);
        }
        finally
        {
            index?.Dispose();
            Directory.Delete(root, recursive: true);
        }
    }

    private static EditorRecoveryService Service(
        WorkspaceIndex index,
        int processId,
        Func<int, bool> processIsRunning) =>
        new(
            index,
            processId,
            new DateTimeOffset(2026, 8, 30, 20, processId % 60, 0, TimeSpan.Zero),
            (candidate, _) => processIsRunning(candidate),
            Guid.NewGuid().ToString("N"));

    private static string TemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"pulsegen-editor-recovery-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }
}
