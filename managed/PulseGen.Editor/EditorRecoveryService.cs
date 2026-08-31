using System.Diagnostics;
using System.Text.Json;
using PulseGen.Contracts;
using PulseGen.Workspace;

namespace PulseGen.Editor;

internal enum EditorRecoveryKind
{
    None,
    UnexpectedExit,
    UnsavedClose
}

internal sealed record EditorRecoveryResult(
    SceneDocument Scene,
    string? ProjectDirectory,
    EditorRecoveryKind Kind,
    DateTimeOffset? AutosavedAtUtc,
    bool ProjectDirectoryMissing)
{
    public bool Recovered => Kind != EditorRecoveryKind.None;
}

internal sealed record EditorAutosaveResult(
    bool Created,
    string? RevisionId,
    string SceneRevisionSha256,
    DateTimeOffset? CreatedAtUtc);

internal sealed class EditorRecoveryService
{
    private readonly WorkspaceIndex index;
    private readonly int processId;
    private readonly DateTimeOffset processStartedAtUtc;
    private readonly Func<int, DateTimeOffset, bool> processInstanceIsRunning;
    private readonly string sessionId;
    private readonly SemaphoreSlim gate = new(1, 1);
    private bool initialized;
    private bool completed;
    private string? currentAutosaveRevisionId;
    private string? lastAutosaveSceneRevision;

    public EditorRecoveryService(
        WorkspaceIndex index,
        int processId,
        DateTimeOffset processStartedAtUtc,
        Func<int, DateTimeOffset, bool>? processInstanceIsRunning = null,
        string? sessionId = null)
    {
        this.index = index ?? throw new ArgumentNullException(nameof(index));
        if (processId < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(processId));
        }
        this.processId = processId;
        this.processStartedAtUtc = processStartedAtUtc.ToUniversalTime();
        this.processInstanceIsRunning = processInstanceIsRunning ?? IsProcessInstanceRunning;
        this.sessionId = sessionId ?? Guid.NewGuid().ToString("N");
    }

    public static EditorRecoveryService CreateForCurrentProcess(WorkspaceIndex index)
    {
        using var process = Process.GetCurrentProcess();
        return new(
            index,
            process.Id,
            new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero));
    }

    public async Task<EditorRecoveryResult> InitializeAsync(
        SceneDocument fallbackScene,
        string? fallbackProjectDirectory,
        CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (initialized)
            {
                throw new InvalidOperationException("Editor recovery was already initialized.");
            }
            SceneValidator.EnsureValid(fallbackScene);
            await index.InitializeAsync(cancellationToken);

            var candidates = await index.GetEditorRecoveryCandidatesAsync(cancellationToken: cancellationToken);
            foreach (var candidate in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (candidate.Session.State == EditorSessionStates.Active &&
                    processInstanceIsRunning(candidate.Session.ProcessId, candidate.Session.ProcessStartedAtUtc))
                {
                    continue;
                }
                if (candidate.Autosave is null)
                {
                    await TryResolveCandidateAsync(candidate.Session.SessionId, EditorSessionStates.Invalid, cancellationToken);
                    continue;
                }

                SceneDocument recoveredScene;
                try
                {
                    recoveredScene = ProjectStore.DeserializeManifest(candidate.Autosave.ManifestJson);
                    if (recoveredScene.ProjectId != candidate.Session.ProjectId ||
                        candidate.Autosave.ProjectId != candidate.Session.ProjectId)
                    {
                        throw new InvalidDataException("Autosave project identity does not match its editor session.");
                    }
                }
                catch (Exception exception) when (exception is JsonException or InvalidDataException or NotSupportedException or ArgumentException)
                {
                    await TryResolveCandidateAsync(candidate.Session.SessionId, EditorSessionStates.Invalid, cancellationToken);
                    continue;
                }

                var projectMissing = false;
                if (candidate.Session.ProjectDirectory is { } projectDirectory)
                {
                    try
                    {
                        var manifestPath = ProjectStore.ResolveManifestPath(projectDirectory);
                        var savedScene = await ProjectStore.LoadAsync(projectDirectory, cancellationToken);
                        var sameRevision = string.Equals(
                            ProjectStore.ComputeSceneRevisionSha256(savedScene),
                            ProjectStore.ComputeSceneRevisionSha256(recoveredScene),
                            StringComparison.Ordinal);
                        var savedAfterAutosave = savedScene.UpdatedAtUtc > recoveredScene.UpdatedAtUtc ||
                            new DateTimeOffset(File.GetLastWriteTimeUtc(manifestPath), TimeSpan.Zero) >= candidate.Autosave.CreatedAtUtc;
                        if (sameRevision || savedAfterAutosave)
                        {
                            await TryResolveCandidateAsync(candidate.Session.SessionId, EditorSessionStates.Superseded, cancellationToken);
                            continue;
                        }
                    }
                    catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
                    {
                        projectMissing = true;
                    }
                    catch (Exception exception) when (exception is JsonException or InvalidDataException or NotSupportedException)
                    {
                        // A valid autosave is safer than a malformed on-disk manifest. Recovery remains in memory.
                    }
                }

                var claimed = await index.ClaimRecoveryAndBeginEditorSessionAsync(
                    candidate.Session.SessionId,
                    sessionId,
                    processId,
                    processStartedAtUtc,
                    recoveredScene.ProjectId,
                    candidate.Session.ProjectDirectory,
                    candidate.Autosave.RevisionId,
                    cancellationToken);
                if (!claimed)
                {
                    continue;
                }
                initialized = true;
                currentAutosaveRevisionId = candidate.Autosave.RevisionId;
                lastAutosaveSceneRevision = ProjectStore.ComputeSceneRevisionSha256(recoveredScene);
                return new(
                    recoveredScene,
                    candidate.Session.ProjectDirectory,
                    candidate.Session.State == EditorSessionStates.ClosedDirty
                        ? EditorRecoveryKind.UnsavedClose
                        : EditorRecoveryKind.UnexpectedExit,
                    candidate.Autosave.CreatedAtUtc,
                    projectMissing);
            }

            await index.BeginEditorSessionAsync(
                sessionId,
                processId,
                processStartedAtUtc,
                fallbackScene.ProjectId,
                fallbackProjectDirectory,
                cancellationToken: cancellationToken);
            initialized = true;
            return new(fallbackScene, fallbackProjectDirectory, EditorRecoveryKind.None, null, false);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<EditorAutosaveResult> AutosaveAsync(
        SceneDocument scene,
        string? projectDirectory,
        bool force = false,
        CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            EnsureActive();
            return await AutosaveUnderGateAsync(scene, projectDirectory, force, cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task ResetContextAsync(
        SceneDocument scene,
        string? projectDirectory,
        CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            EnsureActive();
            SceneValidator.EnsureValid(scene);
            await index.UpdateEditorSessionAsync(sessionId, scene.ProjectId, projectDirectory, null, cancellationToken);
            currentAutosaveRevisionId = null;
            lastAutosaveSceneRevision = null;
        }
        finally
        {
            gate.Release();
        }
    }

    public Task MarkSavedAsync(
        SceneDocument scene,
        string projectDirectory,
        CancellationToken cancellationToken = default) =>
        ResetContextAsync(scene, projectDirectory, cancellationToken);

    public async Task CompleteAsync(
        SceneDocument scene,
        string? projectDirectory,
        bool dirty,
        CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (!initialized || completed)
            {
                return;
            }
            if (dirty)
            {
                _ = await AutosaveUnderGateAsync(scene, projectDirectory, force: true, cancellationToken);
                await index.SetEditorSessionStateAsync(sessionId, EditorSessionStates.ClosedDirty, cancellationToken);
            }
            else
            {
                await index.SetEditorSessionStateAsync(sessionId, EditorSessionStates.Clean, cancellationToken);
            }
            completed = true;
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<EditorAutosaveResult> AutosaveUnderGateAsync(
        SceneDocument scene,
        string? projectDirectory,
        bool force,
        CancellationToken cancellationToken)
    {
        SceneValidator.EnsureValid(scene);
        var sceneRevision = ProjectStore.ComputeSceneRevisionSha256(scene);
        if (!force && currentAutosaveRevisionId is not null &&
            string.Equals(sceneRevision, lastAutosaveSceneRevision, StringComparison.Ordinal))
        {
            return new(false, currentAutosaveRevisionId, sceneRevision, null);
        }

        var autosave = await index.AppendAutosaveAsync(scene, cancellationToken: cancellationToken);
        await index.UpdateEditorSessionAsync(sessionId, scene.ProjectId, projectDirectory, autosave.RevisionId, cancellationToken);
        currentAutosaveRevisionId = autosave.RevisionId;
        lastAutosaveSceneRevision = sceneRevision;
        return new(true, autosave.RevisionId, sceneRevision, autosave.CreatedAtUtc);
    }

    private async Task TryResolveCandidateAsync(string candidateSessionId, string state, CancellationToken cancellationToken)
    {
        try
        {
            await index.SetEditorSessionStateAsync(candidateSessionId, state, cancellationToken);
        }
        catch (KeyNotFoundException)
        {
            // Another editor instance resolved the same stale row first.
        }
    }

    private void EnsureActive()
    {
        if (!initialized || completed)
        {
            throw new InvalidOperationException("Editor recovery session is not active.");
        }
    }

    private static bool IsProcessInstanceRunning(int candidateProcessId, DateTimeOffset candidateStartedAtUtc)
    {
        try
        {
            using var process = Process.GetProcessById(candidateProcessId);
            if (process.HasExited)
            {
                return false;
            }
            var actualStart = new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero);
            return Math.Abs((actualStart - candidateStartedAtUtc.ToUniversalTime()).TotalSeconds) < 2;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }
}
