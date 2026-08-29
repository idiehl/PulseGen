using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using PulseGen.Atlas.Client;
using PulseGen.Contracts;
using PulseGen.Engine.Client;
using PulseGen.ModelHost.Client;
using PulseGen.Workspace;
using System.Globalization;
using System.Text.Json;

namespace PulseGen.Editor;

public sealed partial class MainWindow : Window
{
    private SceneDocument scene = SampleScenes.CreateFlyby();
    private readonly DispatcherTimer previewTimer;
    private DateTimeOffset previewStarted;
    private int selectedKeyframeIndex;
    private bool initializingControls = true;
    private bool sceneDirty;
    private string? lastRenderDirectory;
    private string? lastProjectDirectory;
    private CancellationTokenSource? analysisCancellation;
    private CancellationTokenSource? syncCancellation;
    private NativeSceneRenderer.PreviewSession? audioPreview;
    private readonly HttpClient atlasHttp = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        AutomaticDecompression = System.Net.DecompressionMethods.All
    }) { Timeout = Timeout.InfiniteTimeSpan };
    private readonly WorkspaceIndex workspaceIndex = WorkspaceIndex.CreateDefault();
    private AtlasOidcClient? atlasIdentity;
    private AtlasLibraryClient? atlasLibrary;
    private Guid? atlasBaseRevisionId;
    private string? atlasBaseETag;

    public MainWindow()
    {
        InitializeComponent();
        Viewport.PlayheadChanged += (_, time) => UpdatePlayhead(time);
        Viewport.PathPointSelected += (_, index) => SelectKeyframe(index);
        Viewport.SceneEdited += (_, message) =>
        {
            StopPreviewTransport();
            sceneDirty = true;
            SelectKeyframe(Viewport.SelectedKeyframeIndex);
            StatusText.Text = message;
            GestureButton.Content = "●  Gesture";
        };
        previewTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Render, OnPreviewTick);
        previewTimer.Stop();
        WaveformBox.ItemsSource = new[] { "Sine", "Triangle", "Saw", "Square", "Pulse", "WhiteNoise", "PinkNoise", "BrownNoise", "Impulse", "Additive", "Turbulence" };
        ApplySceneToControls(scene, projectDirectory: null);
        initializingControls = false;
    }

    private void ApplySceneToControls(SceneDocument loadedScene, string? projectDirectory)
    {
        scene = loadedScene;
        lastProjectDirectory = projectDirectory;
        lastRenderDirectory = null;
        atlasBaseRevisionId = null;
        atlasBaseETag = null;
        sceneDirty = false;
        Viewport.Scene = scene;
        Viewport.TimeSeconds = 0;
        Title = $"PulseGen — {scene.Name}";
        SceneNameText.Text = $"{scene.Name} · scene v{scene.SchemaVersion}";
        EmitterCountText.Text = $"{scene.Emitters.Count} source{(scene.Emitters.Count == 1 ? string.Empty : "s")}";
        ReceiverCountText.Text = $"{scene.ReceiverRigs.Count} stereo";
        DurationSummaryText.Text = $"{scene.Timeline.DurationSeconds:0.000} s";
        AtlasStateText.Text = "PRIVATE · LOCAL ONLY";

        var treeItems = new List<TreeViewItem>();
        treeItems.AddRange(scene.Emitters.Select((emitter, index) => new TreeViewItem
        {
            Header = $"◉  {emitter.Name}",
            IsSelected = index == 0
        }));
        treeItems.AddRange(scene.MotionPaths.Select(path => new TreeViewItem { Header = $"⌁  {path.Name}" }));
        treeItems.AddRange(scene.Geometry.Select(geometry => new TreeViewItem { Header = $"▱  {geometry.Name}" }));
        treeItems.AddRange(scene.Atmosphere.WindZones.Select(zone => new TreeViewItem { Header = $"≋  {zone.Name}" }));
        treeItems.AddRange(scene.ReceiverRigs.Select(rig => new TreeViewItem
        {
            Header = $"{(rig.Kind == ReceiverRigKind.BinauralHead ? "◌" : "⌇")}  {rig.Name}"
        }));
        SceneTree.ItemsSource = new[]
        {
            new TreeViewItem { Header = $"▾  {scene.Name}", IsExpanded = true, ItemsSource = treeItems }
        };

        var emitter = scene.Emitters[0];
        var sourceNode = emitter.SignalGraph.Nodes.FirstOrDefault(node => node.Kind == SignalNodeKind.Oscillator);
        var frequency = sourceNode?.NumericParameters.GetValueOrDefault("frequencyHz", 440) ?? 440;
        var waveform = sourceNode?.Waveform ?? WaveformKind.Sine;
        EmitterNameText.Text = emitter.Name;
        WaveformBox.SelectedItem = waveform.ToString();
        WaveformBox.IsEnabled = sourceNode is not null;
        FrequencyBox.IsEnabled = sourceNode is not null;
        FrequencyBox.Text = frequency.ToString("0.###", CultureInfo.InvariantCulture);
        GainBox.Text = emitter.GainLinear.ToString("0.###", CultureInfo.InvariantCulture);
        SignalNodeTitle.Text = waveform.ToString().ToUpperInvariant();
        SignalFrequencyText.Text = $"{frequency:0.00} Hz";
        SignalGainText.Text = $"gain {emitter.GainLinear:0.000}";
        TimelineSourceText.Text = $"{emitter.Name} · source active";

        TemperatureText.Text = $"{scene.Atmosphere.TemperatureCelsius:0.0} °C";
        HumidityText.Text = $"{scene.Atmosphere.RelativeHumidityPercent:0.0} %";
        var wind = scene.Atmosphere.WindZones.FirstOrDefault();
        WindText.Text = wind is null
            ? "Still"
            : $"{wind.VelocityMetresPerSecond.Length:0.00} m/s";
        EffectiveSpeedText.Text = $"{SceneValidator.SpeedOfSound(scene.Atmosphere.TemperatureCelsius):0.00} m/s";

        ReceiverOneText.Text = scene.ReceiverRigs.ElementAtOrDefault(0)?.Name ?? "Receiver take 1";
        ReceiverTwoText.Text = scene.ReceiverRigs.ElementAtOrDefault(1)?.Name ?? "Receiver take 2";
        var tickLabels = new[] { TimelineTick0, TimelineTick1, TimelineTick2, TimelineTick3, TimelineTick4, TimelineTick5, TimelineTick6 };
        for (var index = 0; index < tickLabels.Length; index++)
        {
            var tick = scene.Timeline.DurationSeconds * index / (tickLabels.Length - 1);
            tickLabels[index].Text = index is 0 or 6 ? $"{tick:0.##}s" : $"{tick:0.##}";
        }
        UpdatePlayhead(0);
        SelectKeyframe(0);
    }

    private void OnPlayClicked(object? sender, RoutedEventArgs e)
    {
        StopPreviewTransport();
        previewStarted = DateTimeOffset.UtcNow - TimeSpan.FromSeconds(Viewport.TimeSeconds);
        previewTimer.Start();
        PlayButton.Content = "▶  Playing";
        try
        {
            audioPreview = NativeSceneRenderer.StartPreview(
                scene,
                sampleRate: 48000,
                periodFrames: 512,
                reflectionOrder: 1,
                startTimeSeconds: Viewport.TimeSeconds);
            var status = audioPreview.Status;
            StatusText.Text = $"Audible WASAPI preview · {status.SampleRate:N0} Hz · {status.PeriodFrames} frames · receiver 1";
        }
        catch (Exception exception)
        {
            audioPreview?.Dispose();
            audioPreview = null;
            StatusText.Text = $"Visual-only preview · audio device unavailable: {exception.Message}";
        }
    }

    private void OnStopClicked(object? sender, RoutedEventArgs e)
    {
        StopPreviewTransport();
        StatusText.Text = "Stopped · scene state preserved";
    }

    private void OnGestureClicked(object? sender, RoutedEventArgs e)
    {
        StopPreviewTransport();
        Viewport.IsGestureRecording = !Viewport.IsGestureRecording;
        GestureButton.Content = Viewport.IsGestureRecording ? "●  Draw in world…" : "●  Gesture";
        StatusText.Text = Viewport.IsGestureRecording
            ? "Gesture capture armed · drag across the 3D world; raw samples will be retained"
            : "Gesture capture cancelled";
    }

    private async void OnOpenClicked(object? sender, RoutedEventArgs e)
    {
        if (sceneDirty)
        {
            StatusText.Text = "Save the current scene before opening another project; unsaved work was preserved";
            return;
        }
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Open a PulseGen .pulseproj directory",
            AllowMultiple = false
        });
        if (folders.Count == 0)
        {
            return;
        }

        var projectDirectory = folders[0].Path.LocalPath;
        try
        {
            var loaded = await ProjectStore.LoadAsync(projectDirectory);
            if (loaded.Emitters.Count == 0 || loaded.MotionPaths.Count == 0 || loaded.ReceiverRigs.Count == 0)
            {
                throw new InvalidDataException("The current editor surface requires at least one emitter, motion path, and receiver rig.");
            }
            StopPreviewTransport();
            initializingControls = true;
            ApplySceneToControls(loaded, projectDirectory);
            StatusText.Text = $"Project opened · {projectDirectory}";
        }
        catch (Exception exception)
        {
            StatusText.Text = $"Project open failed without replacing the current scene: {exception.Message}";
        }
        finally
        {
            initializingControls = false;
        }
    }

    private async void OnSaveClicked(object? sender, RoutedEventArgs e)
    {
        var projectDirectory = lastProjectDirectory;
        if (projectDirectory is null)
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = $"Choose a parent folder for {ProjectFolderName(scene.Name)}",
                AllowMultiple = false
            });
            if (folders.Count == 0)
            {
                return;
            }
            projectDirectory = Path.Combine(folders[0].Path.LocalPath, ProjectFolderName(scene.Name));
        }

        try
        {
            scene = scene with { UpdatedAtUtc = DateTimeOffset.UtcNow };
            Viewport.Scene = scene;
            var hash = await ProjectStore.SaveAsync(scene, projectDirectory);
            lastProjectDirectory = projectDirectory;
            sceneDirty = false;
            StatusText.Text = $"Project saved atomically · {hash[..12]}… · {projectDirectory}";
        }
        catch (Exception exception)
        {
            StatusText.Text = $"Project save failed: {exception.Message}";
        }
    }

    private async void OnAtlasClicked(object? sender, RoutedEventArgs e)
    {
        AtlasButton.IsEnabled = false;
        try
        {
            EnsureAtlasClients();
            if (atlasIdentity!.HasMemorySession)
            {
                StatusText.Text = "Revoking the Atlas refresh token…";
                try
                {
                    await atlasIdentity.RevokeAndForgetAsync();
                    StatusText.Text = "Disconnected from Atlas · local credential removed";
                }
                catch (Exception exception)
                {
                    StatusText.Text = $"Disconnected locally · remote revocation could not be confirmed: {exception.Message}";
                }
                AtlasButton.Content = "◇  Atlas sign in";
                AtlasStateText.Text = "PRIVATE · LOCAL ONLY";
                return;
            }

            StatusText.Text = "Restoring the protected Atlas session…";
            if (!await atlasIdentity.TryRestoreAsync())
            {
                StatusText.Text = "Complete sign-in in your browser; PulseGen is listening only on 127.0.0.1…";
                _ = await atlasIdentity.SignInAsync();
            }
            var summary = await atlasLibrary!.GetSummaryAsync();
            AtlasButton.Content = "◇  Disconnect";
            AtlasStateText.Text = $"ATLAS · {summary.Projects} PROJECTS · {summary.Assets} ASSETS";
            StatusText.Text = "Atlas connected · source audio remains local until you explicitly sync it";
        }
        catch (Exception exception)
        {
            StatusText.Text = $"Atlas sign-in unavailable: {exception.Message}";
        }
        finally
        {
            AtlasButton.IsEnabled = true;
        }
    }

    private async void OnSyncClicked(object? sender, RoutedEventArgs e)
    {
        if (syncCancellation is not null)
        {
            syncCancellation.Cancel();
            StatusText.Text = "Atlas sync cancellation requested · the server session remains resumable";
            return;
        }
        if (lastProjectDirectory is null)
        {
            StatusText.Text = "Save this scene as a .pulseproj before explicitly syncing it";
            return;
        }
        if (sceneDirty)
        {
            StatusText.Text = "Save the current scene changes before syncing their immutable manifest";
            return;
        }

        syncCancellation = new CancellationTokenSource();
        var cancellationToken = syncCancellation.Token;
        SyncButton.Content = "■  Cancel sync";
        AtlasButton.IsEnabled = false;
        long? journalId = null;
        try
        {
            EnsureAtlasClients();
            _ = await atlasIdentity!.GetAccessTokenAsync(cancellationToken);
            await workspaceIndex.InitializeAsync(cancellationToken);
            if (atlasBaseRevisionId is null)
            {
                var state = await workspaceIndex.GetProjectSyncStateAsync(scene.ProjectId, cancellationToken);
                atlasBaseRevisionId = state?.HeadRevisionId;
                atlasBaseETag = state?.HeadETag;
            }

            var manifestPath = ProjectStore.ResolveManifestPath(lastProjectDirectory);
            var localHash = await ProjectStore.ComputeSha256Async(manifestPath, cancellationToken);
            var manifest = new FileInfo(manifestPath);
            const string operation = "project.revision.create";
            const string mediaType = "application/vnd.atlas.pulse-project+json";
            AtlasAssetResult? asset = null;
            AtlasUploadLease? lease = null;
            AtlasSyncJournalPayload? payload = null;
            var recoverable = await workspaceIndex.GetLatestRecoverableSyncAsync(scene.ProjectId, operation, cancellationToken);
            if (recoverable is not null && TryReadSyncPayload(recoverable.PayloadJson, out var recovered) &&
                SyncPayloadMatches(recovered, recoverable.BaseRevisionId, atlasBaseRevisionId, manifest, localHash))
            {
                payload = recovered;
                journalId = recoverable.Id;
                await workspaceIndex.MarkSyncStateAsync(journalId.Value, "inflight", cancellationToken);
                if (payload.AssetId is not null)
                {
                    asset = new(payload.AssetId.Value, payload.ManifestSha256, payload.SizeBytes);
                    StatusText.Text = "Resuming Atlas sync from the verified uploaded manifest…";
                }
                else if (payload.SessionId is not null)
                {
                    var resumeSeed = new AtlasUploadLease(
                        payload.SessionId.Value,
                        null,
                        payload.ExpiresAtUtc ?? DateTimeOffset.MinValue,
                        payload.PartSizeBytes,
                        [],
                        payload.ManifestPath,
                        payload.ManifestSha256,
                        payload.SizeBytes,
                        Path.GetFileName(payload.ManifestPath),
                        payload.MediaType,
                        payload.LastWriteTimeUtc);
                    try
                    {
                        lease = await atlasLibrary!.ResumeUploadAsync(resumeSeed, cancellationToken);
                        StatusText.Text = "Resuming the interrupted Atlas multipart upload…";
                    }
                    catch (AtlasProtocolException exception) when (exception.StatusCode is 409 or 410)
                    {
                        await workspaceIndex.MarkSyncStateAsync(journalId.Value, "failed", cancellationToken);
                        journalId = null;
                        payload = null;
                    }
                }
                else
                {
                    await workspaceIndex.MarkSyncStateAsync(journalId.Value, "failed", cancellationToken);
                    journalId = null;
                    payload = null;
                }
            }
            else if (recoverable is not null)
            {
                await workspaceIndex.MarkSyncStateAsync(recoverable.Id, "failed", cancellationToken);
            }

            if (journalId is null)
            {
                payload = new(
                    lastProjectDirectory,
                    manifestPath,
                    localHash,
                    manifest.Length,
                    manifest.LastWriteTimeUtc,
                    mediaType,
                    null,
                    0,
                    null,
                    null);
                journalId = await workspaceIndex.EnqueueSyncAsync(
                    scene.ProjectId,
                    atlasBaseRevisionId?.ToString("D"),
                    operation,
                    payload,
                    cancellationToken);
                await workspaceIndex.MarkSyncStateAsync(journalId.Value, "inflight", cancellationToken);
            }

            var progress = new Progress<(int Completed, int Total)>(value =>
                StatusText.Text = $"Atlas upload · part {value.Completed} of {value.Total}");
            if (asset is null)
            {
                if (lease is null)
                {
                    StatusText.Text = "Uploading the content-addressed project manifest…";
                    lease = await atlasLibrary!.BeginUploadAsync(manifestPath, mediaType, cancellationToken);
                    payload = payload! with
                    {
                        SessionId = lease.SessionId == Guid.Empty ? null : lease.SessionId,
                        PartSizeBytes = lease.PartSizeBytes,
                        ExpiresAtUtc = lease.ExpiresAtUtc
                    };
                    await workspaceIndex.UpdateSyncPayloadAsync(journalId.Value, payload, cancellationToken);
                }
                var completedParts = await atlasLibrary!.UploadPartsAsync(lease, progress, cancellationToken);
                asset = await atlasLibrary.CompleteUploadAsync(lease, completedParts, cancellationToken);
                payload = payload! with { AssetId = asset.AssetId };
                await workspaceIndex.UpdateSyncPayloadAsync(journalId.Value, payload, cancellationToken);
            }
            var revision = await atlasLibrary!.CreateProjectRevisionAsync(
                scene.ProjectId,
                scene.Name,
                asset.AssetId,
                atlasBaseRevisionId,
                atlasBaseETag,
                $"PulseGen desktop sync {DateTimeOffset.UtcNow:O}",
                cancellationToken);
            if (revision.IsConflictBranch)
            {
                await workspaceIndex.MarkSyncStateAsync(journalId.Value, "conflict", cancellationToken);
                StatusText.Text = $"Atlas preserved both versions · created {revision.BranchName}";
            }
            else
            {
                atlasBaseRevisionId = revision.RevisionId;
                atlasBaseETag = revision.ETag;
                await workspaceIndex.UpsertProjectSyncStateAsync(scene.ProjectId, revision.RevisionId, revision.ETag, cancellationToken);
                await workspaceIndex.MarkSyncStateAsync(journalId.Value, "complete", cancellationToken);
                AtlasStateText.Text = $"ATLAS · SYNCED {localHash[..10]}…";
                StatusText.Text = $"Atlas revision committed · {revision.RevisionId:D}";
            }
        }
        catch (OperationCanceledException)
        {
            if (journalId is not null)
            {
                await TryMarkSyncStateAsync(journalId.Value, "pending");
            }
            StatusText.Text = "Atlas sync cancelled · local project and remote immutable parts were preserved";
        }
        catch (AtlasProtocolException exception) when (exception.Code == "not_signed_in")
        {
            if (journalId is not null)
            {
                await TryMarkSyncStateAsync(journalId.Value, "failed");
            }
            StatusText.Text = "Sign in to Atlas before syncing this project";
        }
        catch (Exception exception)
        {
            if (journalId is not null)
            {
                await TryMarkSyncStateAsync(journalId.Value, "failed");
            }
            StatusText.Text = $"Atlas sync failed without overwriting either revision: {exception.Message}";
        }
        finally
        {
            syncCancellation.Dispose();
            syncCancellation = null;
            SyncButton.Content = "⇧  Sync project";
            AtlasButton.IsEnabled = true;
        }
    }

    private static bool TryReadSyncPayload(string json, out AtlasSyncJournalPayload payload)
    {
        try
        {
            payload = JsonSerializer.Deserialize<AtlasSyncJournalPayload>(json)
                ?? throw new JsonException("Empty sync payload.");
            return true;
        }
        catch (JsonException)
        {
            payload = null!;
            return false;
        }
    }

    private bool SyncPayloadMatches(
        AtlasSyncJournalPayload payload,
        string? journalBaseRevisionId,
        Guid? currentBaseRevisionId,
        FileInfo manifest,
        string manifestSha256)
    {
        try
        {
            var expectedBase = currentBaseRevisionId?.ToString("D");
            return string.Equals(journalBaseRevisionId, expectedBase, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(Path.GetFullPath(payload.ProjectPath), Path.GetFullPath(lastProjectDirectory!), StringComparison.OrdinalIgnoreCase) &&
                string.Equals(Path.GetFullPath(payload.ManifestPath), manifest.FullName, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(payload.ManifestSha256, manifestSha256, StringComparison.Ordinal) &&
                payload.SizeBytes == manifest.Length && payload.LastWriteTimeUtc == manifest.LastWriteTimeUtc &&
                payload.MediaType == "application/vnd.atlas.pulse-project+json" &&
                (payload.SessionId is null || payload.SessionId != Guid.Empty) &&
                (payload.AssetId is null || payload.AssetId != Guid.Empty) &&
                payload.PartSizeBytes >= 0;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private void EnsureAtlasClients()
    {
        if (atlasIdentity is not null)
        {
            return;
        }
        var options = new AtlasClientOptions
        {
            IdentityOrigin = ReadOrigin("PULSEGEN_IDENTITY_ORIGIN", "https://identity.atlasuniversalis.com"),
            LibraryOrigin = ReadOrigin("PULSEGEN_LIBRARY_ORIGIN", "https://pulse.atlasuniversalis.com")
        }.EnsureValid();
        var credentials = new WindowsCredentialRefreshTokenStore(options.IdentityOrigin, options.ClientId);
        atlasIdentity = new AtlasOidcClient(options, atlasHttp, credentials);
        atlasLibrary = new AtlasLibraryClient(atlasHttp, atlasIdentity, options);
    }

    private static Uri ReadOrigin(string variableName, string fallback)
    {
        var value = Environment.GetEnvironmentVariable(variableName);
        return new Uri(string.IsNullOrWhiteSpace(value) ? fallback : value, UriKind.Absolute);
    }

    private async Task TryMarkSyncStateAsync(long journalId, string state)
    {
        try
        {
            await workspaceIndex.MarkSyncStateAsync(journalId, state, CancellationToken.None);
        }
        catch
        {
            // The original sync failure remains the actionable error; startup recovery can inspect the inflight row.
        }
    }

    private sealed record AtlasSyncJournalPayload(
        string ProjectPath,
        string ManifestPath,
        string ManifestSha256,
        long SizeBytes,
        DateTime LastWriteTimeUtc,
        string MediaType,
        Guid? SessionId,
        int PartSizeBytes,
        DateTimeOffset? ExpiresAtUtc,
        Guid? AssetId);

    private void OnWaveformChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (initializingControls)
        {
            return;
        }
        if (WaveformBox.SelectedItem is not string selected || !Enum.TryParse<WaveformKind>(selected, out var waveform))
        {
            return;
        }

        StopPreviewTransport();

        var emitter = scene.Emitters[0];
        var nodeIndex = emitter.SignalGraph.Nodes.FindIndex(node => node.Kind == SignalNodeKind.Oscillator);
        if (nodeIndex < 0)
        {
            return;
        }
        emitter.SignalGraph.Nodes[nodeIndex] = emitter.SignalGraph.Nodes[nodeIndex] with { Waveform = waveform };
        sceneDirty = true;
        if (waveform == WaveformKind.Additive && !emitter.SignalGraph.Nodes[nodeIndex].NumericParameters.ContainsKey("partial.0.amplitude"))
        {
            var parameters = emitter.SignalGraph.Nodes[nodeIndex].NumericParameters;
            parameters["partial.0.ratio"] = 1;
            parameters["partial.0.amplitude"] = 1;
            parameters["partial.1.ratio"] = 2;
            parameters["partial.1.amplitude"] = 0.35;
            parameters["partial.2.ratio"] = 3;
            parameters["partial.2.amplitude"] = 0.18;
        }
        SignalNodeTitle.Text = selected.ToUpperInvariant();
        StatusText.Text = $"Primitive changed to {selected} · scene is dirty";
    }

    private void OnSignalValueCommitted(object? sender, RoutedEventArgs e)
    {
        if (!double.TryParse(FrequencyBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var frequency) || !double.IsFinite(frequency) || frequency is < 0.1 or > 96000 ||
            !double.TryParse(GainBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var gain) || !double.IsFinite(gain) || gain is < 0 or > 16)
        {
            StatusText.Text = "Signal values rejected · frequency must be 0.1–96,000 Hz and gain 0–16";
            return;
        }

        StopPreviewTransport();

        var emitter = scene.Emitters[0];
        var node = emitter.SignalGraph.Nodes.First(item => item.Kind == SignalNodeKind.Oscillator);
        node.NumericParameters["frequencyHz"] = frequency;
        scene.Emitters[0] = emitter with { GainLinear = gain };
        sceneDirty = true;
        FrequencyBox.Text = frequency.ToString("0.###", CultureInfo.InvariantCulture);
        GainBox.Text = gain.ToString("0.###", CultureInfo.InvariantCulture);
        SignalFrequencyText.Text = $"{frequency:0.00} Hz";
        StatusText.Text = "Primitive parameters committed · scene is dirty";
    }

    private void OnKeyframeValueCommitted(object? sender, RoutedEventArgs e)
    {
        if (!double.TryParse(KeyframeX.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var x) || !double.IsFinite(x) ||
            !double.TryParse(KeyframeY.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var y) || !double.IsFinite(y) ||
            !double.TryParse(KeyframeZ.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var z) || !double.IsFinite(z))
        {
            StatusText.Text = "Keyframe coordinate rejected · enter finite SI values";
            SelectKeyframe(selectedKeyframeIndex);
            return;
        }
        Viewport.SetPathPoint(selectedKeyframeIndex, new Vector3d(x, y, z));
    }

    private async void OnRenderClicked(object? sender, RoutedEventArgs e)
    {
        StopPreviewTransport();
        var output = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "PulseGen",
            "Renders",
            $"{SafeName(scene.Name)}-{DateTime.UtcNow:yyyyMMdd-HHmmss}");

        StatusText.Text = "Rendering isolated receiver takes…";
        try
        {
            var receipt = await Task.Run(() => NativeSceneRenderer.Render(scene, output));
            lastRenderDirectory = output;
            StatusText.Text = $"Rendered {receipt.FilesWritten} stereo takes + provenance receipt · peak {Math.Max(receipt.PeakLeft, receipt.PeakRight):0.000} · {output}";
        }
        catch (Exception exception)
        {
            StatusText.Text = $"Render unavailable: {exception.Message}";
        }
    }

    private async void OnAnalyzeClicked(object? sender, RoutedEventArgs e)
    {
        if (analysisCancellation is not null)
        {
            analysisCancellation.Cancel();
            StatusText.Text = "Analysis cancellation requested…";
            return;
        }
        StopPreviewTransport();
        if (lastRenderDirectory is null || !Directory.Exists(lastRenderDirectory))
        {
            StatusText.Text = "Render receiver takes first; analysis never substitutes an unrelated source";
            return;
        }
        var input = Directory.EnumerateFiles(lastRenderDirectory, "*.wav").Order(StringComparer.Ordinal).FirstOrDefault();
        if (input is null)
        {
            StatusText.Text = "The latest render directory contains no WAV take";
            return;
        }

        var output = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "PulseGen",
            "Analysis",
            $"{Path.GetFileNameWithoutExtension(input)}-{DateTime.UtcNow:yyyyMMdd-HHmmss}");
        analysisCancellation = new CancellationTokenSource();
        AnalyzeButton.Content = "■  Cancel analysis";
        try
        {
            await using var host = await ModelHostClient.StartAsync(analysisCancellation.Token);
            host.ProgressChanged += (_, progress) => Dispatcher.UIThread.Post(() =>
                StatusText.Text = $"Analysis {progress.Progress:P0} · {progress.Message}");
            var result = await host.AnalyzeAsync(input, output, "linked", analysisCancellation.Token);
            var reconstruction = result.Manifest.GetProperty("reconstruction");
            StatusText.Text = $"Analysis committed · null RMS {reconstruction.GetProperty("rmsNull").GetDouble():0.0e+0} · {output}";
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Analysis cancelled · no partial result was imported";
        }
        catch (Exception exception)
        {
            StatusText.Text = $"Analysis failed: {exception.Message}";
        }
        finally
        {
            analysisCancellation.Dispose();
            analysisCancellation = null;
            AnalyzeButton.Content = "⌁  Analyze";
        }
    }

    private void OnPreviewTick(object? sender, EventArgs e)
    {
        var elapsed = (DateTimeOffset.UtcNow - previewStarted).TotalSeconds;
        if (elapsed >= scene.Timeline.DurationSeconds)
        {
            StopPreviewTransport();
            UpdatePlayhead(scene.Timeline.DurationSeconds);
            StatusText.Text = "Preview complete · scene state preserved";
            return;
        }
        if (audioPreview is not null && !audioPreview.Status.Running)
        {
            audioPreview.Dispose();
            audioPreview = null;
            StatusText.Text = "Visual-only preview · the WASAPI device stopped or was rerouted";
        }

        UpdatePlayhead(elapsed);
    }

    private void StopPreviewTransport()
    {
        previewTimer.Stop();
        audioPreview?.Dispose();
        audioPreview = null;
        PlayButton.Content = "▶  Preview";
    }

    protected override void OnClosed(EventArgs e)
    {
        StopPreviewTransport();
        analysisCancellation?.Cancel();
        syncCancellation?.Cancel();
        atlasIdentity?.Dispose();
        atlasHttp.Dispose();
        workspaceIndex.Dispose();
        base.OnClosed(e);
    }

    private void UpdatePlayhead(double timeSeconds)
    {
        var duration = scene.Timeline.DurationSeconds;
        var time = Math.Clamp(timeSeconds, 0, duration);
        Viewport.TimeSeconds = time;
        var position = SceneKinematics.PositionAt(scene.MotionPaths[0], time);
        var velocity = SceneKinematics.VelocityAt(scene.MotionPaths[0], time);
        PositionText.Text = $"t  {time:0.000} s";
        PositionX.Text = $"{position.X,8:0.000} m";
        PositionY.Text = $"{position.Y,8:0.000} m";
        PositionZ.Text = $"{position.Z,8:0.000} m";
        VelocityText.Text = $"{velocity.Length:0.00} m/s";
        var timelineWidth = (Playhead.Parent as Control)?.Bounds.Width ?? 0;
        Canvas.SetLeft(Playhead, Math.Max(0, timelineWidth * (time / duration)));
    }

    private void SelectKeyframe(int index)
    {
        var path = scene.MotionPaths[0];
        if (path.Keyframes.Count == 0)
        {
            return;
        }
        selectedKeyframeIndex = Math.Clamp(index, 0, path.Keyframes.Count - 1);
        var keyframe = path.Keyframes[selectedKeyframeIndex];
        SelectedPointText.Text = $"KEYFRAME {selectedKeyframeIndex + 1} · t {keyframe.TimeSeconds:0.000} s";
        KeyframeX.Text = keyframe.PositionMetres.X.ToString("0.000", CultureInfo.InvariantCulture);
        KeyframeY.Text = keyframe.PositionMetres.Y.ToString("0.000", CultureInfo.InvariantCulture);
        KeyframeZ.Text = keyframe.PositionMetres.Z.ToString("0.000", CultureInfo.InvariantCulture);
    }

    private static string ProjectFolderName(string sceneName) => $"{SafeName(sceneName)}.pulseproj";

    private static string SafeName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var filtered = new string(value.Where(character => !invalid.Contains(character)).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(filtered) ? "PulseGen" : filtered.Replace(' ', '-');
    }
}
