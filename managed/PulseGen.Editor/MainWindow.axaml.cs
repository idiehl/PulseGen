using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using PulseGen.Atlas.Client;
using PulseGen.Contracts;
using PulseGen.Engine.Client;
using PulseGen.ModelHost.Client;
using PulseGen.Workspace;
using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PulseGen.Editor;

public sealed partial class MainWindow : Window
{
    private static readonly TimeSpan AutosaveDelay = TimeSpan.FromSeconds(8);

    private sealed record MaterialBandEditorRow(
        double FrequencyHz,
        TextBox Absorption,
        TextBox Scattering,
        TextBox Transmission);

    private sealed record AnalysisTakeChoice(string FilePath, string DisplayName)
    {
        public override string ToString() => DisplayName;
    }

    private sealed record AnalysisModeChoice(
        ModelHostChannelMode Mode,
        string DisplayName,
        string Description)
    {
        public override string ToString() => DisplayName;
    }

    private SceneDocument scene = SampleScenes.CreateFlyby();
    private readonly ViewportCameraController viewportCamera = new();
    private readonly DispatcherTimer previewTimer;
    private readonly DispatcherTimer autosaveTimer;
    private readonly DispatcherTimer reflectionDiagnosticTimer;
    private readonly AudioDeviceRecoveryController audioDeviceRecovery = new();
    private DateTimeOffset nextAudioPerformanceRefreshAtUtc;
    private DateTimeOffset previewStarted;
    private double previewDurationSeconds;
    private bool previewAdvancesScene = true;
    private string previewCompletionStatus = "Preview complete · scene state preserved";
    private string previewInterruptedStatus = "Preview stopped · the WASAPI device was removed or rerouted";
    private int selectedKeyframeIndex;
    private bool initializingControls = true;
    private bool sceneDirty;
    private string? lastRenderDirectory;
    private string? lastProjectDirectory;
    private CancellationTokenSource? analysisCancellation;
    private CancellationTokenSource? spectralCancellation;
    private CancellationTokenSource? receiverTakeCancellation;
    private CancellationTokenSource? syncCancellation;
    private NativeSceneRenderer.PreviewSession? audioPreview;
    private SceneSelection currentSelection;
    private Guid activeRenderProfileId;
    private bool refreshingSceneTree;
    private readonly List<MaterialBandEditorRow> materialBandEditors = [];
    private readonly HttpClient atlasHttp = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        AutomaticDecompression = System.Net.DecompressionMethods.All
    })
    { Timeout = Timeout.InfiniteTimeSpan };
    private readonly WorkspaceIndex workspaceIndex = WorkspaceIndex.CreateDefault();
    private AtlasOidcClient? atlasIdentity;
    private AtlasLibraryClient? atlasLibrary;
    private Guid? atlasBaseRevisionId;
    private string? atlasBaseETag;
    private IReadOnlyList<SpectralAnalysisSession> spectralSessions = [];
    private SpectralAnalysisSession? activeSpectralSession;
    private SpectralEditState? activeSpectralEditState;
    private SpectralPartialTrack? selectedSpectralTrack;
    private readonly Dictionary<string, SpectralTrackEdit> pendingSpectralTrackEdits = new(StringComparer.Ordinal);
    private int spectralLoadVersion;
    private int spectralSelectionVersion;
    private bool spectralAuditionPlaying;
    private IReadOnlyList<ReceiverTakeSession> receiverTakeSessions = [];
    private ReceiverTakeSession? activeReceiverTake;
    private int receiverTakeLoadVersion;
    private bool refreshingReceiverTakeControls;
    private bool receiverTakeAuditionPlaying;
    private bool refreshingStudioExportControls;
    private readonly EditorRecoveryService recoveryService;
    private readonly CancellationTokenSource recoveryCancellation = new();
    private DateTimeOffset? sceneEditedAtUtc;
    private bool recoveryReady;
    private bool autosaveInProgress;
    private bool recoveredAutosaveActive;
    private bool discardRecoveryArmed;
    private bool closingInProgress;
    private bool closeFinalized;
    private bool reflectionDiagnosticInFlight;
    private bool reflectionDiagnosticPending;
    private int reflectionDiagnosticVersion;
    private NativeSceneRenderer.ReflectionMonitorSession? reflectionMonitor;
    private int reflectionMonitorConfigurationVersion;
    private int reflectionMonitorSessionVersion = -1;

    public MainWindow()
    {
        InitializeComponent();
        Viewport.Camera = viewportCamera;
        GpuViewport.Camera = viewportCamera;
        Viewport.PlayheadChanged += (_, time) => UpdatePlayhead(time);
        Viewport.PathPointSelected += (_, index) => SelectKeyframe(index);
        Viewport.SceneObjectSelected += selection => SelectSceneObject(selection, refreshTree: true);
        Viewport.GeometryPickRequested += point => GpuViewport.Pick(point)?.GeometryId;
        Viewport.SpatialObjectMoved += selection => PopulateInspector(selection);
        Viewport.SceneEditRejected += message =>
        {
            StatusText.Text = message;
            GestureButton.Content = "●  Gesture";
            PopulateInspector(currentSelection);
        };
        Viewport.SceneEdited += (_, message) => MarkSceneEdited(message);
        GpuViewport.PerformanceUpdated += UpdateViewportPerformance;
        SpectralTrackPlot.TrackSelected += SelectSpectralTrack;
        ReceiverTakeTimeline.TakeSelected += SelectReceiverTake;
        previewTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Render, OnPreviewTick);
        previewTimer.Stop();
        autosaveTimer = new DispatcherTimer(TimeSpan.FromSeconds(2), DispatcherPriority.Background, OnAutosaveTick);
        autosaveTimer.Stop();
        reflectionDiagnosticTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(180), DispatcherPriority.Background, OnReflectionDiagnosticTick);
        reflectionDiagnosticTimer.Stop();
        recoveryService = EditorRecoveryService.CreateForCurrentProcess(workspaceIndex);
        Opened += OnEditorOpened;
        WaveformBox.ItemsSource = new[] { "Sine", "Triangle", "Saw", "Square", "Pulse", "WhiteNoise", "PinkNoise", "BrownNoise", "Impulse", "Additive", "Turbulence" };
        MaterialPresetBox.ItemsSource = AcousticMaterialPresets.All;
        MaterialAuditionBandBox.ItemsSource = MaterialAuditionBands.All;
        MaterialAuditionBandBox.SelectedItem = MaterialAuditionBands.All.First(item => item.FrequencyHz == 1000);
        RenderQualityBox.ItemsSource = RenderProfileChoices.Qualities;
        RenderSampleRateBox.ItemsSource = RenderProfileChoices.SampleRates;
        RenderSampleFormatBox.ItemsSource = RenderProfileChoices.SampleFormats;
        RenderDitherBox.ItemsSource = RenderProfileChoices.Dithers;
        StudioContainerBox.ItemsSource = StudioExportChoices.Containers;
        StudioContainerBox.SelectedItem = StudioExportChoices.Containers.First(choice => choice.Container == StudioExportContainer.BroadcastWave);
        StudioEncodingBox.ItemsSource = StudioExportChoices.EncodingsFor(StudioExportContainer.BroadcastWave);
        StudioEncodingBox.SelectedItem = StudioExportChoices.Encodings.First(choice => choice.Encoding == StudioSampleEncoding.Pcm24);
        StudioDitherBox.ItemsSource = StudioExportChoices.Dithers;
        StudioDitherBox.SelectedItem = StudioExportChoices.Dithers.First(choice => choice.Dither == DitherMode.Tpdf);
        RefreshStudioExportChoices(refreshEncodingSource: true);
        AnalysisChannelModeBox.ItemsSource = new[]
        {
            new AnalysisModeChoice(
                ModelHostChannelMode.LinkedStereo,
                "Linked stereo",
                "Track the same tones across left and right while retaining each channel's level and phase."),
            new AnalysisModeChoice(
                ModelHostChannelMode.IndependentChannels,
                "Independent left / right",
                "Find tones separately in each channel when the two sides contain substantially different material."),
            new AnalysisModeChoice(
                ModelHostChannelMode.MidSide,
                "Center / width (mid-side)",
                "Analyze the shared center and stereo width as separate signals, then reconstruct ordinary left and right audio.")
        };
        AnalysisChannelModeBox.SelectedIndex = 0;
        ApplySceneToControls(scene, projectDirectory: null);
        initializingControls = false;
    }

    private void ApplySceneToControls(SceneDocument loadedScene, string? projectDirectory)
    {
        InvalidateReflectionMonitorConfiguration();
        scene = loadedScene;
        lastProjectDirectory = projectDirectory;
        lastRenderDirectory = null;
        atlasBaseRevisionId = null;
        atlasBaseETag = null;
        sceneDirty = false;
        viewportCamera.SetScene(scene, fit: true);
        Viewport.Scene = scene;
        GpuViewport.SetScene(scene, projectDirectory);
        Viewport.TimeSeconds = 0;
        activeRenderProfileId = scene.RenderProfiles.First().Id;
        currentSelection = SceneSelection.Emitter(scene.Emitters[0].Id);
        Viewport.Selection = currentSelection;
        Viewport.ActiveMotionPathId = SceneSelectionResolver.MotionPath(scene, currentSelection)?.Id;
        Title = $"PulseGen — {scene.Name}";
        SceneNameText.Text = $"{scene.Name} · scene v{scene.SchemaVersion}";
        EmitterCountText.Text = $"{scene.Emitters.Count} source{(scene.Emitters.Count == 1 ? string.Empty : "s")}";
        DurationSummaryText.Text = $"{scene.Timeline.DurationSeconds:0.000} s";
        AtlasStateText.Text = "PRIVATE · LOCAL ONLY";

        RefreshSceneTree();

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

        RefreshAtmosphereSummary();
        RefreshRenderProfileSummary();

        RefreshReceiverSummary();
        RefreshAnalysisTakeChoices();
        RefreshSpectralWorkspace();
        RefreshReceiverTakeWorkspace();
        var tickLabels = new[] { TimelineTick0, TimelineTick1, TimelineTick2, TimelineTick3, TimelineTick4, TimelineTick5, TimelineTick6 };
        for (var index = 0; index < tickLabels.Length; index++)
        {
            var tick = scene.Timeline.DurationSeconds * index / (tickLabels.Length - 1);
            tickLabels[index].Text = index is 0 or 6 ? $"{tick:0.##}s" : $"{tick:0.##}";
        }
        UpdatePlayhead(0);
        SelectKeyframe(0);
        PopulateInspector(currentSelection);
    }

    private void RefreshSceneTree()
    {
        refreshingSceneTree = true;
        try
        {
            TreeViewItem Leaf(string header, SceneSelection selection) => new()
            {
                Header = header,
                Tag = selection,
                IsSelected = selection == currentSelection
            };

            var sourceItems = scene.Emitters
                .Select(emitter => Leaf($"◉  {emitter.Name}", SceneSelection.Emitter(emitter.Id)))
                .Concat(scene.MotionPaths.Select(path => Leaf($"⌁  {path.Name}", SceneSelection.MotionPath(path.Id))))
                .ToList();
            var atmosphereItems = new List<TreeViewItem>
            {
                Leaf("◫  Base air", SceneSelection.BaseAtmosphere())
            };
            atmosphereItems.AddRange(scene.Atmosphere.Layers.Select((layer, index) =>
                Leaf($"≋  Layer {index + 1} · {layer.BaseAltitudeMetres:0.##}–{layer.TopAltitudeMetres:0.##} m", SceneSelection.AtmosphereLayer(index))));
            atmosphereItems.AddRange(scene.Atmosphere.WindZones.Select(zone =>
                Leaf($"➜  {zone.Name}", SceneSelection.WindZone(zone.Id))));

            var worldItems = scene.Geometry
                .Select(geometry => Leaf($"▱  {geometry.Name}", SceneSelection.Geometry(geometry.Id)))
                .ToList();
            worldItems.Add(new TreeViewItem
            {
                Header = "◫  MATERIALS",
                IsExpanded = true,
                ItemsSource = scene.Materials.Select(material =>
                    Leaf($"◇  {material.Name}", SceneSelection.Material(material.Id))).ToList()
            });
            worldItems.Add(new TreeViewItem
            {
                Header = "≋  ATMOSPHERE",
                IsExpanded = true,
                ItemsSource = atmosphereItems
            });

            var receiverItems = scene.ReceiverRigs.Select(rig => Leaf(
                $"{(rig.Kind == ReceiverRigKind.BinauralHead ? "◌" : "⌇")}  {rig.Name}",
                SceneSelection.Receiver(rig.Id))).ToList();
            var renderProfileItems = scene.RenderProfiles.Select(profile => Leaf(
                $"⇥  {profile.Name}",
                SceneSelection.RenderProfile(profile.Id))).ToList();

            SceneTree.ItemsSource = new[]
            {
                new TreeViewItem
                {
                    Header = $"▾  {scene.Name}",
                    IsExpanded = true,
                    ItemsSource = new[]
                    {
                        new TreeViewItem { Header = "◉  SOURCES & PATHS", IsExpanded = true, ItemsSource = sourceItems },
                        new TreeViewItem { Header = "▱  ACOUSTIC WORLD", IsExpanded = true, ItemsSource = worldItems },
                        new TreeViewItem { Header = "⌇  RECEIVERS", IsExpanded = true, ItemsSource = receiverItems },
                        new TreeViewItem { Header = "⇥  RENDER PROFILES", IsExpanded = true, ItemsSource = renderProfileItems }
                    }
                }
            };
        }
        finally
        {
            refreshingSceneTree = false;
        }
    }

    private void OnSceneTreeSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (refreshingSceneTree || initializingControls)
        {
            return;
        }
        var item = e.AddedItems.OfType<TreeViewItem>().FirstOrDefault() ?? SceneTree.SelectedItem as TreeViewItem;
        if (item?.Tag is SceneSelection selection)
        {
            SelectSceneObject(selection, refreshTree: false);
        }
    }

    private void SelectSceneObject(SceneSelection selection, bool refreshTree)
    {
        if (!SelectionExists(selection))
        {
            return;
        }
        currentSelection = selection;
        Viewport.Selection = selection;
        Viewport.ActiveMotionPathId = SceneSelectionResolver.MotionPath(scene, selection)?.Id;
        PopulateInspector(selection);
        ScheduleReflectionDiagnostics();
        if (refreshTree)
        {
            RefreshSceneTree();
        }
    }

    private bool SelectionExists(SceneSelection selection) => SceneSelectionResolver.Exists(scene, selection);

    private void PopulateInspector(SceneSelection selection)
    {
        var wasInitializing = initializingControls;
        initializingControls = true;
        try
        {
            EmitterInspectorPanel.IsVisible = false;
            MaterialInspectorPanel.IsVisible = false;
            GeometryInspectorPanel.IsVisible = false;
            WindInspectorPanel.IsVisible = false;
            LayerInspectorPanel.IsVisible = false;
            BaseAtmosphereInspectorPanel.IsVisible = false;
            ReceiverInspectorPanel.IsVisible = false;
            RenderProfileInspectorPanel.IsVisible = false;
            ReadOnlyInspectorPanel.IsVisible = false;

            switch (selection.Kind)
            {
                case SceneSelectionKind.Emitter:
                    {
                        var emitter = SceneSelectionResolver.Emitter(scene, selection) ?? scene.Emitters[0];
                        var sourceNode = emitter.SignalGraph.Nodes.FirstOrDefault(node => node.Kind == SignalNodeKind.Oscillator);
                        var waveform = sourceNode?.Waveform ?? WaveformKind.Sine;
                        var frequency = sourceNode?.NumericParameters.GetValueOrDefault("frequencyHz", 440) ?? 440;
                        InspectorSelectionText.Text = emitter.Name;
                        InspectorKindBadge.Text = "SOURCE";
                        EmitterNameText.Text = emitter.Name;
                        WaveformBox.SelectedItem = waveform.ToString();
                        WaveformBox.IsEnabled = sourceNode is not null;
                        FrequencyBox.IsEnabled = sourceNode is not null;
                        FrequencyBox.Text = FormatNumber(frequency);
                        GainBox.Text = FormatNumber(emitter.GainLinear);
                        SignalNodeTitle.Text = waveform.ToString().ToUpperInvariant();
                        SignalFrequencyText.Text = $"{frequency:0.00} Hz";
                        SignalGainText.Text = $"gain {emitter.GainLinear:0.000}";
                        TimelineSourceText.Text = $"{emitter.Name} · source active";
                        EmitterInspectorPanel.IsVisible = true;
                        UpdatePlayhead(Viewport.TimeSeconds);
                        SelectKeyframe(selectedKeyframeIndex);
                        break;
                    }
                case SceneSelectionKind.Geometry:
                    {
                        var geometry = scene.Geometry.First(item => item.Id == selection.Id);
                        InspectorSelectionText.Text = geometry.Name;
                        InspectorKindBadge.Text = geometry.Kind == GeometryKind.ParametricRoom ? "ROOM" : "SURFACE";
                        GeometryKindText.Text = geometry.Kind switch
                        {
                            GeometryKind.ParametricRoom => "Parametric reflective room",
                            GeometryKind.ImportedMesh => "Imported acoustic mesh",
                            _ => $"{geometry.Kind} acoustic primitive"
                        };
                        SetVectorFields(geometry.Transform.PositionMetres, GeometryPositionX, GeometryPositionY, GeometryPositionZ);
                        SetVectorFields(geometry.DimensionsMetres, GeometryDimensionX, GeometryDimensionY, GeometryDimensionZ);
                        SetVectorFields(geometry.Transform.RotationDegrees, GeometryRotationX, GeometryRotationY, GeometryRotationZ);
                        SetVectorFields(geometry.Transform.Scale, GeometryScaleX, GeometryScaleY, GeometryScaleZ);
                        var materials = scene.Materials.Select(item => new MaterialChoice(item.Id, item.Name)).ToList();
                        GeometryMaterialBox.ItemsSource = materials;
                        GeometryMaterialBox.SelectedItem = materials.FirstOrDefault(item => item.Id == geometry.MaterialId);
                        GeometryOccludesBox.IsChecked = geometry.Occludes;
                        GeometryReflectsBox.IsChecked = geometry.Reflects;
                        var room = geometry.Kind == GeometryKind.ParametricRoom;
                        var importedMesh = geometry.Kind == GeometryKind.ImportedMesh;
                        GeometryRotationX.IsEnabled = !room;
                        GeometryRotationY.IsEnabled = !room;
                        GeometryRotationZ.IsEnabled = !room;
                        GeometryReflectsBox.IsEnabled = true;
                        RoomDecayPanel.IsVisible = room;
                        MeshMaterialRegionPanel.IsVisible = importedMesh;
                        if (importedMesh)
                        {
                            var regionChoices = geometry.MaterialBindings
                                .OrderBy(item => item.SourceMeshIndex)
                                .Select(item => new ImportedMeshRegionChoice(
                                    item.SourceMeshIndex,
                                    item.SourceMaterialIndex,
                                    item.SourceMeshName,
                                    item.SourceMaterialName,
                                    0))
                                .ToArray();
                            GeometryRegionBox.ItemsSource = regionChoices;
                            GeometryRegionMaterialBox.ItemsSource = materials;
                            GeometryRegionBox.IsEnabled = regionChoices.Length > 0;
                            GeometryRegionMaterialBox.IsEnabled = regionChoices.Length > 0;
                            RefreshMeshRegionsButton.IsEnabled = lastProjectDirectory is not null;
                            MeshMaterialRegionSummaryText.Text = regionChoices.Length == 0
                                ? "This older import uses the fallback material for every triangle. Read the verified mesh to expose its source regions."
                                : $"{regionChoices.Length} source region{(regionChoices.Length == 1 ? string.Empty : "s")} retained · choose one to assign its physical surface.";
                            if (regionChoices.Length > 0)
                            {
                                GeometryRegionBox.SelectedIndex = 0;
                                GeometryRegionMaterialBox.SelectedItem = materials.FirstOrDefault(item =>
                                    item.Id == geometry.MaterialBindings.First(binding => binding.SourceMeshIndex == regionChoices[0].SourceMeshIndex).MaterialId);
                            }
                        }
                        if (room)
                        {
                            var material = scene.Materials.First(item => item.Id == geometry.MaterialId);
                            var dimensions = new Vector3d(
                                geometry.DimensionsMetres.X * geometry.Transform.Scale.X,
                                geometry.DimensionsMetres.Y * geometry.Transform.Scale.Y,
                                geometry.DimensionsMetres.Z * geometry.Transform.Scale.Z);
                            var speed = SceneValidator.EffectiveSpeedOfSound(
                                scene.Atmosphere.TemperatureCelsius,
                                scene.Atmosphere.RelativeHumidityPercent);
                            var estimate = AcousticRoomAnalysis.Estimate(dimensions, material, speed);
                            RoomLowDecayText.Text = $"{estimate.LowFrequencyRt60Seconds:0.00} s";
                            RoomMidDecayText.Text = $"{estimate.MidFrequencyRt60Seconds:0.00} s";
                            RoomHighDecayText.Text = $"{estimate.HighFrequencyRt60Seconds:0.00} s";
                            RoomReflectionIntervalText.Text = $"{estimate.MeanReflectionIntervalMilliseconds:0.0} ms";
                            RoomProfileTailText.Text = $"{GetActiveRenderProfile().ImpulseResponseSeconds:0.00} s";
                        }
                        GeometryConstraintText.Text = room
                            ? "The current modal-room solver keeps rooms axis-aligned. Position and size remain fully editable."
                            : importedMesh
                                ? "Dimensions control the editable selection handle; the shaded surface and echoes use verified imported triangles. Scale and rotation transform both the acoustic and visible mesh."
                                : "Drag the selected centre handle in the world for X/Z movement; use these fields for exact position, size, and rotation.";
                        GeometryInspectorPanel.IsVisible = true;
                        break;
                    }
                case SceneSelectionKind.Material:
                    {
                        var material = scene.Materials.First(item => item.Id == selection.Id);
                        InspectorSelectionText.Text = material.Name;
                        InspectorKindBadge.Text = "MATERIAL";
                        PopulateMaterialEditor(material);
                        MaterialInspectorPanel.IsVisible = true;
                        break;
                    }
                case SceneSelectionKind.WindZone:
                    {
                        var zone = scene.Atmosphere.WindZones.First(item => item.Id == selection.Id);
                        InspectorSelectionText.Text = zone.Name;
                        InspectorKindBadge.Text = "WIND";
                        SetVectorFields(zone.Transform.PositionMetres, WindPositionX, WindPositionY, WindPositionZ);
                        SetVectorFields(zone.DimensionsMetres, WindDimensionX, WindDimensionY, WindDimensionZ);
                        SetVectorFields(zone.Transform.RotationDegrees, WindRotationX, WindRotationY, WindRotationZ);
                        SetVectorFields(zone.Transform.Scale, WindScaleX, WindScaleY, WindScaleZ);
                        SetVectorFields(zone.VelocityMetresPerSecond, WindVelocityX, WindVelocityY, WindVelocityZ);
                        WindTurbulence.Text = FormatNumber(zone.Turbulence);
                        WindNoiseSeed.Text = FormatNumber(zone.NoiseSeed);
                        WindInspectorPanel.IsVisible = true;
                        break;
                    }
                case SceneSelectionKind.AtmosphereLayer:
                    {
                        var layer = scene.Atmosphere.Layers[selection.Index];
                        InspectorSelectionText.Text = $"Atmosphere layer {selection.Index + 1}";
                        InspectorKindBadge.Text = "AIR LAYER";
                        LayerBaseAltitude.Text = FormatNumber(layer.BaseAltitudeMetres);
                        LayerTopAltitude.Text = FormatNumber(layer.TopAltitudeMetres);
                        LayerTemperature.Text = FormatNumber(layer.TemperatureCelsius);
                        LayerHumidity.Text = FormatNumber(layer.RelativeHumidityPercent);
                        LayerPressure.Text = FormatNumber(layer.PressureKilopascals);
                        LayerInspectorPanel.IsVisible = true;
                        break;
                    }
                case SceneSelectionKind.BaseAtmosphere:
                    InspectorSelectionText.Text = "Base atmosphere";
                    InspectorKindBadge.Text = "BASE AIR";
                    BaseTemperature.Text = FormatNumber(scene.Atmosphere.TemperatureCelsius);
                    BaseHumidity.Text = FormatNumber(scene.Atmosphere.RelativeHumidityPercent);
                    BasePressure.Text = FormatNumber(scene.Atmosphere.PressureKilopascals);
                    BaseAtmosphereInspectorPanel.IsVisible = true;
                    break;
                case SceneSelectionKind.MotionPath:
                    {
                        var path = scene.MotionPaths.First(item => item.Id == selection.Id);
                        InspectorSelectionText.Text = path.Name;
                        InspectorKindBadge.Text = "PATH";
                        ReadOnlySelectionTitle.Text = $"{path.Kind} motion path";
                        ReadOnlySelectionMessage.Text = "Select and drag a path point in the world, or select the source to use exact keyframe coordinates.";
                        ReadOnlyInspectorPanel.IsVisible = true;
                        break;
                    }
                case SceneSelectionKind.Receiver:
                    {
                        var receiver = scene.ReceiverRigs.First(item => item.Id == selection.Id);
                        InspectorSelectionText.Text = receiver.Name;
                        InspectorKindBadge.Text = "RECEIVER";
                        ReceiverKindText.Text = receiver.Kind == ReceiverRigKind.BinauralHead
                            ? "Binaural head receiver"
                            : "Independent stereo microphones";
                        ReceiverNameBox.Text = receiver.Name;
                        ReceiverEnabledBox.IsChecked = receiver.Enabled;
                        SetVectorFields(receiver.Transform.PositionMetres, ReceiverPositionX, ReceiverPositionY, ReceiverPositionZ);
                        ReceiverYaw.Text = FormatNumber(receiver.Transform.RotationDegrees.Y);
                        var motionPaths = new List<MotionPathChoice> { new(null, "Static / no path") };
                        motionPaths.AddRange(scene.MotionPaths.Select(path => new MotionPathChoice(path.Id, path.Name)));
                        ReceiverMotionPathBox.ItemsSource = motionPaths;
                        ReceiverMotionPathBox.SelectedItem = motionPaths.First(item => item.Id == receiver.MotionPathId);
                        ReceiverMotionHint.Text = receiver.MotionPathId is null
                            ? "Drag this static rig in the world for X/Z movement, or assign an authored motion path."
                            : "This rig follows its motion path. Edit the highlighted path points to change its travel through the sound world.";

                        BinauralReceiverPanel.IsVisible = receiver.Kind == ReceiverRigKind.BinauralHead;
                        MicrophoneReceiverPanel.IsVisible = receiver.Kind == ReceiverRigKind.StereoMicrophone;
                        if (receiver.Kind == ReceiverRigKind.BinauralHead)
                        {
                            ReceiverEarSpacing.Text = FormatNumber(receiver.SpacingMetres);
                            ReceiverHeadRadius.Text = FormatNumber(receiver.HeadRadiusMetres);
                            SetVectorFields(receiver.HeadDimensionsMetres, ReceiverHeadDimensionX, ReceiverHeadDimensionY, ReceiverHeadDimensionZ);
                            var modes = new List<HrtfModeChoice>
                            {
                                new(HrtfMode.Parametric, "Analytic head"),
                                new(HrtfMode.ExactSofa, "Measured SOFA"),
                                new(HrtfMode.BoundedHybrid, "Bounded hybrid")
                            };
                            ReceiverHrtfModeBox.ItemsSource = modes;
                            ReceiverHrtfModeBox.SelectedItem = modes.First(item => item.Mode == receiver.HrtfMode);
                            var sofaAssets = new List<HrtfAssetChoice> { new(null, "No dataset") };
                            sofaAssets.AddRange(scene.Assets
                                .Where(asset => asset.Kind == AssetKind.Hrtf)
                                .Select(asset => new HrtfAssetChoice(asset.Sha256, asset.FileName)));
                            ReceiverSofaAssetBox.ItemsSource = sofaAssets;
                            ReceiverSofaAssetBox.SelectedItem = sofaAssets.FirstOrDefault(item =>
                                string.Equals(item.Sha256, receiver.SofaAssetHash, StringComparison.OrdinalIgnoreCase)) ?? sofaAssets[0];
                            UpdateReceiverHrtfControls();
                        }
                        else
                        {
                            var (left, right) = ReceiverRigGeometry.EffectiveMicrophoneCapsules(receiver);
                            PopulateCapsuleControls(left, isLeft: true);
                            PopulateCapsuleControls(right, isLeft: false);
                        }
                        ReceiverInspectorPanel.IsVisible = true;
                        break;
                    }
                case SceneSelectionKind.RenderProfile:
                    {
                        var profile = scene.RenderProfiles.First(item => item.Id == selection.Id);
                        activeRenderProfileId = profile.Id;
                        InspectorSelectionText.Text = profile.Name;
                        InspectorKindBadge.Text = "RENDER";
                        RenderProfileNameBox.Text = profile.Name;
                        RenderQualityBox.SelectedItem = RenderProfileChoices.Qualities.First(item => item.Quality == profile.Quality);
                        RenderSampleRateBox.SelectedItem = RenderProfileChoices.SampleRates.First(item => item.SampleRate == profile.SampleRate);
                        RenderSampleFormatBox.SelectedItem = RenderProfileChoices.SampleFormats.First(item => item.Format == profile.SampleFormat);
                        RenderDitherBox.SelectedItem = RenderProfileChoices.Dithers.First(item => item.Mode == profile.Dither);
                        UpdateRenderDitherControls(profile.SampleFormat);
                        RenderBufferFramesBox.Text = profile.BufferFrames.ToString(CultureInfo.InvariantCulture);
                        RenderReflectionOrderBox.Text = profile.ReflectionOrder.ToString(CultureInfo.InvariantCulture);
                        RenderRayCountBox.Text = profile.RayCount.ToString(CultureInfo.InvariantCulture);
                        RenderTailSecondsBox.Text = FormatNumber(profile.ImpulseResponseSeconds);
                        RenderAtmosphereSegmentsBox.Text = profile.AtmosphereIntegrationSegments.ToString(CultureInfo.InvariantCulture);
                        RenderSeedBox.Text = profile.Seed.ToString(CultureInfo.InvariantCulture);
                        RenderProfileExplanationText.Text = profile.Quality == RenderQuality.Final
                            ? "Final mode uses every authored air-path slice. Depth 2 adds bounded imported-surface pairs and opposing-wall room bounces; authored primitive planes and boxes stay first-bounce. Diffuse detail controls tail density."
                            : "Preview uses one air sample per path leg. Depth 2 keeps one selected imported-surface pair plus opposing-wall room bounces; authored primitive planes and boxes stay first-bounce. Diffuse detail controls tail density.";
                        RenderProfileInspectorPanel.IsVisible = true;
                        RefreshRenderProfileSummary();
                        break;
                    }
                default:
                    InspectorSelectionText.Text = "Scene selection";
                    InspectorKindBadge.Text = "WORLD";
                    ReadOnlySelectionTitle.Text = "No editable object selected";
                    ReadOnlySelectionMessage.Text = "Choose a room, surface, atmosphere layer, or wind zone from the scene tree or world view.";
                    ReadOnlyInspectorPanel.IsVisible = true;
                    break;
            }
        }
        finally
        {
            initializingControls = wasInitializing;
        }
    }

    private void RefreshAtmosphereSummary()
    {
        TemperatureText.Text = $"{scene.Atmosphere.TemperatureCelsius:0.0} °C";
        HumidityText.Text = $"{scene.Atmosphere.RelativeHumidityPercent:0.0} %";
        PressureText.Text = $"{scene.Atmosphere.PressureKilopascals:0.###} kPa";
        LayerCountText.Text = scene.Atmosphere.Layers.Count == 0
            ? "0 · base air"
            : $"{scene.Atmosphere.Layers.Count} active";
        var strongestWind = scene.Atmosphere.WindZones.Count == 0
            ? null
            : scene.Atmosphere.WindZones.MaxBy(zone => zone.VelocityMetresPerSecond.Length);
        WindText.Text = strongestWind is null
            ? "Still"
            : $"{scene.Atmosphere.WindZones.Count} bounded · {strongestWind.VelocityMetresPerSecond.Length:0.00} m/s";
        EffectiveSpeedText.Text = $"{SceneValidator.EffectiveSpeedOfSound(scene.Atmosphere.TemperatureCelsius, scene.Atmosphere.RelativeHumidityPercent):0.00} m/s";
        var profile = GetActiveRenderProfile();
        FieldQualityText.Text = $"1 preview / {profile.AtmosphereIntegrationSegments} final";
    }

    private RenderProfile GetActiveRenderProfile() =>
        scene.RenderProfiles.FirstOrDefault(profile => profile.Id == activeRenderProfileId)
        ?? scene.RenderProfiles.First();

    private void RefreshRenderProfileSummary()
    {
        var profile = GetActiveRenderProfile();
        var qualityName = RenderProfileChoices.Qualities.First(item => item.Quality == profile.Quality).Name;
        ActiveSampleRateText.Text = $"{profile.SampleRate / 1000d:0.#} kHz · {qualityName.ToUpperInvariant()}";
        FieldQualityText.Text = $"1 preview / {profile.AtmosphereIntegrationSegments} final";
    }

    private void RefreshReceiverSummary()
    {
        var enabled = scene.ReceiverRigs.Where(receiver => receiver.Enabled).ToList();
        ReceiverCountText.Text = enabled.Count == scene.ReceiverRigs.Count
            ? $"{enabled.Count} stereo"
            : $"{enabled.Count}/{scene.ReceiverRigs.Count} active";
        ReceiverOneText.Text = enabled.ElementAtOrDefault(0)?.Name ?? "Receiver take 1";
        ReceiverTwoText.Text = enabled.ElementAtOrDefault(1)?.Name ?? "Receiver take 2";
    }

    private void PopulateCapsuleControls(MicrophoneCapsule capsule, bool isLeft)
    {
        var positionX = isLeft ? LeftCapsulePositionX : RightCapsulePositionX;
        var positionY = isLeft ? LeftCapsulePositionY : RightCapsulePositionY;
        var positionZ = isLeft ? LeftCapsulePositionZ : RightCapsulePositionZ;
        var rotationX = isLeft ? LeftCapsuleRotationX : RightCapsuleRotationX;
        var rotationY = isLeft ? LeftCapsuleRotationY : RightCapsuleRotationY;
        var rotationZ = isLeft ? LeftCapsuleRotationZ : RightCapsuleRotationZ;
        var patternBox = isLeft ? LeftCapsulePatternBox : RightCapsulePatternBox;
        var sensitivity = isLeft ? LeftCapsuleSensitivity : RightCapsuleSensitivity;
        var selfNoise = isLeft ? LeftCapsuleSelfNoise : RightCapsuleSelfNoise;

        SetVectorFields(capsule.RelativeTransform.PositionMetres, positionX, positionY, positionZ);
        SetVectorFields(capsule.RelativeTransform.RotationDegrees, rotationX, rotationY, rotationZ);
        var patterns = Enum.GetValues<PolarPattern>()
            .Select(pattern => new PolarPatternChoice(pattern, PolarPatternName(pattern)))
            .ToList();
        patternBox.ItemsSource = patterns;
        patternBox.SelectedItem = patterns.First(item => item.Pattern == capsule.PolarPattern);
        sensitivity.Text = FormatNumber(capsule.SensitivityDb);
        selfNoise.Text = FormatNumber(capsule.SelfNoiseDbSpl);
    }

    private static string PolarPatternName(PolarPattern pattern) => pattern switch
    {
        PolarPattern.Omni => "Omnidirectional",
        PolarPattern.FigureEight => "Figure-eight",
        _ => pattern.ToString()
    };

    private void UpdateReceiverHrtfControls()
    {
        var mode = (ReceiverHrtfModeBox.SelectedItem as HrtfModeChoice)?.Mode ?? HrtfMode.Parametric;
        ReceiverSofaAssetBox.IsEnabled = mode != HrtfMode.Parametric;
        ReceiverHrtfStatusText.Text = mode switch
        {
            HrtfMode.Parametric => "Active now: analytic ear timing and head-shadow filtering.",
            HrtfMode.ExactSofa => "The selected measured response is saved as a content-addressed project asset, but preview and final rendering will stop clearly until the Steam Audio SOFA adapter is active.",
            _ => "Bounded hybrid response settings are saved, but preview and final rendering will stop clearly until the Steam Audio HRTF adapter is active."
        };
    }

    private void PopulateMaterialEditor(AcousticMaterial material)
    {
        MaterialNameBox.Text = material.Name;
        var matchingPreset = AcousticMaterialPresets.All.FirstOrDefault(preset => preset.Matches(material));
        MaterialPresetBox.SelectedItem = matchingPreset;
        MaterialPresetDescriptionText.Text = matchingPreset is null
            ? "Custom material. Presets are adjustable starting points, not certified measurements."
            : $"{matchingPreset.Description} Values are simulation starting points, not certified measurements.";
        BuildMaterialBandRows(material);
        MaterialRoughnessBox.Text = FormatNumber(material.Roughness);

        var usedBy = scene.Geometry
            .Where(item => item.MaterialId == material.Id || item.MaterialBindings.Any(binding => binding.MaterialId == material.Id))
            .Select(item => item.Name)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        MaterialUsageText.Text = usedBy.Length switch
        {
            0 => "Not assigned to a surface yet.",
            1 => $"Used by {usedBy[0]}.",
            _ => $"Used by {usedBy.Length} surfaces: {string.Join(", ", usedBy.Take(3))}{(usedBy.Length > 3 ? $" and {usedBy.Length - 3} more" : string.Empty)}."
        };
        UpdateMaterialEnergySummary(material);
    }

    private void BuildMaterialBandRows(AcousticMaterial material)
    {
        materialBandEditors.Clear();
        MaterialBandRows.Children.Clear();
        for (var index = 0; index < material.OctaveBandCentresHz.Length; index++)
        {
            var row = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("54,*,*,*"),
                ColumnSpacing = 5
            };
            var frequency = new TextBlock
            {
                Text = FormatBandFrequency(material.OctaveBandCentresHz[index]),
                VerticalAlignment = VerticalAlignment.Center,
                FontSize = 10
            };
            var absorption = CreateMaterialCoefficientBox(material.Absorption[index]);
            var scattering = CreateMaterialCoefficientBox(material.Scattering[index]);
            var transmission = CreateMaterialCoefficientBox(material.Transmission[index]);
            Grid.SetColumn(absorption, 1);
            Grid.SetColumn(scattering, 2);
            Grid.SetColumn(transmission, 3);
            row.Children.Add(frequency);
            row.Children.Add(absorption);
            row.Children.Add(scattering);
            row.Children.Add(transmission);
            MaterialBandRows.Children.Add(row);
            materialBandEditors.Add(new(
                material.OctaveBandCentresHz[index],
                absorption,
                scattering,
                transmission));
        }
    }

    private TextBox CreateMaterialCoefficientBox(double value)
    {
        var field = new TextBox
        {
            Text = FormatNumber(value),
            HorizontalContentAlignment = HorizontalAlignment.Right
        };
        field.LostFocus += OnMaterialDraftChanged;
        return field;
    }

    private static string FormatBandFrequency(double frequencyHz) => frequencyHz >= 1000
        ? $"{frequencyHz / 1000:0.#}k"
        : $"{frequencyHz:0}";

    private void OnMaterialPresetChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (initializingControls)
        {
            return;
        }
        MaterialPresetDescriptionText.Text = MaterialPresetBox.SelectedItem is AcousticMaterialPreset preset
            ? $"{preset.Description} Load changes only the draft until Apply material is pressed."
            : "Custom material. Presets are adjustable starting points, not certified measurements.";
    }

    private void OnLoadMaterialPresetClicked(object? sender, RoutedEventArgs e)
    {
        if (currentSelection.Kind != SceneSelectionKind.Material ||
            MaterialPresetBox.SelectedItem is not AcousticMaterialPreset preset)
        {
            StatusText.Text = "Choose a material starting point to load";
            return;
        }
        var material = scene.Materials.FirstOrDefault(item => item.Id == currentSelection.Id);
        if (material is null)
        {
            return;
        }

        var draft = preset.ApplyTo(material);
        BuildMaterialBandRows(draft);
        MaterialRoughnessBox.Text = FormatNumber(draft.Roughness);
        UpdateMaterialEnergySummary(draft);
        StatusText.Text = $"{preset.Name} values loaded into the editor · Apply material to commit them";
    }

    private void OnMaterialDraftChanged(object? sender, RoutedEventArgs e)
    {
        if (initializingControls || currentSelection.Kind != SceneSelectionKind.Material)
        {
            return;
        }
        var original = scene.Materials.FirstOrDefault(item => item.Id == currentSelection.Id);
        if (original is null)
        {
            return;
        }
        if (TryReadMaterialDraft(original, out var draft, out var error))
        {
            UpdateMaterialEnergySummary(draft);
        }
        else
        {
            MaterialEnergyHintText.Text = error;
        }
    }

    private bool TryReadMaterialDraft(
        AcousticMaterial original,
        out AcousticMaterial replacement,
        out string error)
    {
        var name = MaterialNameBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            replacement = original;
            error = "Give the material a name before applying it.";
            return false;
        }
        if (materialBandEditors.Count == 0 || !TryReadNumber(MaterialRoughnessBox, out var roughness) || roughness is < 0 or > 1)
        {
            replacement = original;
            error = "Roughness must be a number from 0 to 1.";
            return false;
        }

        var centres = new double[materialBandEditors.Count];
        var absorption = new double[materialBandEditors.Count];
        var scattering = new double[materialBandEditors.Count];
        var transmission = new double[materialBandEditors.Count];
        for (var index = 0; index < materialBandEditors.Count; index++)
        {
            var row = materialBandEditors[index];
            if (!TryReadNumber(row.Absorption, out absorption[index]) ||
                !TryReadNumber(row.Scattering, out scattering[index]) ||
                !TryReadNumber(row.Transmission, out transmission[index]) ||
                absorption[index] is < 0 or > 1 ||
                scattering[index] is < 0 or > 1 ||
                transmission[index] is < 0 or > 1)
            {
                replacement = original;
                error = $"The {FormatBandFrequency(row.FrequencyHz)} band needs values from 0 to 1.";
                return false;
            }
            if (absorption[index] + transmission[index] > 1 + 1e-12)
            {
                replacement = original;
                error = $"At {FormatBandFrequency(row.FrequencyHz)}, soak plus pass cannot exceed all incoming sound.";
                return false;
            }
            centres[index] = row.FrequencyHz;
        }

        replacement = original with
        {
            Name = name,
            OctaveBandCentresHz = centres,
            Absorption = absorption,
            Scattering = scattering,
            Transmission = transmission,
            Roughness = roughness
        };
        error = string.Empty;
        return true;
    }

    private void UpdateMaterialEnergySummary(AcousticMaterial material)
    {
        var summary = AcousticMaterialPhysics.Average(material);
        SetMaterialEnergy(MaterialAbsorbedBar, MaterialAbsorbedText, summary.Absorbed);
        SetMaterialEnergy(MaterialClearEchoBar, MaterialClearEchoText, summary.ClearReflection);
        SetMaterialEnergy(MaterialScatteredBar, MaterialScatteredText, summary.ScatteredAway);
        SetMaterialEnergy(MaterialTransmittedBar, MaterialTransmittedText, summary.Transmitted);
        MaterialEnergyHintText.Text =
            "These averages cover every shown frequency band. Clear echo stays directional; scattered sound feeds the deterministic diffuse tail when the active profile has diffuse detail and tail time. Roughness softens brighter reflections.";
    }

    private void OnAuditionMaterialClicked(object? sender, RoutedEventArgs e)
    {
        if (currentSelection.Kind != SceneSelectionKind.Material ||
            MaterialAuditionBandBox.SelectedItem is not MaterialAuditionBandChoice band)
        {
            StatusText.Text = "Select a material and listening band first";
            return;
        }
        var original = scene.Materials.FirstOrDefault(item => item.Id == currentSelection.Id);
        if (original is null)
        {
            StatusText.Text = "Material audition rejected · the selected material no longer exists";
            return;
        }
        if (!TryReadMaterialDraft(original, out var draft, out var draftError))
        {
            StatusText.Text = $"Material audition rejected · {draftError}";
            return;
        }

        var auditionScene = MaterialAuditionScene.Create(draft, band.FrequencyHz);
        var profile = auditionScene.RenderProfiles[0];
        StopPreviewTransport();
        previewStarted = DateTimeOffset.UtcNow;
        previewDurationSeconds = auditionScene.Timeline.DurationSeconds;
        previewAdvancesScene = false;
        previewCompletionStatus = $"{draft.Name} audition complete · project was not changed";
        previewInterruptedStatus = "Material audition stopped · the WASAPI device was removed or rerouted";
        PlayButton.Content = "▶  Auditioning";
        try
        {
            audioPreview = NativeSceneRenderer.StartPreview(
                auditionScene,
                sampleRate: (uint)profile.SampleRate,
                periodFrames: (uint)profile.BufferFrames,
                reflectionOrder: (uint)profile.ReflectionOrder,
                rayCount: (uint)profile.RayCount,
                impulseResponseSeconds: profile.ImpulseResponseSeconds,
                seed: (uint)profile.Seed);
            previewTimer.Start();
            StatusText.Text = $"Auditioning {draft.Name} at {band.Name} · direct tone, clear echo, and diffuse tail";
        }
        catch (Exception exception)
        {
            StopPreviewTransport();
            StatusText.Text = $"Material audition unavailable: {exception.Message}";
        }
    }

    private static void SetMaterialEnergy(ProgressBar bar, TextBlock label, double amount)
    {
        var percent = Math.Clamp(amount * 100, 0, 100);
        bar.Value = percent;
        label.Text = $"{percent:0}%";
    }

    private void MarkSceneEdited(string message)
    {
        StopPreviewTransport();
        InvalidateReflectionMonitorConfiguration();
        sceneDirty = true;
        sceneEditedAtUtc = DateTimeOffset.UtcNow;
        AutosaveStateText.Text = "UNSAVED · AUTOSAVE PENDING";
        AutosaveStateText.Foreground = Avalonia.Media.Brushes.Goldenrod;
        lastRenderDirectory = null;
        RefreshAnalysisTakeChoices();
        if (currentSelection.Kind == SceneSelectionKind.Emitter)
        {
            SelectKeyframe(Viewport.SelectedKeyframeIndex);
        }
        RefreshAtmosphereSummary();
        var sceneBounds = ViewportSceneBounds.Calculate(scene);
        viewportCamera.ExtendContentBounds(sceneBounds.Minimum, sceneBounds.Maximum, fit: false);
        Viewport.Scene = scene;
        GpuViewport.Scene = scene;
        Viewport.Selection = currentSelection;
        Viewport.ActiveMotionPathId = SceneSelectionResolver.MotionPath(scene, currentSelection)?.Id;
        StatusText.Text = message;
        GestureButton.Content = "●  Gesture";
        ScheduleReflectionDiagnostics();
    }

    private static string FormatNumber(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    private static void SetVectorFields(Vector3d value, TextBox x, TextBox y, TextBox z)
    {
        x.Text = FormatNumber(value.X);
        y.Text = FormatNumber(value.Y);
        z.Text = FormatNumber(value.Z);
    }

    private static bool TryReadNumber(TextBox field, out double value) =>
        double.TryParse(field.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && double.IsFinite(value);

    private static bool TryReadInteger(TextBox field, out int value) =>
        int.TryParse(field.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);

    private static bool TryReadVector(TextBox x, TextBox y, TextBox z, out Vector3d value)
    {
        if (TryReadNumber(x, out var xValue) && TryReadNumber(y, out var yValue) && TryReadNumber(z, out var zValue))
        {
            value = new Vector3d(xValue, yValue, zValue);
            return true;
        }
        value = default;
        return false;
    }

    private void CommitSceneEdit(string description) =>
        MarkSceneEdited($"{description} committed · scene is dirty");

    private void RejectSceneEdit(string description, ValidationIssue? error)
    {
        Viewport.Scene = scene;
        GpuViewport.Scene = scene;
        Viewport.Selection = currentSelection;
        StatusText.Text = $"{description} rejected · {error?.Message ?? "the scene object is no longer available"}";
    }

    private void OnAddPlaneClicked(object? sender, RoutedEventArgs e) => AddGeometry(GeometryKind.Plane);

    private void OnAddBoxClicked(object? sender, RoutedEventArgs e) => AddGeometry(GeometryKind.Box);

    private void OnAddRoomClicked(object? sender, RoutedEventArgs e) => AddGeometry(GeometryKind.ParametricRoom);

    private void OnFitViewportClicked(object? sender, RoutedEventArgs e)
    {
        GpuViewport.FitCameraToScene();
        Viewport.Focus();
        StatusText.Text = "Viewport fitted to the complete authored sound space";
    }

    private void OnAddMaterialClicked(object? sender, RoutedEventArgs e)
    {
        var material = AcousticMaterialPresets.Default.Create(
            Guid.NewGuid(),
            SceneSelectionResolver.UniqueName("Acoustic material", scene.Materials.Select(item => item.Name)));
        if (!SceneAuthoringOperations.TryAddMaterial(scene, material, out var error))
        {
            RejectSceneEdit($"Add {material.Name}", error);
            return;
        }
        currentSelection = SceneSelection.Material(material.Id);
        Viewport.ActiveMotionPathId = null;
        CommitSceneEdit($"{material.Name} added from the balanced-wall starting point");
        RefreshSceneTree();
        PopulateInspector(currentSelection);
    }

    private void OnDuplicateMaterialClicked(object? sender, RoutedEventArgs e)
    {
        if (currentSelection.Kind != SceneSelectionKind.Material)
        {
            return;
        }
        var original = scene.Materials.FirstOrDefault(item => item.Id == currentSelection.Id);
        if (original is null)
        {
            return;
        }
        var duplicate = original with
        {
            Id = Guid.NewGuid(),
            Name = SceneSelectionResolver.UniqueName($"{original.Name} copy", scene.Materials.Select(item => item.Name)),
            OctaveBandCentresHz = original.OctaveBandCentresHz.ToArray(),
            Absorption = original.Absorption.ToArray(),
            Scattering = original.Scattering.ToArray(),
            Transmission = original.Transmission.ToArray()
        };
        if (!SceneAuthoringOperations.TryAddMaterial(scene, duplicate, out var error))
        {
            RejectSceneEdit($"Duplicate {original.Name}", error);
            return;
        }
        currentSelection = SceneSelection.Material(duplicate.Id);
        Viewport.ActiveMotionPathId = null;
        CommitSceneEdit($"{duplicate.Name} created as an independent comparison material");
        RefreshSceneTree();
        PopulateInspector(currentSelection);
    }

    private void AddGeometry(GeometryKind kind)
    {
        if (kind == GeometryKind.ParametricRoom && scene.Geometry.Any(item => item.Kind == GeometryKind.ParametricRoom))
        {
            StatusText.Text = "Only one parametric room can feed the current modal-room solver; select the existing room to edit it";
            return;
        }
        var material = scene.Materials.FirstOrDefault();
        if (material is null)
        {
            StatusText.Text = "A verified acoustic material is required before adding geometry";
            return;
        }
        var stem = kind switch
        {
            GeometryKind.Plane => "Reflective plane",
            GeometryKind.Box => "Acoustic box",
            GeometryKind.ParametricRoom => "Simulation room",
            _ => kind.ToString()
        };
        var geometry = new AcousticGeometry
        {
            Id = Guid.NewGuid(),
            Name = SceneSelectionResolver.UniqueName(stem, scene.Geometry.Select(item => item.Name)),
            Kind = kind,
            Transform = new Transform
            {
                PositionMetres = kind == GeometryKind.ParametricRoom ? new Vector3d(0, 2.5, 0) : new Vector3d(0, 2, 0)
            },
            MaterialId = material.Id,
            DimensionsMetres = kind switch
            {
                GeometryKind.Plane => new Vector3d(6, 4, 0),
                GeometryKind.Box => new Vector3d(2, 2, 2),
                _ => new Vector3d(20, 5, 14)
            },
            Occludes = true,
            Reflects = true
        };
        if (!SceneAuthoringOperations.TryAddGeometry(scene, geometry, out var error))
        {
            RejectSceneEdit($"Add {geometry.Name}", error);
            return;
        }
        currentSelection = SceneSelection.Geometry(geometry.Id);
        Viewport.ActiveMotionPathId = null;
        CommitSceneEdit($"{geometry.Name} added");
        RefreshSceneTree();
        PopulateInspector(currentSelection);
    }

    private void OnAddWindZoneClicked(object? sender, RoutedEventArgs e)
    {
        var zone = new WindZone
        {
            Id = Guid.NewGuid(),
            Name = SceneSelectionResolver.UniqueName("Wind zone", scene.Atmosphere.WindZones.Select(item => item.Name)),
            Transform = new Transform { PositionMetres = new Vector3d(0, 3, 0) },
            DimensionsMetres = new Vector3d(12, 6, 8),
            VelocityMetresPerSecond = new Vector3d(5, 0, 0),
            Turbulence = 0.1,
            NoiseSeed = scene.Atmosphere.WindZones.Count + 1
        };
        if (!SceneAuthoringOperations.TryAddWindZone(scene, zone, out var error))
        {
            RejectSceneEdit($"Add {zone.Name}", error);
            return;
        }
        currentSelection = SceneSelection.WindZone(zone.Id);
        Viewport.ActiveMotionPathId = null;
        CommitSceneEdit($"{zone.Name} added");
        RefreshSceneTree();
        PopulateInspector(currentSelection);
    }

    private void OnAddAtmosphereLayerClicked(object? sender, RoutedEventArgs e)
    {
        var baseAltitude = scene.Atmosphere.Layers.Count == 0
            ? 0
            : scene.Atmosphere.Layers.Max(item => item.TopAltitudeMetres) + 0.5;
        var layer = new AtmosphereLayer
        {
            BaseAltitudeMetres = baseAltitude,
            TopAltitudeMetres = baseAltitude + 5,
            TemperatureCelsius = scene.Atmosphere.TemperatureCelsius,
            RelativeHumidityPercent = scene.Atmosphere.RelativeHumidityPercent,
            PressureKilopascals = scene.Atmosphere.PressureKilopascals
        };
        if (!SceneAuthoringOperations.TryAddAtmosphereLayer(scene, layer, out var error))
        {
            RejectSceneEdit("Add atmosphere layer", error);
            return;
        }
        currentSelection = SceneSelection.AtmosphereLayer(scene.Atmosphere.Layers.Count - 1);
        Viewport.ActiveMotionPathId = null;
        CommitSceneEdit("Atmosphere layer added");
        RefreshSceneTree();
        PopulateInspector(currentSelection);
    }

    private void OnAddBinauralReceiverClicked(object? sender, RoutedEventArgs e) =>
        AddReceiver(ReceiverRigKind.BinauralHead);

    private void OnAddMicrophoneReceiverClicked(object? sender, RoutedEventArgs e) =>
        AddReceiver(ReceiverRigKind.StereoMicrophone);

    private void AddReceiver(ReceiverRigKind kind)
    {
        if (scene.ReceiverRigs.Count >= 16)
        {
            StatusText.Text = "Receiver add rejected · the current real-time engine supports at most 16 simultaneous stereo rigs";
            return;
        }

        var microphone = kind == ReceiverRigKind.StereoMicrophone;
        var spacing = microphone ? 0.17 : 0.18;
        var angle = microphone ? 110 : 0;
        var sensitivity = microphone ? -6 : 0;
        var receiver = new ReceiverRig
        {
            Id = Guid.NewGuid(),
            Name = SceneSelectionResolver.UniqueName(
                microphone ? "Stereo microphones" : "Binaural listener",
                scene.ReceiverRigs.Select(item => item.Name)),
            Kind = kind,
            Transform = new Transform
            {
                PositionMetres = new Vector3d((scene.ReceiverRigs.Count - 1) * 0.75, 1.7, 0)
            },
            SpacingMetres = spacing,
            AngleDegrees = angle,
            HeadRadiusMetres = 0.09,
            HeadDimensionsMetres = new Vector3d(0.18, 0.22, 0.24),
            HrtfMode = HrtfMode.Parametric,
            PolarPattern = microphone ? PolarPattern.Cardioid : PolarPattern.Omni,
            SensitivityDb = sensitivity,
            SelfNoiseDbSpl = microphone ? 18 : 0,
            LeftCapsule = microphone
                ? CreateMicrophoneCapsule(-spacing * 0.5, -angle * 0.5, sensitivity)
                : null,
            RightCapsule = microphone
                ? CreateMicrophoneCapsule(spacing * 0.5, angle * 0.5, sensitivity)
                : null
        };
        if (!SceneAuthoringOperations.TryAddReceiverRig(scene, receiver, out var error))
        {
            RejectSceneEdit($"Add {receiver.Name}", error);
            return;
        }
        currentSelection = SceneSelection.Receiver(receiver.Id);
        Viewport.ActiveMotionPathId = null;
        CommitSceneEdit($"{receiver.Name} added as an isolated stereo take");
        RefreshReceiverSummary();
        RefreshSceneTree();
        PopulateInspector(currentSelection);
    }

    private static MicrophoneCapsule CreateMicrophoneCapsule(double xMetres, double yawDegrees, double sensitivityDb) => new()
    {
        RelativeTransform = new Transform
        {
            PositionMetres = new Vector3d(xMetres, 0, 0),
            RotationDegrees = new Vector3d(0, yawDegrees, 0)
        },
        PolarPattern = PolarPattern.Cardioid,
        SensitivityDb = sensitivityDb,
        SelfNoiseDbSpl = 18
    };

    private void OnReceiverHrtfModeChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!initializingControls)
        {
            UpdateReceiverHrtfControls();
        }
    }

    private void OnApplyMaterialClicked(object? sender, RoutedEventArgs e)
    {
        var index = scene.Materials.FindIndex(item => item.Id == currentSelection.Id);
        if (currentSelection.Kind != SceneSelectionKind.Material || index < 0)
        {
            return;
        }
        var original = scene.Materials[index];
        if (!TryReadMaterialDraft(original, out var replacement, out var draftError))
        {
            StatusText.Text = $"Material edit rejected · {draftError}";
            return;
        }
        if (!SceneAuthoringOperations.TryReplaceMaterial(scene, original.Id, replacement, out var error))
        {
            RejectSceneEdit($"{original.Name} material", error);
            PopulateInspector(currentSelection);
            return;
        }

        CommitSceneEdit($"{replacement.Name} material physics");
        RefreshSceneTree();
        PopulateInspector(currentSelection);
    }

    private void OnRenderSampleFormatChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (initializingControls || RenderSampleFormatBox.SelectedItem is not RenderSampleFormatChoice choice)
        {
            return;
        }
        UpdateRenderDitherControls(choice.Format);
    }

    private void UpdateRenderDitherControls(RenderSampleFormat format)
    {
        if (format == RenderSampleFormat.Float32)
        {
            RenderDitherBox.SelectedItem = RenderProfileChoices.Dithers.First(item => item.Mode == DitherMode.None);
            RenderDitherBox.IsEnabled = false;
            return;
        }

        RenderDitherBox.IsEnabled = true;
        if ((RenderDitherBox.SelectedItem as DitherChoice)?.Mode != DitherMode.Tpdf)
        {
            RenderDitherBox.SelectedItem = RenderProfileChoices.Dithers.First(item => item.Mode == DitherMode.Tpdf);
        }
    }

    private void OnApplyRenderProfileClicked(object? sender, RoutedEventArgs e)
    {
        if (currentSelection.Kind != SceneSelectionKind.RenderProfile)
        {
            return;
        }
        var original = scene.RenderProfiles.FirstOrDefault(item => item.Id == currentSelection.Id);
        var name = RenderProfileNameBox.Text?.Trim();
        if (original is null || string.IsNullOrWhiteSpace(name) ||
            RenderQualityBox.SelectedItem is not RenderQualityChoice qualityChoice ||
            RenderSampleRateBox.SelectedItem is not RenderSampleRateChoice sampleRateChoice ||
            RenderSampleFormatBox.SelectedItem is not RenderSampleFormatChoice sampleFormatChoice ||
            RenderDitherBox.SelectedItem is not DitherChoice ditherChoice ||
            !TryReadInteger(RenderBufferFramesBox, out var bufferFrames) ||
            !TryReadInteger(RenderReflectionOrderBox, out var reflectionOrder) ||
            !TryReadInteger(RenderRayCountBox, out var rayCount) ||
            !TryReadNumber(RenderTailSecondsBox, out var tailSeconds) ||
            !TryReadInteger(RenderAtmosphereSegmentsBox, out var atmosphereSegments) ||
            !TryReadInteger(RenderSeedBox, out var seed))
        {
            StatusText.Text = "Render profile rejected · complete every field with a valid number and name";
            return;
        }

        StopPreviewTransport();
        var replacement = original with
        {
            Name = name,
            Quality = qualityChoice.Quality,
            SampleRate = sampleRateChoice.SampleRate,
            SampleFormat = sampleFormatChoice.Format,
            Dither = ditherChoice.Mode,
            BufferFrames = bufferFrames,
            ReflectionOrder = reflectionOrder,
            RayCount = rayCount,
            ImpulseResponseSeconds = tailSeconds,
            AtmosphereIntegrationSegments = atmosphereSegments,
            Seed = seed
        };
        if (!SceneAuthoringOperations.TryReplaceRenderProfile(scene, original.Id, replacement, out var error))
        {
            RejectSceneEdit($"{original.Name} render profile", error);
            PopulateInspector(currentSelection);
            return;
        }

        activeRenderProfileId = replacement.Id;
        CommitSceneEdit($"{replacement.Name} render profile");
        RefreshRenderProfileSummary();
        RefreshSceneTree();
        PopulateInspector(currentSelection);
    }

    private void OnApplyGeometryClicked(object? sender, RoutedEventArgs e)
    {
        var index = scene.Geometry.FindIndex(item => item.Id == currentSelection.Id);
        if (currentSelection.Kind != SceneSelectionKind.Geometry || index < 0)
        {
            return;
        }
        if (!TryReadVector(GeometryPositionX, GeometryPositionY, GeometryPositionZ, out var position) ||
            !TryReadVector(GeometryDimensionX, GeometryDimensionY, GeometryDimensionZ, out var dimensions) ||
            !TryReadVector(GeometryRotationX, GeometryRotationY, GeometryRotationZ, out var rotation) ||
            !TryReadVector(GeometryScaleX, GeometryScaleY, GeometryScaleZ, out var scale))
        {
            StatusText.Text = "Geometry edit rejected · every transform and dimension must be a finite SI value";
            return;
        }
        var original = scene.Geometry[index];
        var isRoom = original.Kind == GeometryKind.ParametricRoom;
        var materialId = (GeometryMaterialBox.SelectedItem as MaterialChoice)?.Id;
        var materialBindings = original.MaterialBindings;
        if (original.Kind == GeometryKind.ImportedMesh &&
            GeometryRegionBox.SelectedItem is ImportedMeshRegionChoice region &&
            GeometryRegionMaterialBox.SelectedItem is MaterialChoice regionMaterial)
        {
            materialBindings = original.MaterialBindings
                .Select(binding => binding.SourceMeshIndex == region.SourceMeshIndex
                    ? binding with { MaterialId = regionMaterial.Id }
                    : binding)
                .ToList();
        }
        var replacement = original with
        {
            Transform = original.Transform with
            {
                PositionMetres = position,
                RotationDegrees = isRoom ? Vector3d.Zero : rotation,
                Scale = scale
            },
            DimensionsMetres = dimensions,
            MaterialId = materialId,
            MaterialBindings = materialBindings,
            Occludes = GeometryOccludesBox.IsChecked == true,
            Reflects = GeometryReflectsBox.IsChecked == true
        };
        if (!SceneAuthoringOperations.TryReplaceGeometry(scene, original.Id, replacement, out var error))
        {
            RejectSceneEdit($"{original.Name} geometry", error);
            PopulateInspector(currentSelection);
            return;
        }
        CommitSceneEdit($"{original.Name} geometry");
        PopulateInspector(currentSelection);
    }

    private void OnGeometryRegionSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (currentSelection.Kind != SceneSelectionKind.Geometry ||
            GeometryRegionBox.SelectedItem is not ImportedMeshRegionChoice region)
        {
            return;
        }
        var geometry = scene.Geometry.FirstOrDefault(item => item.Id == currentSelection.Id);
        var binding = geometry?.MaterialBindings.FirstOrDefault(item => item.SourceMeshIndex == region.SourceMeshIndex);
        if (binding is null)
        {
            return;
        }
        GeometryRegionMaterialBox.SelectedItem = (GeometryRegionMaterialBox.ItemsSource as IEnumerable<MaterialChoice>)?
            .FirstOrDefault(item => item.Id == binding.MaterialId);
    }

    private async void OnRefreshMeshRegionsClicked(object? sender, RoutedEventArgs e)
    {
        if (currentSelection.Kind != SceneSelectionKind.Geometry || lastProjectDirectory is null)
        {
            return;
        }
        var index = scene.Geometry.FindIndex(item => item.Id == currentSelection.Id);
        if (index < 0 || scene.Geometry[index].Kind != GeometryKind.ImportedMesh)
        {
            return;
        }
        var original = scene.Geometry[index];
        RefreshMeshRegionsButton.IsEnabled = false;
        try
        {
            StopPreviewTransport();
            StatusText.Text = $"Reading verified source-material regions from {original.Name}…";
            var snapshot = ProjectStore.DeserializeManifest(ProjectStore.SerializeManifest(scene));
            var projectDirectory = lastProjectDirectory;
            var decoded = await Task.Run(() =>
                NativeSceneRenderer.DecodeImportedMeshes(snapshot, [original.Id], projectDirectory).Single());
            var fallbackMaterialId = original.MaterialId
                ?? throw new InvalidOperationException("Imported mesh fallback material is missing.");
            var existingBySourceMesh = original.MaterialBindings.ToDictionary(item => item.SourceMeshIndex);
            var materialBindings = decoded.MaterialRegions.Select(region =>
            {
                var materialId = existingBySourceMesh.TryGetValue(region.SourceMeshIndex, out var existing) &&
                    existing.SourceMaterialIndex == region.SourceMaterialIndex
                        ? existing.MaterialId
                        : fallbackMaterialId;
                return new ImportedMeshMaterialBinding
                {
                    SourceMeshIndex = region.SourceMeshIndex,
                    SourceMaterialIndex = region.SourceMaterialIndex,
                    SourceMeshName = region.SourceMeshName,
                    SourceMaterialName = region.SourceMaterialName,
                    MaterialId = materialId
                };
            }).ToList();
            var replacement = original with { MaterialBindings = materialBindings };
            if (!SceneAuthoringOperations.TryReplaceGeometry(scene, original.Id, replacement, out var error))
            {
                RejectSceneEdit($"{original.Name} source material regions", error);
                return;
            }
            CommitSceneEdit($"{original.Name} source material regions");
            PopulateInspector(currentSelection);
            StatusText.Text = $"{original.Name} · {materialBindings.Count} verified source material region{(materialBindings.Count == 1 ? string.Empty : "s")} retained";
        }
        catch (Exception exception)
        {
            StatusText.Text = $"Mesh region read failed without changing the scene: {exception.Message}";
        }
        finally
        {
            RefreshMeshRegionsButton.IsEnabled = lastProjectDirectory is not null;
        }
    }

    private void OnApplyWindClicked(object? sender, RoutedEventArgs e)
    {
        var index = scene.Atmosphere.WindZones.FindIndex(item => item.Id == currentSelection.Id);
        if (currentSelection.Kind != SceneSelectionKind.WindZone || index < 0)
        {
            return;
        }
        if (!TryReadVector(WindPositionX, WindPositionY, WindPositionZ, out var position) ||
            !TryReadVector(WindDimensionX, WindDimensionY, WindDimensionZ, out var dimensions) ||
            !TryReadVector(WindRotationX, WindRotationY, WindRotationZ, out var rotation) ||
            !TryReadVector(WindScaleX, WindScaleY, WindScaleZ, out var scale) ||
            !TryReadVector(WindVelocityX, WindVelocityY, WindVelocityZ, out var velocity) ||
            !TryReadNumber(WindTurbulence, out var turbulence) ||
            !TryReadNumber(WindNoiseSeed, out var noiseSeed))
        {
            StatusText.Text = "Wind edit rejected · bounds, transform, velocity, turbulence, and seed must be finite numbers";
            return;
        }
        var original = scene.Atmosphere.WindZones[index];
        var replacement = original with
        {
            Transform = original.Transform with
            {
                PositionMetres = position,
                RotationDegrees = rotation,
                Scale = scale
            },
            DimensionsMetres = dimensions,
            VelocityMetresPerSecond = velocity,
            Turbulence = turbulence,
            NoiseSeed = noiseSeed
        };
        if (!SceneAuthoringOperations.TryReplaceWindZone(scene, original.Id, replacement, out var error))
        {
            RejectSceneEdit($"{original.Name} wind field", error);
            PopulateInspector(currentSelection);
            return;
        }
        CommitSceneEdit($"{original.Name} wind field");
        PopulateInspector(currentSelection);
    }

    private void OnApplyLayerClicked(object? sender, RoutedEventArgs e)
    {
        var index = currentSelection.Index;
        if (currentSelection.Kind != SceneSelectionKind.AtmosphereLayer || index < 0 || index >= scene.Atmosphere.Layers.Count)
        {
            return;
        }
        if (!TryReadNumber(LayerBaseAltitude, out var baseAltitude) ||
            !TryReadNumber(LayerTopAltitude, out var topAltitude) ||
            !TryReadNumber(LayerTemperature, out var temperature) ||
            !TryReadNumber(LayerHumidity, out var humidity) ||
            !TryReadNumber(LayerPressure, out var pressure))
        {
            StatusText.Text = "Air-layer edit rejected · enter finite altitude and atmosphere values";
            return;
        }
        var original = scene.Atmosphere.Layers[index];
        var replacement = original with
        {
            BaseAltitudeMetres = baseAltitude,
            TopAltitudeMetres = topAltitude,
            TemperatureCelsius = temperature,
            RelativeHumidityPercent = humidity,
            PressureKilopascals = pressure
        };
        if (!SceneAuthoringOperations.TryReplaceAtmosphereLayer(scene, index, replacement, out var error))
        {
            RejectSceneEdit($"Atmosphere layer {index + 1}", error);
            PopulateInspector(currentSelection);
            return;
        }
        CommitSceneEdit($"Atmosphere layer {index + 1}");
        RefreshSceneTree();
        PopulateInspector(currentSelection);
    }

    private void OnApplyBaseAtmosphereClicked(object? sender, RoutedEventArgs e)
    {
        if (!TryReadNumber(BaseTemperature, out var temperature) ||
            !TryReadNumber(BaseHumidity, out var humidity) ||
            !TryReadNumber(BasePressure, out var pressure))
        {
            StatusText.Text = "Base-air edit rejected · enter finite temperature, humidity, and pressure";
            return;
        }
        var replacement = scene.Atmosphere with
        {
            TemperatureCelsius = temperature,
            RelativeHumidityPercent = humidity,
            PressureKilopascals = pressure
        };
        if (!SceneAuthoringOperations.TryReplaceBaseAtmosphere(scene, replacement, out var updatedScene, out var error))
        {
            RejectSceneEdit("Base atmosphere", error);
            PopulateInspector(currentSelection);
            return;
        }
        scene = updatedScene;
        CommitSceneEdit("Base atmosphere");
        PopulateInspector(currentSelection);
    }

    private void OnApplyReceiverClicked(object? sender, RoutedEventArgs e)
    {
        var index = scene.ReceiverRigs.FindIndex(item => item.Id == currentSelection.Id);
        if (currentSelection.Kind != SceneSelectionKind.Receiver || index < 0)
        {
            return;
        }
        var name = ReceiverNameBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(name) ||
            !TryReadVector(ReceiverPositionX, ReceiverPositionY, ReceiverPositionZ, out var position) ||
            !TryReadNumber(ReceiverYaw, out var yaw))
        {
            StatusText.Text = "Receiver edit rejected · enter a take name and finite position/yaw values";
            return;
        }

        var original = scene.ReceiverRigs[index];
        var replacement = original with
        {
            Name = name,
            Transform = original.Transform with
            {
                PositionMetres = position,
                RotationDegrees = new Vector3d(
                    original.Transform.RotationDegrees.X,
                    yaw,
                    original.Transform.RotationDegrees.Z)
            },
            MotionPathId = (ReceiverMotionPathBox.SelectedItem as MotionPathChoice)?.Id,
            Enabled = ReceiverEnabledBox.IsChecked == true
        };

        if (original.Kind == ReceiverRigKind.BinauralHead)
        {
            if (!TryReadNumber(ReceiverEarSpacing, out var spacing) ||
                !TryReadNumber(ReceiverHeadRadius, out var radius) ||
                !TryReadVector(ReceiverHeadDimensionX, ReceiverHeadDimensionY, ReceiverHeadDimensionZ, out var dimensions))
            {
                StatusText.Text = "Binaural edit rejected · ear spacing, head radius, and dimensions must be finite SI values";
                return;
            }
            var mode = (ReceiverHrtfModeBox.SelectedItem as HrtfModeChoice)?.Mode ?? HrtfMode.Parametric;
            var sofaHash = mode == HrtfMode.Parametric
                ? null
                : (ReceiverSofaAssetBox.SelectedItem as HrtfAssetChoice)?.Sha256;
            replacement = replacement with
            {
                SpacingMetres = spacing,
                HeadRadiusMetres = radius,
                HeadDimensionsMetres = dimensions,
                HrtfMode = mode,
                SofaAssetHash = sofaHash
            };
        }
        else
        {
            var (originalLeft, originalRight) = ReceiverRigGeometry.EffectiveMicrophoneCapsules(original);
            if (!TryReadCapsule(originalLeft, isLeft: true, out var left) ||
                !TryReadCapsule(originalRight, isLeft: false, out var right))
            {
                StatusText.Text = "Microphone edit rejected · capsule offsets, rotations, sensitivity, and self-noise must be finite values";
                return;
            }
            var spacing = (right.RelativeTransform.PositionMetres - left.RelativeTransform.PositionMetres).Length;
            replacement = replacement with
            {
                SpacingMetres = Math.Max(0.01, spacing),
                AngleDegrees = right.RelativeTransform.RotationDegrees.Y - left.RelativeTransform.RotationDegrees.Y,
                PolarPattern = left.PolarPattern == right.PolarPattern ? left.PolarPattern : original.PolarPattern,
                SensitivityDb = 0.5 * (left.SensitivityDb + right.SensitivityDb),
                SelfNoiseDbSpl = 0.5 * (left.SelfNoiseDbSpl + right.SelfNoiseDbSpl),
                LeftCapsule = left,
                RightCapsule = right
            };
        }

        if (!SceneAuthoringOperations.TryReplaceReceiverRig(scene, original.Id, replacement, out var error))
        {
            RejectSceneEdit($"{original.Name} receiver", error);
            PopulateInspector(currentSelection);
            return;
        }
        CommitSceneEdit($"{replacement.Name} receiver rig");
        RefreshReceiverSummary();
        RefreshSceneTree();
        PopulateInspector(currentSelection);
    }

    private bool TryReadCapsule(MicrophoneCapsule original, bool isLeft, out MicrophoneCapsule capsule)
    {
        var positionX = isLeft ? LeftCapsulePositionX : RightCapsulePositionX;
        var positionY = isLeft ? LeftCapsulePositionY : RightCapsulePositionY;
        var positionZ = isLeft ? LeftCapsulePositionZ : RightCapsulePositionZ;
        var rotationX = isLeft ? LeftCapsuleRotationX : RightCapsuleRotationX;
        var rotationY = isLeft ? LeftCapsuleRotationY : RightCapsuleRotationY;
        var rotationZ = isLeft ? LeftCapsuleRotationZ : RightCapsuleRotationZ;
        var patternBox = isLeft ? LeftCapsulePatternBox : RightCapsulePatternBox;
        var sensitivity = isLeft ? LeftCapsuleSensitivity : RightCapsuleSensitivity;
        var selfNoise = isLeft ? LeftCapsuleSelfNoise : RightCapsuleSelfNoise;
        if (!TryReadVector(positionX, positionY, positionZ, out var position) ||
            !TryReadVector(rotationX, rotationY, rotationZ, out var rotation) ||
            !TryReadNumber(sensitivity, out var sensitivityDb) ||
            !TryReadNumber(selfNoise, out var selfNoiseDbSpl) ||
            patternBox.SelectedItem is not PolarPatternChoice pattern)
        {
            capsule = original;
            return false;
        }
        capsule = original with
        {
            RelativeTransform = original.RelativeTransform with
            {
                PositionMetres = position,
                RotationDegrees = rotation
            },
            PolarPattern = pattern.Pattern,
            SensitivityDb = sensitivityDb,
            SelfNoiseDbSpl = selfNoiseDbSpl
        };
        return true;
    }

    private void OnRemoveSelectedClicked(object? sender, RoutedEventArgs e)
    {
        StopPreviewTransport();
        string? removedName = null;
        var nextSelection = SceneSelection.BaseAtmosphere();
        switch (currentSelection.Kind)
        {
            case SceneSelectionKind.Material:
                {
                    var material = scene.Materials.FirstOrDefault(item => item.Id == currentSelection.Id);
                    if (material is null) return;
                    removedName = material.Name;
                    if (!SceneAuthoringOperations.TryRemoveMaterial(scene, material.Id, out var error))
                    {
                        RejectSceneEdit($"Remove {material.Name}", error);
                        PopulateInspector(currentSelection);
                        return;
                    }
                    nextSelection = SceneSelection.Material(scene.Materials[0].Id);
                    break;
                }
            case SceneSelectionKind.Geometry:
                {
                    var index = scene.Geometry.FindIndex(item => item.Id == currentSelection.Id);
                    if (index < 0) return;
                    removedName = scene.Geometry[index].Name;
                    scene.Geometry.RemoveAt(index);
                    break;
                }
            case SceneSelectionKind.WindZone:
                {
                    var index = scene.Atmosphere.WindZones.FindIndex(item => item.Id == currentSelection.Id);
                    if (index < 0) return;
                    removedName = scene.Atmosphere.WindZones[index].Name;
                    scene.Atmosphere.WindZones.RemoveAt(index);
                    break;
                }
            case SceneSelectionKind.AtmosphereLayer:
                if (currentSelection.Index < 0 || currentSelection.Index >= scene.Atmosphere.Layers.Count) return;
                removedName = $"Atmosphere layer {currentSelection.Index + 1}";
                scene.Atmosphere.Layers.RemoveAt(currentSelection.Index);
                break;
            case SceneSelectionKind.Receiver:
                {
                    var index = scene.ReceiverRigs.FindIndex(item => item.Id == currentSelection.Id);
                    if (index < 0) return;
                    var receiver = scene.ReceiverRigs[index];
                    if (scene.ReceiverRigs.Count == 1 ||
                        (receiver.Enabled && scene.ReceiverRigs.Count(item => item.Enabled) == 1))
                    {
                        StatusText.Text = "Receiver removal rejected · at least one enabled stereo take must remain";
                        return;
                    }
                    if (scene.CaptureTakes.Any(take => take.ReceiverRigId == receiver.Id))
                    {
                        StatusText.Text = "Receiver removal rejected · recorded takes still reference this rig";
                        return;
                    }
                    removedName = receiver.Name;
                    scene.ReceiverRigs.RemoveAt(index);
                    var remaining = scene.ReceiverRigs.FirstOrDefault(item => item.Enabled) ?? scene.ReceiverRigs[0];
                    nextSelection = SceneSelection.Receiver(remaining.Id);
                    break;
                }
            default:
                return;
        }
        currentSelection = nextSelection;
        MarkSceneEdited($"{removedName} removed from the scene · save to persist this change");
        RefreshReceiverSummary();
        RefreshSceneTree();
        PopulateInspector(currentSelection);
    }

    private void OnPlayClicked(object? sender, RoutedEventArgs e)
    {
        StopPreviewTransport();
        var profile = GetActiveRenderProfile();
        previewStarted = DateTimeOffset.UtcNow - TimeSpan.FromSeconds(Viewport.TimeSeconds);
        previewDurationSeconds = scene.Timeline.DurationSeconds;
        previewAdvancesScene = true;
        previewCompletionStatus = "Preview complete · scene state preserved";
        previewInterruptedStatus = "Visual-only preview · the WASAPI device stopped or was rerouted";
        previewTimer.Start();
        PlayButton.Content = "▶  Playing";
        try
        {
            audioPreview = NativeSceneRenderer.StartPreview(
                scene,
                sampleRate: (uint)profile.SampleRate,
                periodFrames: (uint)profile.BufferFrames,
                reflectionOrder: (uint)profile.ReflectionOrder,
                rayCount: (uint)profile.RayCount,
                impulseResponseSeconds: profile.ImpulseResponseSeconds,
                seed: (uint)profile.Seed,
                startTimeSeconds: Viewport.TimeSeconds,
                projectDirectory: lastProjectDirectory);
            var status = audioPreview.Status;
            StatusText.Text = $"Audible WASAPI preview · {profile.Name} · {status.SampleRate:N0} Hz · {profile.RayCount:N0} diffuse-detail budget";
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
        if (SceneSelectionResolver.MotionPath(scene, currentSelection) is null)
        {
            Viewport.IsGestureRecording = false;
            GestureButton.Content = "●  Gesture";
            StatusText.Text = "Gesture capture requires a selected source, receiver, or motion path that already owns a path";
            return;
        }
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
            string? recoveryWarning = null;
            try
            {
                await ResetRecoveryContextAsync(loaded, projectDirectory);
            }
            catch (Exception exception)
            {
                DisableRecovery(exception);
                recoveryWarning = " · autosave unavailable for this session";
            }
            initializingControls = true;
            ApplySceneToControls(loaded, projectDirectory);
            ClearRecoveredState();
            StatusText.Text = $"Project opened · {projectDirectory}{recoveryWarning}";
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

        string hash;
        try
        {
            scene = scene with { UpdatedAtUtc = DateTimeOffset.UtcNow };
            Viewport.Scene = scene;
            GpuViewport.Scene = scene;
            hash = await ProjectStore.SaveAsync(scene, projectDirectory);
        }
        catch (Exception exception)
        {
            StatusText.Text = $"Project save failed: {exception.Message}";
            return;
        }

        var projectDirectoryChanged = !string.Equals(
            lastProjectDirectory is null ? null : Path.GetFullPath(lastProjectDirectory),
            Path.GetFullPath(projectDirectory),
            StringComparison.OrdinalIgnoreCase);
        lastProjectDirectory = projectDirectory;
        if (projectDirectoryChanged)
        {
            InvalidateReflectionMonitorConfiguration();
        }
        GpuViewport.SetScene(scene, projectDirectory);
        sceneDirty = false;
        sceneEditedAtUtc = null;
        string? recoveryWarning = null;
        try
        {
            await MarkRecoverySavedAsync(scene, projectDirectory);
        }
        catch (Exception exception)
        {
            DisableRecovery(exception);
            recoveryWarning = " · autosave index unavailable";
        }
        ClearRecoveredState();
        RefreshSpectralWorkspace();
        RefreshReceiverTakeWorkspace();
        StatusText.Text = $"Project saved atomically · {hash[..12]}… · {projectDirectory}{recoveryWarning}";
        ScheduleReflectionDiagnostics();
    }

    private async void OnImportSofaClicked(object? sender, RoutedEventArgs e)
    {
        if (currentSelection.Kind != SceneSelectionKind.Receiver)
        {
            return;
        }
        var receiver = scene.ReceiverRigs.FirstOrDefault(item => item.Id == currentSelection.Id);
        if (receiver is null || receiver.Kind != ReceiverRigKind.BinauralHead)
        {
            return;
        }
        if (lastProjectDirectory is null)
        {
            StatusText.Text = "Save this project before importing a SOFA dataset so its private asset store has a stable location";
            return;
        }

        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import a measured SOFA HRTF dataset",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("SOFA HRTF datasets") { Patterns = ["*.sofa"] }
            ]
        });
        if (files.Count == 0)
        {
            return;
        }

        ImportSofaButton.IsEnabled = false;
        string? newlyDeclaredAssetHash = null;
        try
        {
            StopPreviewTransport();
            var sourcePath = files[0].Path.LocalPath;
            var source = new FileInfo(sourcePath);
            if (!source.Exists || !string.Equals(source.Extension, ".sofa", StringComparison.OrdinalIgnoreCase) ||
                source.Length is <= 0 or > 256L * 1024 * 1024)
            {
                throw new InvalidDataException("SOFA datasets must use the .sofa extension and contain between 1 byte and 256 MiB.");
            }
            StatusText.Text = "Hashing and copying the SOFA dataset into the private project asset store…";
            var imported = await ProjectStore.ImportAssetAsync(
                lastProjectDirectory,
                sourcePath,
                AssetKind.Hrtf,
                "application/sofa",
                provenance: "PulseGen editor local SOFA import");
            var existing = scene.Assets.FirstOrDefault(asset =>
                string.Equals(asset.Sha256, imported.Sha256, StringComparison.OrdinalIgnoreCase));
            if (existing is not null && existing.Kind != AssetKind.Hrtf)
            {
                throw new InvalidDataException("The same content hash is already declared as a non-HRTF asset.");
            }
            if (existing is null)
            {
                scene.Assets.Add(imported);
                newlyDeclaredAssetHash = imported.Sha256;
            }

            var replacement = receiver with
            {
                HrtfMode = HrtfMode.ExactSofa,
                SofaAssetHash = imported.Sha256
            };
            if (!SceneAuthoringOperations.TryReplaceReceiverRig(scene, receiver.Id, replacement, out var error))
            {
                if (newlyDeclaredAssetHash is not null)
                {
                    scene.Assets.RemoveAll(asset => string.Equals(
                        asset.Sha256,
                        newlyDeclaredAssetHash,
                        StringComparison.OrdinalIgnoreCase));
                }
                throw new InvalidDataException(error?.Message ?? "The SOFA dataset could not be assigned to this receiver.");
            }
            CommitSceneEdit($"Imported measured dataset {imported.FileName}");
            PopulateInspector(currentSelection);
            StatusText.Text = $"Imported {imported.FileName} · exact SOFA selection saved · rendering remains gated until the Steam Audio adapter is active";
        }
        catch (Exception exception)
        {
            StatusText.Text = $"SOFA import failed without changing the receiver: {exception.Message}";
        }
        finally
        {
            ImportSofaButton.IsEnabled = true;
        }
    }

    private async void OnImportMeshClicked(object? sender, RoutedEventArgs e)
    {
        if (lastProjectDirectory is null)
        {
            StatusText.Text = "Save the scene as a .pulseproj before importing a content-addressed mesh";
            return;
        }
        var material = scene.Materials.FirstOrDefault();
        if (material is null)
        {
            StatusText.Text = "Create an acoustic material before importing mesh geometry";
            return;
        }

        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import a bounded acoustic OBJ or self-contained glTF/GLB",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("Acoustic meshes") { Patterns = ["*.obj", "*.gltf", "*.glb"] }
            ]
        });
        if (files.Count == 0)
        {
            return;
        }

        ImportMeshButton.IsEnabled = false;
        string? newlyDeclaredAssetHash = null;
        var geometryAdded = false;
        try
        {
            StopPreviewTransport();
            var sourcePath = files[0].Path.LocalPath;
            var extension = Path.GetExtension(sourcePath).ToLowerInvariant();
            var mediaType = extension switch
            {
                ".obj" => "model/obj",
                ".gltf" => "model/gltf+json",
                ".glb" => "model/gltf-binary",
                _ => throw new NotSupportedException("Choose an OBJ, glTF, or GLB mesh.")
            };
            var source = new FileInfo(sourcePath);
            if (!source.Exists || source.Length is <= 0 or > 512L * 1024 * 1024)
            {
                throw new InvalidDataException("Mesh files must contain between 1 byte and 512 MiB.");
            }

            StatusText.Text = "Hashing and copying the mesh into the private project asset store…";
            var imported = await ProjectStore.ImportAssetAsync(
                lastProjectDirectory,
                sourcePath,
                AssetKind.Mesh,
                mediaType,
                provenance: "PulseGen editor local import");
            var existing = scene.Assets.FirstOrDefault(asset =>
                string.Equals(asset.Sha256, imported.Sha256, StringComparison.OrdinalIgnoreCase));
            if (existing is not null && existing.Kind != AssetKind.Mesh)
            {
                throw new InvalidDataException("The same content hash is already declared as a non-mesh asset.");
            }
            if (existing is null)
            {
                scene.Assets.Add(imported);
                newlyDeclaredAssetHash = imported.Sha256;
            }

            var baseName = Path.GetFileNameWithoutExtension(imported.FileName);
            if (string.IsNullOrWhiteSpace(baseName))
            {
                baseName = "Imported mesh";
            }
            var geometryName = SceneSelectionResolver.UniqueName(
                baseName,
                scene.Geometry.Select(item => item.Name));
            var geometry = new AcousticGeometry
            {
                Id = Guid.NewGuid(),
                Name = geometryName,
                Kind = GeometryKind.ImportedMesh,
                MaterialId = material.Id,
                MeshAssetHash = imported.Sha256,
                DimensionsMetres = new(2, 2, 2),
                Occludes = true,
                Reflects = true
            };
            StatusText.Text = "Verifying triangles and retaining source material regions…";
            var inspectionScene = scene with { Geometry = [.. scene.Geometry, geometry] };
            var inspection = await Task.Run(() =>
                NativeSceneRenderer.DecodeImportedMeshes(inspectionScene, [geometry.Id], lastProjectDirectory).Single());
            geometry = geometry with
            {
                DimensionsMetres = new(
                    Math.Max(0.001, inspection.BoundsMaximum.X - inspection.BoundsMinimum.X),
                    Math.Max(0.001, inspection.BoundsMaximum.Y - inspection.BoundsMinimum.Y),
                    Math.Max(0.001, inspection.BoundsMaximum.Z - inspection.BoundsMinimum.Z)),
                MaterialBindings = inspection.MaterialRegions.Select(region => new ImportedMeshMaterialBinding
                {
                    SourceMeshIndex = region.SourceMeshIndex,
                    SourceMaterialIndex = region.SourceMaterialIndex,
                    SourceMeshName = region.SourceMeshName,
                    SourceMaterialName = region.SourceMaterialName,
                    MaterialId = material.Id
                }).ToList()
            };
            if (!SceneAuthoringOperations.TryAddGeometry(scene, geometry, out var error))
            {
                if (newlyDeclaredAssetHash is not null)
                {
                    scene.Assets.RemoveAll(asset => string.Equals(
                        asset.Sha256,
                        newlyDeclaredAssetHash,
                        StringComparison.OrdinalIgnoreCase));
                }
                throw new InvalidDataException(error?.Message ?? "The imported mesh could not be added to the scene.");
            }
            geometryAdded = true;

            currentSelection = SceneSelection.Geometry(geometry.Id);
            CommitSceneEdit($"Imported {imported.FileName}");
            RefreshSceneTree();
            PopulateInspector(currentSelection);
            StatusText.Text = $"Imported {imported.FileName} · {inspection.TriangleCount:N0} verified triangles · {geometry.MaterialBindings.Count} source material region{(geometry.MaterialBindings.Count == 1 ? string.Empty : "s")} · bounded first/second echoes active · save to commit the manifest";
        }
        catch (Exception exception)
        {
            if (!geometryAdded && newlyDeclaredAssetHash is not null)
            {
                scene.Assets.RemoveAll(asset => string.Equals(
                    asset.Sha256,
                    newlyDeclaredAssetHash,
                    StringComparison.OrdinalIgnoreCase));
            }
            StatusText.Text = $"Mesh import failed without changing the scene manifest: {exception.Message}";
        }
        finally
        {
            ImportMeshButton.IsEnabled = true;
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

        var emitter = SceneSelectionResolver.Emitter(scene, currentSelection);
        if (emitter is null)
        {
            return;
        }
        var nodeIndex = emitter.SignalGraph.Nodes.FindIndex(node => node.Kind == SignalNodeKind.Oscillator);
        if (nodeIndex < 0)
        {
            return;
        }
        var originalNode = emitter.SignalGraph.Nodes[nodeIndex];
        var parameters = new Dictionary<string, double>(originalNode.NumericParameters, StringComparer.Ordinal);
        if (waveform == WaveformKind.Additive && !parameters.ContainsKey("partial.0.amplitude"))
        {
            parameters["partial.0.ratio"] = 1;
            parameters["partial.0.amplitude"] = 1;
            parameters["partial.1.ratio"] = 2;
            parameters["partial.1.amplitude"] = 0.35;
            parameters["partial.2.ratio"] = 3;
            parameters["partial.2.amplitude"] = 0.18;
        }
        var nodes = emitter.SignalGraph.Nodes.ToList();
        nodes[nodeIndex] = originalNode with { Waveform = waveform, NumericParameters = parameters };
        var replacement = emitter with
        {
            SignalGraph = emitter.SignalGraph with { Nodes = nodes }
        };
        if (!SceneAuthoringOperations.TryReplaceEmitter(scene, emitter.Id, replacement, out var error))
        {
            RejectSceneEdit($"{emitter.Name} primitive", error);
            PopulateInspector(currentSelection);
            return;
        }

        CommitSceneEdit($"{emitter.Name} primitive changed to {selected}");
        PopulateInspector(currentSelection);
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

        var emitter = SceneSelectionResolver.Emitter(scene, currentSelection);
        if (emitter is null)
        {
            return;
        }
        var nodeIndex = emitter.SignalGraph.Nodes.FindIndex(item => item.Kind == SignalNodeKind.Oscillator);
        if (nodeIndex < 0)
        {
            StatusText.Text = "Signal edit rejected · the selected source has no oscillator node";
            return;
        }

        var nodes = emitter.SignalGraph.Nodes.ToList();
        var node = nodes[nodeIndex];
        var parameters = new Dictionary<string, double>(node.NumericParameters, StringComparer.Ordinal)
        {
            ["frequencyHz"] = frequency
        };
        nodes[nodeIndex] = node with { NumericParameters = parameters };
        var replacement = emitter with
        {
            GainLinear = gain,
            SignalGraph = emitter.SignalGraph with { Nodes = nodes }
        };
        if (!SceneAuthoringOperations.TryReplaceEmitter(scene, emitter.Id, replacement, out var error))
        {
            RejectSceneEdit($"{emitter.Name} signal", error);
            PopulateInspector(currentSelection);
            return;
        }

        CommitSceneEdit($"{emitter.Name} primitive parameters");
        PopulateInspector(currentSelection);
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
            var profile = GetActiveRenderProfile();
            var receipt = await Task.Run(() => NativeSceneRenderer.Render(scene, output, profile, lastProjectDirectory));
            lastRenderDirectory = output;
            RefreshAnalysisTakeChoices();
            if (lastProjectDirectory is not null && !sceneDirty)
            {
                try
                {
                    var imported = await CaptureTakeImporter.ImportAsync(scene, lastProjectDirectory, receipt.RenderReceiptPath);
                    scene = imported.Scene;
                    Viewport.Scene = scene;
                    GpuViewport.Scene = scene;
                    sceneDirty = false;
                    RefreshReceiverTakeWorkspace(scene.CaptureTakes.LastOrDefault()?.Id);
                    StatusText.Text = $"Rendered and recorded {imported.ImportedTakeCount} stereo takes · {imported.AddedAssetCount} immutable assets added · peak {Math.Max(receipt.PeakLeft, receipt.PeakRight):0.000}";
                }
                catch (Exception exception)
                {
                    StatusText.Text = $"Render completed, but project take import was rejected without changing the manifest: {exception.Message}";
                }
                return;
            }
            StatusText.Text = $"Rendered {receipt.FilesWritten} external stereo files with {profile.Name} · save the scene before rendering to add them to Receiver Takes · {output}";
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
        if (lastProjectDirectory is null)
        {
            StatusText.Text = "Save this scene as a .pulseproj before analysis so verified layers have a private asset store";
            return;
        }
        if (sceneDirty)
        {
            StatusText.Text = "Save the current scene before analysis so the receiver take and project revision stay together";
            return;
        }
        if (AnalysisTakeBox.SelectedItem is not AnalysisTakeChoice take || !File.Exists(take.FilePath))
        {
            StatusText.Text = "Choose an available stereo receiver take from the decomposition panel";
            return;
        }
        if (AnalysisChannelModeBox.SelectedItem is not AnalysisModeChoice mode)
        {
            StatusText.Text = "Choose how the left and right channels should be interpreted";
            return;
        }
        if (!TryReadInteger(AnalysisMaxPartialsBox, out var maxPartials) || maxPartials is < 1 or > 256)
        {
            StatusText.Text = "Tones kept per frame must be a whole number from 1 to 256";
            return;
        }

        var input = take.FilePath;
        var output = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "PulseGen",
            "Analysis",
            $"{Path.GetFileNameWithoutExtension(input)}-{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}");
        analysisCancellation = new CancellationTokenSource();
        AnalyzeButton.Content = "■  Cancel analysis";
        AnalysisResultText.Text = $"Analyzing {take.DisplayName} locally…";
        try
        {
            await using var host = await ModelHostClient.StartAsync(analysisCancellation.Token);
            host.ProgressChanged += (_, progress) => Dispatcher.UIThread.Post(() =>
                StatusText.Text = $"Analysis {progress.Progress:P0} · {progress.Message}");
            var hostResult = await host.AnalyzeAsync(
                input,
                output,
                mode.Mode,
                maxPartials,
                analysisCancellation.Token);
            StatusText.Text = "Verifying hashes and reconstruction before changing the project…";
            var imported = await AnalysisArtifactImporter.ImportAsync(
                scene,
                lastProjectDirectory,
                hostResult.OutputDirectory,
                input,
                mode.Mode,
                maxPartials,
                analysisCancellation.Token);
            scene = imported.Scene;
            Viewport.Scene = scene;
            GpuViewport.Scene = scene;
            sceneDirty = false;
            RefreshSpectralWorkspace(imported.AnalysisReceiptSha256);
            var artifactCount = imported.DeclaredAssetCount + imported.ReusedAssetCount;
            AnalysisResultText.Text = $"{imported.PartialTrackCount:N0} tone tracks · null RMS {imported.RmsNull:0.0e+0} · {artifactCount} verified artifacts ({imported.DeclaredAssetCount} new)";
            StatusText.Text = $"Analysis imported atomically · {mode.DisplayName} · {imported.PartialTrackCount:N0} tone tracks · {output}";
        }
        catch (OperationCanceledException)
        {
            AnalysisResultText.Text = "Analysis cancelled. No analysis assets were declared by the project.";
            StatusText.Text = "Analysis cancelled · no partial result was imported";
        }
        catch (Exception exception)
        {
            AnalysisResultText.Text = $"Analysis was not imported: {exception.Message}";
            StatusText.Text = $"Analysis failed: {exception.Message}";
        }
        finally
        {
            analysisCancellation.Dispose();
            analysisCancellation = null;
            AnalyzeButton.Content = "⌁  Analyze";
        }
    }

    private void OnAnalysisChannelModeChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (AnalysisChannelModeBox.SelectedItem is AnalysisModeChoice mode)
        {
            AnalysisModeHelpText.Text = mode.Description;
        }
    }

    private void RefreshAnalysisTakeChoices()
    {
        var selectedPath = (AnalysisTakeBox.SelectedItem as AnalysisTakeChoice)?.FilePath;
        var choices = lastRenderDirectory is not null && Directory.Exists(lastRenderDirectory)
            ? Directory.EnumerateFiles(lastRenderDirectory, "*.wav")
                .Order(StringComparer.Ordinal)
                .Select(path => new AnalysisTakeChoice(path, Path.GetFileNameWithoutExtension(path)))
                .ToList()
            : [];
        AnalysisTakeBox.ItemsSource = choices;
        AnalysisTakeBox.SelectedItem = choices.FirstOrDefault(choice =>
            string.Equals(choice.FilePath, selectedPath, StringComparison.OrdinalIgnoreCase)) ?? choices.FirstOrDefault();
        if (analysisCancellation is null)
        {
            AnalysisResultText.Text = choices.Count == 0
                ? "Render a receiver take, save the project, then choose Analyze."
                : $"{choices.Count} receiver take{(choices.Count == 1 ? string.Empty : "s")} ready · results will be verified before project import.";
        }
    }

    private void OnSignalWorkspaceClicked(object? sender, RoutedEventArgs e)
    {
        SignalWorkspace.IsVisible = true;
        SpectrumWorkspace.IsVisible = false;
        ReceiverTakesWorkspace.IsVisible = false;
        SignalWorkspaceButton.Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#71DCCF"));
        SpectrumWorkspaceButton.Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#8191A6"));
        ReceiverTakesWorkspaceButton.Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#8191A6"));
    }

    private void OnSpectrumWorkspaceClicked(object? sender, RoutedEventArgs e)
    {
        SignalWorkspace.IsVisible = false;
        SpectrumWorkspace.IsVisible = true;
        ReceiverTakesWorkspace.IsVisible = false;
        SignalWorkspaceButton.Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#8191A6"));
        SpectrumWorkspaceButton.Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#71DCCF"));
        ReceiverTakesWorkspaceButton.Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#8191A6"));
        if (lastProjectDirectory is not null && spectralSessions.Count == 0)
        {
            RefreshSpectralWorkspace();
        }
    }

    private void OnReceiverTakesWorkspaceClicked(object? sender, RoutedEventArgs e)
    {
        SignalWorkspace.IsVisible = false;
        SpectrumWorkspace.IsVisible = false;
        ReceiverTakesWorkspace.IsVisible = true;
        SignalWorkspaceButton.Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#8191A6"));
        SpectrumWorkspaceButton.Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#8191A6"));
        ReceiverTakesWorkspaceButton.Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#71DCCF"));
        if (lastProjectDirectory is not null && receiverTakeSessions.Count == 0)
        {
            RefreshReceiverTakeWorkspace();
        }
    }

    private async void RefreshSpectralWorkspace(string? preferredReceiptSha256 = null)
    {
        var version = ++spectralLoadVersion;
        if (lastProjectDirectory is null)
        {
            ClearSpectralWorkspace("Save the project and analyze a receiver take to populate this workspace.");
            return;
        }

        try
        {
            var sceneSnapshot = scene;
            var projectDirectory = lastProjectDirectory;
            var sessions = await Task.Run(() => SpectralAnalysisCatalog.LoadAsync(sceneSnapshot, projectDirectory));
            if (version != spectralLoadVersion)
            {
                return;
            }
            spectralSessions = sessions;
            SpectralSessionBox.ItemsSource = sessions;
            SpectralSessionBox.IsEnabled = sessions.Count > 0;
            if (sessions.Count == 0)
            {
                ClearSpectralWorkspace("No imported decomposition exists in this project yet.");
                return;
            }
            var preferred = preferredReceiptSha256 ?? activeSpectralSession?.AnalysisReceiptSha256;
            SpectralSessionBox.SelectedItem = sessions.FirstOrDefault(session =>
                string.Equals(session.AnalysisReceiptSha256, preferred, StringComparison.OrdinalIgnoreCase)) ?? sessions[0];
        }
        catch (Exception exception)
        {
            if (version == spectralLoadVersion)
            {
                ClearSpectralWorkspace($"Spectral assets could not be verified: {exception.Message}");
            }
        }
    }

    private void ClearSpectralWorkspace(string message)
    {
        spectralSessions = [];
        activeSpectralSession = null;
        activeSpectralEditState = null;
        selectedSpectralTrack = null;
        pendingSpectralTrackEdits.Clear();
        SpectralSessionBox.ItemsSource = Array.Empty<SpectralAnalysisSession>();
        SpectralSessionBox.IsEnabled = false;
        SpectralSessionSummaryText.Text = message;
        SpectralEditStateText.Text = "No spectral edit loaded";
        SpectralTrackPlot.Tracks = [];
        SpectralTrackPlot.Edits = new Dictionary<string, SpectralTrackEdit>();
        SpectralTrackPlot.SelectedTrackId = null;
        SpectralTrackNameText.Text = "Click a tone path";
        SpectralTrackRangeText.Text = "Frequency and channel details appear here.";
        SetSpectralActionsEnabled(false);
    }

    private async void OnSpectralSessionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (SpectralSessionBox.SelectedItem is not SpectralAnalysisSession session || lastProjectDirectory is null)
        {
            return;
        }
        var selectionVersion = ++spectralSelectionVersion;
        try
        {
            var editState = await SpectralEditStore.LoadLatestAsync(scene, lastProjectDirectory, session);
            if (selectionVersion != spectralSelectionVersion || !Equals(SpectralSessionBox.SelectedItem, session))
            {
                return;
            }
            activeSpectralSession = session;
            activeSpectralEditState = editState;
            pendingSpectralTrackEdits.Clear();
            foreach (var edit in editState.Document.TrackEdits)
            {
                pendingSpectralTrackEdits[edit.TrackId] = edit;
            }

            SpectralPartialsEnabled.IsChecked = editState.Document.Partials.Enabled;
            SpectralPartialsGain.Text = FormatNumber(editState.Document.Partials.GainLinear);
            SpectralTransientsEnabled.IsChecked = editState.Document.Transients.Enabled;
            SpectralTransientsGain.Text = FormatNumber(editState.Document.Transients.GainLinear);
            SpectralStochasticEnabled.IsChecked = editState.Document.StochasticResidual.Enabled;
            SpectralStochasticGain.Text = FormatNumber(editState.Document.StochasticResidual.GainLinear);
            SpectralSessionSummaryText.Text = $"{session.DurationSeconds:0.000} s · {session.SampleRate:N0} Hz · {session.ChannelModeLabel} · null RMS {session.RmsNull:0.0e+0}";
            SpectralPlotTitleText.Text = $"{session.Tracks.Count:N0} TONE PATHS · TIME × LOG FREQUENCY";
            SpectralEditStateText.Text = editState.AssetSha256 is null
                ? "Fresh non-destructive edit · not saved"
                : $"Loaded immutable edit {editState.AssetSha256[..12]}…";
            SpectralTrackPlot.Tracks = session.Tracks;
            SpectralTrackPlot.Edits = pendingSpectralTrackEdits;
            SpectralTrackPlot.DurationSeconds = session.DurationSeconds;
            SpectralTrackPlot.SampleRate = session.SampleRate;
            SelectSpectralTrack(session.Tracks.OrderByDescending(track => track.PeakAmplitude).FirstOrDefault());
            SetSpectralActionsEnabled(true);
        }
        catch (Exception exception)
        {
            SpectralSessionSummaryText.Text = $"This analysis could not be opened: {exception.Message}";
            SetSpectralActionsEnabled(false);
        }
    }

    private void SelectSpectralTrack(SpectralPartialTrack? track)
    {
        selectedSpectralTrack = track;
        SpectralTrackPlot.SelectedTrackId = track?.Id;
        if (track is null)
        {
            SpectralTrackNameText.Text = "No tracked tone available";
            SpectralTrackRangeText.Text = "The residual layers can still be mixed and rendered.";
            SpectralTrackEnabled.IsEnabled = false;
            SpectralTrackGain.IsEnabled = false;
            SpectralTrackPitch.IsEnabled = false;
            return;
        }

        pendingSpectralTrackEdits.TryGetValue(track.Id, out var edit);
        edit ??= new SpectralTrackEdit { TrackId = track.Id };
        SpectralTrackNameText.Text = track.Id;
        var channelLabel = activeSpectralSession?.ChannelMode == ModelHostChannelMode.MidSide
            ? track.Channel == 0 ? "center (mid)" : "width (side)"
            : track.Channel == 0 ? "left" : "right";
        SpectralTrackRangeText.Text = $"{channelLabel} · {track.MinimumFrequencyHz:0.0}–{track.MaximumFrequencyHz:0.0} Hz · {track.Points.Count:N0} control points";
        SpectralTrackEnabled.IsEnabled = true;
        SpectralTrackGain.IsEnabled = true;
        SpectralTrackPitch.IsEnabled = true;
        SpectralTrackEnabled.IsChecked = edit.Enabled;
        SpectralTrackGain.Text = FormatNumber(edit.GainLinear);
        SpectralTrackPitch.Text = FormatNumber(edit.PitchSemitones);
    }

    private void OnApplySpectralTrackClicked(object? sender, RoutedEventArgs e)
    {
        if (selectedSpectralTrack is null)
        {
            StatusText.Text = "Select a tone path in the spectrum first";
            return;
        }
        if (!TryReadNumber(SpectralTrackGain, out var gain) || gain is < 0 or > 4 ||
            !TryReadNumber(SpectralTrackPitch, out var pitch) || pitch is < -24 or > 24)
        {
            StatusText.Text = "Tone edit rejected · gain must be 0–4 and pitch must be −24 to +24 semitones";
            return;
        }
        var edit = new SpectralTrackEdit
        {
            TrackId = selectedSpectralTrack.Id,
            Enabled = SpectralTrackEnabled.IsChecked == true,
            GainLinear = gain,
            PitchSemitones = pitch
        };
        if (edit.Enabled && Math.Abs(edit.GainLinear - 1) < 1.0e-12 && Math.Abs(edit.PitchSemitones) < 1.0e-12)
        {
            pendingSpectralTrackEdits.Remove(edit.TrackId);
        }
        else
        {
            pendingSpectralTrackEdits[edit.TrackId] = edit;
        }
        SpectralTrackPlot.Edits = new Dictionary<string, SpectralTrackEdit>(pendingSpectralTrackEdits, StringComparer.Ordinal);
        SpectralEditStateText.Text = $"Unsaved edit · {pendingSpectralTrackEdits.Count:N0} changed tone{(pendingSpectralTrackEdits.Count == 1 ? string.Empty : "s")}";
        StatusText.Text = $"{selectedSpectralTrack.Id} edit staged · imported analysis remains untouched";
    }

    private void OnResetSpectralTrackClicked(object? sender, RoutedEventArgs e)
    {
        if (selectedSpectralTrack is null)
        {
            return;
        }
        pendingSpectralTrackEdits.Remove(selectedSpectralTrack.Id);
        SpectralTrackPlot.Edits = new Dictionary<string, SpectralTrackEdit>(pendingSpectralTrackEdits, StringComparer.Ordinal);
        SelectSpectralTrack(selectedSpectralTrack);
        SpectralEditStateText.Text = $"Unsaved edit · {pendingSpectralTrackEdits.Count:N0} changed tone{(pendingSpectralTrackEdits.Count == 1 ? string.Empty : "s")}";
    }

    private bool TryBuildPendingSpectralEdit(out SpectralEditDocument document, out string error)
    {
        document = null!;
        if (activeSpectralSession is null || activeSpectralEditState is null)
        {
            error = "Select an imported analysis first.";
            return false;
        }
        if (!TryReadNumber(SpectralPartialsGain, out var partialGain) || partialGain is < 0 or > 4 ||
            !TryReadNumber(SpectralTransientsGain, out var transientGain) || transientGain is < 0 or > 4 ||
            !TryReadNumber(SpectralStochasticGain, out var stochasticGain) || stochasticGain is < 0 or > 4)
        {
            error = "Each spectral layer gain must be a finite value from 0 to 4.";
            return false;
        }
        document = activeSpectralEditState.Document with
        {
            Partials = new SpectralLayerSettings { Enabled = SpectralPartialsEnabled.IsChecked == true, GainLinear = partialGain },
            Transients = new SpectralLayerSettings { Enabled = SpectralTransientsEnabled.IsChecked == true, GainLinear = transientGain },
            StochasticResidual = new SpectralLayerSettings { Enabled = SpectralStochasticEnabled.IsChecked == true, GainLinear = stochasticGain },
            TrackEdits = pendingSpectralTrackEdits.Values.OrderBy(edit => edit.TrackId, StringComparer.Ordinal).ToList()
        };
        try
        {
            SpectralEditStore.Validate(document, activeSpectralSession);
            error = string.Empty;
            return true;
        }
        catch (Exception exception)
        {
            error = exception.Message;
            return false;
        }
    }

    private async void OnAuditionSpectralClicked(object? sender, RoutedEventArgs e)
    {
        if (spectralAuditionPlaying)
        {
            StopPreviewTransport();
            StatusText.Text = "Spectral audition stopped · project was not changed";
            return;
        }
        if (!TryBuildPendingSpectralEdit(out var edit, out var error) || activeSpectralSession is null)
        {
            StatusText.Text = $"Spectral audition unavailable · {error}";
            return;
        }
        StopPreviewTransport();
        SetSpectralBusy(true);
        spectralCancellation = new CancellationTokenSource();
        try
        {
            StatusText.Text = "Recombining verified layers and staged tone edits…";
            var rendered = await SpectralResynthesisService.RenderAsync(activeSpectralSession, edit, spectralCancellation.Token);
            var profile = GetActiveRenderProfile();
            audioPreview = NativeSceneRenderer.StartBufferPreview(
                rendered.Buffer.InterleavedSamples,
                (uint)rendered.Buffer.SampleRate,
                (uint)profile.BufferFrames);
            previewStarted = DateTimeOffset.UtcNow;
            previewDurationSeconds = rendered.Buffer.DurationSeconds;
            previewAdvancesScene = false;
            previewCompletionStatus = "Spectral audition complete · project was not changed";
            previewInterruptedStatus = "Spectral audition stopped · the WASAPI device was removed or rerouted";
            spectralAuditionPlaying = true;
            previewTimer.Start();
            PlayButton.Content = "▶  Auditioning";
            SpectralAuditionButton.Content = "■  Playing";
            StatusText.Text = $"Spectral audition · peak {rendered.Peak:0.000} · {rendered.Buffer.DurationSeconds:0.000} s · in-process WASAPI";
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Spectral audition cancelled";
        }
        catch (Exception exception)
        {
            StopPreviewTransport();
            StatusText.Text = $"Spectral audition failed: {exception.Message}";
        }
        finally
        {
            spectralCancellation.Dispose();
            spectralCancellation = null;
            SetSpectralBusy(false);
        }
    }

    private async void OnSaveSpectralEditClicked(object? sender, RoutedEventArgs e)
    {
        if (!TryBuildPendingSpectralEdit(out var edit, out var error) ||
            activeSpectralSession is null || activeSpectralEditState is null || lastProjectDirectory is null)
        {
            StatusText.Text = $"Spectral edit was not saved · {error}";
            return;
        }
        SetSpectralBusy(true);
        spectralCancellation = new CancellationTokenSource();
        try
        {
            var saved = await SpectralEditStore.SaveAsync(
                scene,
                lastProjectDirectory,
                activeSpectralSession,
                activeSpectralEditState,
                edit,
                spectralCancellation.Token);
            scene = saved.Scene;
            Viewport.Scene = scene;
            GpuViewport.Scene = scene;
            sceneDirty = false;
            activeSpectralEditState = saved.State;
            SpectralEditStateText.Text = $"Saved immutable edit {saved.Asset.Sha256[..12]}… · parent retained";
            StatusText.Text = $"Spectral edit saved atomically · {saved.Asset.Sha256[..12]}…";
        }
        catch (Exception exception)
        {
            StatusText.Text = $"Spectral edit save failed without replacing the current version: {exception.Message}";
        }
        finally
        {
            spectralCancellation.Dispose();
            spectralCancellation = null;
            SetSpectralBusy(false);
        }
    }

    private async void OnRenderSpectralClicked(object? sender, RoutedEventArgs e)
    {
        if (!TryBuildPendingSpectralEdit(out var edit, out var error) ||
            activeSpectralSession is null || activeSpectralEditState is null || lastProjectDirectory is null)
        {
            StatusText.Text = $"Spectral WAV was not rendered · {error}";
            return;
        }
        StopPreviewTransport();
        SetSpectralBusy(true);
        spectralCancellation = new CancellationTokenSource();
        try
        {
            StatusText.Text = "Saving the non-destructive edit before rendering its stereo mix…";
            var saved = await SpectralEditStore.SaveAsync(
                scene,
                lastProjectDirectory,
                activeSpectralSession,
                activeSpectralEditState,
                edit,
                spectralCancellation.Token);
            scene = saved.Scene;
            activeSpectralEditState = saved.State;
            Viewport.Scene = scene;
            GpuViewport.Scene = scene;
            sceneDirty = false;
            var rendered = await SpectralResynthesisService.RenderAsync(activeSpectralSession, saved.State.Document, spectralCancellation.Token);
            var outputDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "PulseGen",
                "SpectralRenders");
            var output = Path.Combine(
                outputDirectory,
                $"{SafeName(Path.GetFileNameWithoutExtension(activeSpectralSession.SourceFileName))}-spectral-{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}.wav");
            await Task.Run(() => FloatWaveFile.WriteAtomic(output, rendered.Buffer), spectralCancellation.Token);
            var imported = await ProjectStore.ImportAssetAsync(
                lastProjectDirectory,
                output,
                AssetKind.ModelOutput,
                "audio/wav",
                $"PulseGen spectral resynthesis; analysisReceiptSha256={activeSpectralSession.AnalysisReceiptSha256}; editSha256={saved.Asset.Sha256}",
                cancellationToken: spectralCancellation.Token);
            imported = imported with
            {
                SampleRate = rendered.Buffer.SampleRate,
                Channels = 2,
                DurationSeconds = rendered.Buffer.DurationSeconds
            };
            var assets = scene.Assets.ToList();
            var existing = assets.FirstOrDefault(asset => string.Equals(asset.Sha256, imported.Sha256, StringComparison.OrdinalIgnoreCase));
            if (existing is null)
            {
                assets.Add(imported);
            }
            else if (existing.Kind != imported.Kind || !string.Equals(existing.MediaType, imported.MediaType, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("Rendered spectral mix collides with an incompatible project asset.");
            }
            scene = scene with { Assets = assets, UpdatedAtUtc = DateTimeOffset.UtcNow };
            await ProjectStore.SaveAsync(scene, lastProjectDirectory, spectralCancellation.Token);
            Viewport.Scene = scene;
            GpuViewport.Scene = scene;
            sceneDirty = false;
            SpectralEditStateText.Text = $"Edit {saved.Asset.Sha256[..12]}… · stereo mix {imported.Sha256[..12]}…";
            StatusText.Text = $"Spectral stereo WAV rendered · peak {rendered.Peak:0.000} · {output}";
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Spectral render cancelled";
        }
        catch (Exception exception)
        {
            StatusText.Text = $"Spectral render failed: {exception.Message}";
        }
        finally
        {
            spectralCancellation.Dispose();
            spectralCancellation = null;
            SetSpectralBusy(false);
        }
    }

    private void SetSpectralBusy(bool busy)
    {
        var enabled = !busy && activeSpectralSession is not null;
        SetSpectralActionsEnabled(enabled);
        SpectralSessionBox.IsEnabled = !busy && spectralSessions.Count > 0;
    }

    private void SetSpectralActionsEnabled(bool enabled)
    {
        SpectralPartialsEnabled.IsEnabled = enabled;
        SpectralPartialsGain.IsEnabled = enabled;
        SpectralTransientsEnabled.IsEnabled = enabled;
        SpectralTransientsGain.IsEnabled = enabled;
        SpectralStochasticEnabled.IsEnabled = enabled;
        SpectralStochasticGain.IsEnabled = enabled;
        var trackEnabled = enabled && selectedSpectralTrack is not null;
        SpectralTrackEnabled.IsEnabled = trackEnabled;
        SpectralTrackGain.IsEnabled = trackEnabled;
        SpectralTrackPitch.IsEnabled = trackEnabled;
        SpectralApplyTrackButton.IsEnabled = trackEnabled;
        SpectralResetTrackButton.IsEnabled = trackEnabled;
        SpectralAuditionButton.IsEnabled = enabled;
        SpectralSaveEditButton.IsEnabled = enabled;
        SpectralRenderButton.IsEnabled = enabled;
    }

    private async void RefreshReceiverTakeWorkspace(Guid? preferredTakeId = null)
    {
        var version = ++receiverTakeLoadVersion;
        preferredTakeId ??= activeReceiverTake?.Take.Id;
        if (lastProjectDirectory is null)
        {
            ClearReceiverTakeWorkspace("Save the project, then render to record editable receiver takes.");
            return;
        }
        ClearReceiverTakeWorkspace("Verifying saved receiver takes…");
        try
        {
            var snapshot = scene;
            var projectDirectory = lastProjectDirectory;
            var sessions = await Task.Run(() => ReceiverTakeCatalog.LoadAsync(snapshot, projectDirectory));
            if (version != receiverTakeLoadVersion)
            {
                return;
            }
            receiverTakeSessions = sessions;
            ReceiverTakeList.ItemsSource = sessions;
            ReceiverTakeList.IsEnabled = sessions.Count > 0;
            ReceiverTakeTimeline.Sessions = sessions;
            var duration = Math.Max(scene.Timeline.DurationSeconds, sessions.Count == 0 ? 0 : sessions.Max(session => session.ArrangedEndSeconds));
            ReceiverTakeTimeline.TimelineDurationSeconds = Math.Max(duration, 1);
            ReceiverTimelineSummaryText.Text = $"{sessions.Count} take{(sessions.Count == 1 ? string.Empty : "s")} · {duration:0.000} s";
            ReceiverTimelineEndText.Text = $"{duration:0.###} s";
            ReceiverTakeCountText.Text = sessions.Count == 0
                ? "No project takes yet"
                : $"{sessions.Count} stereo take{(sessions.Count == 1 ? string.Empty : "s")} · {sessions.Count(session => session.Take.IncludedInMaster)} in master";
            if (sessions.Count == 0)
            {
                ClearSelectedReceiverTake();
                return;
            }
            var selected = sessions.FirstOrDefault(session => session.Take.Id == preferredTakeId) ?? sessions[0];
            ReceiverTakeList.SelectedItem = selected;
            SelectReceiverTake(selected);
        }
        catch (Exception exception)
        {
            if (version == receiverTakeLoadVersion)
            {
                ClearReceiverTakeWorkspace($"Receiver takes could not be verified: {exception.Message}");
            }
        }
    }

    private void ClearReceiverTakeWorkspace(string message)
    {
        receiverTakeSessions = [];
        activeReceiverTake = null;
        ReceiverTakeList.ItemsSource = Array.Empty<ReceiverTakeSession>();
        ReceiverTakeList.IsEnabled = false;
        ReceiverTakeTimeline.Sessions = [];
        ReceiverTakeTimeline.SelectedTakeId = null;
        ReceiverTakeTimeline.TimelineDurationSeconds = Math.Max(scene.Timeline.DurationSeconds, 1);
        ReceiverTakeCountText.Text = message;
        ReceiverTimelineSummaryText.Text = "0 project takes";
        ReceiverTimelineEndText.Text = $"{scene.Timeline.DurationSeconds:0.###} s";
        ClearSelectedReceiverTake();
    }

    private void ClearSelectedReceiverTake()
    {
        activeReceiverTake = null;
        ReceiverTakeNameText.Text = "Choose a recorded take";
        ReceiverTakeDetailsText.Text = "Channel and timing details appear here.";
        SetReceiverTakeActionsEnabled(false);
    }

    private void OnReceiverTakeSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (ReceiverTakeList.SelectedItem is ReceiverTakeSession session)
        {
            SelectReceiverTake(session);
        }
    }

    private void SelectReceiverTake(ReceiverTakeSession session)
    {
        activeReceiverTake = session;
        ReceiverTakeTimeline.SelectedTakeId = session.Take.Id;
        if (!ReferenceEquals(ReceiverTakeList.SelectedItem, session))
        {
            ReceiverTakeList.SelectedItem = session;
        }
        refreshingReceiverTakeControls = true;
        try
        {
            ReceiverTakeNameText.Text = session.Take.Name;
            ReceiverTakeDetailsText.Text = $"{session.Receiver.Name} · {session.SampleRate:N0} Hz · {session.DurationSeconds:0.000} s · immutable stereo source retained";
            ReceiverTakeIncluded.IsChecked = session.Take.IncludedInMaster;
            ReceiverTakeLinked.IsChecked = session.Take.ChannelsLinked;
            ReceiverTakeLeftMuted.IsChecked = session.Take.LeftMuted;
            ReceiverTakeRightMuted.IsChecked = session.Take.RightMuted;
            ReceiverTakeLeftGain.Text = FormatNumber(session.Take.LeftGainLinear);
            ReceiverTakeRightGain.Text = FormatNumber(session.Take.RightGainLinear);
            ReceiverTakeOffset.Text = FormatNumber(session.Take.TimelineOffsetSeconds);
            ReceiverTakeTrimStart.Text = FormatNumber(session.Take.TrimStartSeconds);
            ReceiverTakeTrimEnd.Text = session.Take.TrimEndSeconds is { } trimEnd ? FormatNumber(trimEnd) : string.Empty;
        }
        finally
        {
            refreshingReceiverTakeControls = false;
        }
        SetReceiverTakeActionsEnabled(true);
    }

    private void OnReceiverTakeLinkedChanged(object? sender, RoutedEventArgs e)
    {
        if (refreshingReceiverTakeControls || activeReceiverTake is null)
        {
            return;
        }
        if (ReceiverTakeLinked.IsChecked == true)
        {
            ReceiverTakeRightMuted.IsChecked = ReceiverTakeLeftMuted.IsChecked;
            ReceiverTakeRightGain.Text = ReceiverTakeLeftGain.Text;
        }
        SetReceiverTakeActionsEnabled(true);
    }

    private bool TryBuildReceiverTakeEdit(out CaptureTake replacement, out string error)
    {
        replacement = activeReceiverTake?.Take ?? new CaptureTake { Id = Guid.Empty, ReceiverRigId = Guid.Empty, Name = string.Empty };
        if (activeReceiverTake is null || lastProjectDirectory is null)
        {
            error = "Select a take in a saved project first.";
            return false;
        }
        if (!TryReadNumber(ReceiverTakeLeftGain, out var leftGain) || leftGain is < 0 or > 4 ||
            !TryReadNumber(ReceiverTakeRightGain, out var rightGain) || rightGain is < 0 or > 4 ||
            !TryReadNumber(ReceiverTakeOffset, out var offset) || offset is < 0 or > 3600 ||
            !TryReadNumber(ReceiverTakeTrimStart, out var trimStart) || trimStart < 0)
        {
            error = "Channel gains must be 0–4, and start/in times must be finite non-negative seconds.";
            return false;
        }
        double? trimEnd = null;
        if (!string.IsNullOrWhiteSpace(ReceiverTakeTrimEnd.Text))
        {
            if (!TryReadNumber(ReceiverTakeTrimEnd, out var parsedTrimEnd))
            {
                error = "The out point must be a number in seconds or left blank for the full take.";
                return false;
            }
            trimEnd = parsedTrimEnd;
        }
        var effectiveEnd = trimEnd ?? activeReceiverTake.DurationSeconds;
        if (trimStart >= effectiveEnd || effectiveEnd > activeReceiverTake.DurationSeconds + 1e-9)
        {
            error = $"The in/out range must stay inside this {activeReceiverTake.DurationSeconds:0.000}-second take.";
            return false;
        }
        var linked = ReceiverTakeLinked.IsChecked == true;
        replacement = activeReceiverTake.Take with
        {
            IncludedInMaster = ReceiverTakeIncluded.IsChecked == true,
            ChannelsLinked = linked,
            LeftMuted = ReceiverTakeLeftMuted.IsChecked == true,
            RightMuted = linked ? ReceiverTakeLeftMuted.IsChecked == true : ReceiverTakeRightMuted.IsChecked == true,
            LeftGainLinear = leftGain,
            RightGainLinear = linked ? leftGain : rightGain,
            TimelineOffsetSeconds = offset,
            TrimStartSeconds = trimStart,
            TrimEndSeconds = trimEnd
        };
        error = string.Empty;
        return true;
    }

    private async void OnApplyReceiverTakeClicked(object? sender, RoutedEventArgs e)
    {
        if (!TryBuildReceiverTakeEdit(out var replacement, out var error) || lastProjectDirectory is null)
        {
            StatusText.Text = $"Take edit was not applied · {error}";
            return;
        }
        SetReceiverTakeBusy(true);
        receiverTakeCancellation = new CancellationTokenSource();
        try
        {
            StopPreviewTransport();
            var takes = scene.CaptureTakes.ToList();
            var index = takes.FindIndex(take => take.Id == replacement.Id);
            if (index < 0)
            {
                throw new InvalidDataException("The selected take no longer exists in the project manifest.");
            }
            takes[index] = replacement;
            var updated = scene with { CaptureTakes = takes, UpdatedAtUtc = DateTimeOffset.UtcNow };
            SceneValidator.EnsureValid(updated);
            await ProjectStore.SaveAsync(updated, lastProjectDirectory, receiverTakeCancellation.Token);
            scene = updated;
            Viewport.Scene = scene;
            GpuViewport.Scene = scene;
            sceneDirty = false;
            StatusText.Text = $"Take arrangement saved · {replacement.Name} · source audio was not overwritten";
            RefreshReceiverTakeWorkspace(replacement.Id);
        }
        catch (Exception exception)
        {
            StatusText.Text = $"Take edit failed without replacing the saved arrangement: {exception.Message}";
        }
        finally
        {
            receiverTakeCancellation.Dispose();
            receiverTakeCancellation = null;
            SetReceiverTakeBusy(false);
        }
    }

    private void OnRevertReceiverTakeClicked(object? sender, RoutedEventArgs e)
    {
        if (activeReceiverTake is not null)
        {
            SelectReceiverTake(activeReceiverTake);
            StatusText.Text = "Take fields restored to the last saved arrangement";
        }
    }

    private async void OnHearReceiverTakeClicked(object? sender, RoutedEventArgs e) =>
        await AuditionReceiverTakeAsync(ReceiverAuditionMode.Stereo, "Stereo take", ReceiverTakeHearButton);

    private async void OnHearReceiverTakeLeftClicked(object? sender, RoutedEventArgs e) =>
        await AuditionReceiverTakeAsync(ReceiverAuditionMode.LeftCentered, "Isolated left channel", ReceiverTakeHearLeftButton);

    private async void OnHearReceiverTakeRightClicked(object? sender, RoutedEventArgs e) =>
        await AuditionReceiverTakeAsync(ReceiverAuditionMode.RightCentered, "Isolated right channel", ReceiverTakeHearRightButton);

    private async Task AuditionReceiverTakeAsync(ReceiverAuditionMode mode, string label, Button activeButton)
    {
        if (receiverTakeAuditionPlaying)
        {
            StopPreviewTransport();
            StatusText.Text = "Receiver take audition stopped · project was not changed";
            return;
        }
        if (activeReceiverTake is null)
        {
            StatusText.Text = "Choose a receiver take to hear first";
            return;
        }
        StopPreviewTransport();
        SetReceiverTakeBusy(true);
        receiverTakeCancellation = new CancellationTokenSource();
        try
        {
            StatusText.Text = $"Preparing {label.ToLowerInvariant()} from verified channel assets…";
            var rendered = await ReceiverTakeMixer.RenderTakeAsync(activeReceiverTake, mode, cancellationToken: receiverTakeCancellation.Token);
            StartReceiverTakeAudition(rendered, label, activeButton);
        }
        catch (Exception exception)
        {
            StopPreviewTransport();
            StatusText.Text = $"Receiver take audition failed: {exception.Message}";
        }
        finally
        {
            receiverTakeCancellation.Dispose();
            receiverTakeCancellation = null;
            SetReceiverTakeBusy(false);
        }
    }

    private async void OnHearReceiverMasterClicked(object? sender, RoutedEventArgs e)
    {
        if (receiverTakeAuditionPlaying)
        {
            StopPreviewTransport();
            StatusText.Text = "Receiver master audition stopped · project was not changed";
            return;
        }
        StopPreviewTransport();
        SetReceiverTakeBusy(true);
        receiverTakeCancellation = new CancellationTokenSource();
        try
        {
            StatusText.Text = "Building the arranged stereo master from verified take channels…";
            var rendered = await ReceiverTakeMixer.BuildMasterAsync(receiverTakeSessions, receiverTakeCancellation.Token);
            StartReceiverTakeAudition(rendered, "Arranged stereo master", ReceiverMasterHearButton);
        }
        catch (Exception exception)
        {
            StopPreviewTransport();
            StatusText.Text = $"Receiver master audition failed: {exception.Message}";
        }
        finally
        {
            receiverTakeCancellation.Dispose();
            receiverTakeCancellation = null;
            SetReceiverTakeBusy(false);
        }
    }

    private void StartReceiverTakeAudition(ReceiverMixResult rendered, string label, Button activeButton)
    {
        var profile = GetActiveRenderProfile();
        audioPreview = NativeSceneRenderer.StartBufferPreview(
            rendered.Buffer.InterleavedSamples,
            (uint)rendered.Buffer.SampleRate,
            (uint)profile.BufferFrames);
        previewStarted = DateTimeOffset.UtcNow;
        previewDurationSeconds = rendered.Buffer.DurationSeconds;
        previewAdvancesScene = false;
        previewCompletionStatus = $"{label} audition complete · project was not changed";
        previewInterruptedStatus = $"{label} audition stopped · the WASAPI device was removed or rerouted";
        receiverTakeAuditionPlaying = true;
        previewTimer.Start();
        PlayButton.Content = "▶  Auditioning";
        activeButton.Content = "■ Playing";
        var overload = rendered.Peak > 1 ? " · peak exceeds 0 dBFS; no normalization applied" : string.Empty;
        StatusText.Text = $"{label} · peak {rendered.Peak:0.000} · {rendered.Buffer.DurationSeconds:0.000} s · in-process WASAPI{overload}";
    }

    private async void OnExportReceiverChannelsClicked(object? sender, RoutedEventArgs e)
    {
        if (activeReceiverTake is null)
        {
            StatusText.Text = "Choose a receiver take before exporting its channels";
            return;
        }
        SetReceiverTakeBusy(true);
        receiverTakeCancellation = new CancellationTokenSource();
        try
        {
            var outputDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "PulseGen",
                "ReceiverExports");
            var stem = $"{SafeName(activeReceiverTake.Take.Name)}-{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}";
            var leftPath = Path.Combine(outputDirectory, $"{stem}-L.wav");
            var rightPath = Path.Combine(outputDirectory, $"{stem}-R.wav");
            var left = await ReceiverTakeMixer.RenderChannelAsync(activeReceiverTake, leftChannel: true, includeTimelineOffset: true, cancellationToken: receiverTakeCancellation.Token);
            var right = await ReceiverTakeMixer.RenderChannelAsync(activeReceiverTake, leftChannel: false, includeTimelineOffset: true, cancellationToken: receiverTakeCancellation.Token);
            await Task.Run(() => FloatWaveFile.WriteAtomic(leftPath, left), receiverTakeCancellation.Token);
            await Task.Run(() => FloatWaveFile.WriteAtomic(rightPath, right), receiverTakeCancellation.Token);
            StatusText.Text = $"Isolated mono L/R files exported with saved gain, trim, and timeline start · {outputDirectory}";
        }
        catch (Exception exception)
        {
            StatusText.Text = $"Channel export failed: {exception.Message}";
        }
        finally
        {
            receiverTakeCancellation.Dispose();
            receiverTakeCancellation = null;
            SetReceiverTakeBusy(false);
        }
    }

    private void OnStudioExportSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!initializingControls && !refreshingStudioExportControls)
        {
            RefreshStudioExportChoices(refreshEncodingSource: ReferenceEquals(sender, StudioContainerBox));
        }
    }

    private void RefreshStudioExportChoices(bool refreshEncodingSource)
    {
        if (StudioContainerBox.SelectedItem is not StudioContainerChoice container)
        {
            return;
        }
        refreshingStudioExportControls = true;
        try
        {
            if (refreshEncodingSource)
            {
                var previousEncoding = (StudioEncodingBox.SelectedItem as StudioEncodingChoice)?.Encoding ?? StudioSampleEncoding.Pcm24;
                var compatible = StudioExportChoices.EncodingsFor(container.Container);
                StudioEncodingBox.ItemsSource = compatible;
                StudioEncodingBox.SelectedItem = compatible.FirstOrDefault(choice => choice.Encoding == previousEncoding)
                    ?? compatible.FirstOrDefault(choice => choice.Encoding == StudioSampleEncoding.Pcm24)
                    ?? compatible[0];
            }

            var encoding = (StudioEncodingBox.SelectedItem as StudioEncodingChoice)?.Encoding ?? StudioSampleEncoding.Pcm24;
            if (encoding == StudioSampleEncoding.Float32 || StudioExportChoices.IsLossy(container.Container))
            {
                StudioDitherBox.SelectedItem = StudioExportChoices.Dithers.First(choice => choice.Dither == DitherMode.None);
                StudioDitherBox.IsEnabled = false;
            }
            else
            {
                StudioDitherBox.IsEnabled = receiverTakeCancellation is null;
                StudioDitherBox.SelectedItem ??= StudioExportChoices.Dithers.First(choice => choice.Dither == DitherMode.Tpdf);
            }
            StudioExportDescriptionText.Text = container.Description;
        }
        finally
        {
            refreshingStudioExportControls = false;
        }
    }

    private async void OnRenderReceiverMasterClicked(object? sender, RoutedEventArgs e)
    {
        if (lastProjectDirectory is null)
        {
            StatusText.Text = "Save the project before building a recorded stereo master";
            return;
        }
        if (sceneDirty)
        {
            StatusText.Text = "Save the current scene and take arrangement before exporting a studio master";
            return;
        }
        if (StudioContainerBox.SelectedItem is not StudioContainerChoice container ||
            StudioEncodingBox.SelectedItem is not StudioEncodingChoice encoding ||
            StudioDitherBox.SelectedItem is not StudioDitherChoice dither)
        {
            StatusText.Text = "Choose a studio file type, encoding, and dither setting first";
            return;
        }

        var extension = StudioMasterExporter.Extension(container.Container);
        var fileType = new FilePickerFileType(container.Name)
        {
            Patterns = [$"*{extension}"],
            MimeTypes = [StudioMasterExporter.MediaType(container.Container)]
        };
        var destination = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export a PulseGen stereo studio master",
            SuggestedFileName = $"{SafeName(scene.Name)}-master-{DateTime.UtcNow:yyyyMMdd-HHmmss}{extension}",
            DefaultExtension = extension[1..],
            FileTypeChoices = [fileType],
            SuggestedFileType = fileType,
            ShowOverwritePrompt = true
        });
        if (destination is null)
        {
            return;
        }

        StopPreviewTransport();
        SetReceiverTakeBusy(true);
        receiverTakeCancellation = new CancellationTokenSource();
        try
        {
            StatusText.Text = "Building and measuring the arranged stereo master from verified take channels…";
            var rendered = await ReceiverTakeMixer.BuildMasterAsync(receiverTakeSessions, receiverTakeCancellation.Token);
            var revision = ProjectStore.ComputeSceneRevisionSha256(scene);
            var request = new StudioExportRequest
            {
                DestinationPath = destination.Path.LocalPath,
                Container = container.Container,
                Encoding = encoding.Encoding,
                Dither = dither.Dither,
                DitherSeed = ComputeStudioDitherSeed(revision, rendered.TakeIds, encoding.Encoding),
                ProjectId = scene.ProjectId,
                ProjectName = scene.Name,
                SceneRevisionSha256 = revision,
                TakeIds = rendered.TakeIds,
                CreatedAtUtc = DateTimeOffset.UtcNow
            };
            var exported = await Task.Run(
                () => StudioMasterExporter.Export(request, rendered.Buffer, receiverTakeCancellation.Token),
                receiverTakeCancellation.Token);
            var imported = await StudioExportProjectImporter.ImportAsync(
                scene,
                lastProjectDirectory,
                request,
                exported,
                receiverTakeCancellation.Token);
            scene = imported.Scene;
            Viewport.Scene = scene;
            GpuViewport.Scene = scene;
            sceneDirty = false;
            ReceiverMasterMetricsText.Text = StudioMetricsSummary(exported.Metrics);
            var overload = exported.Metrics.TruePeakDbTp is > 0
                ? " · WARNING: true peak exceeds 0 dBTP; no normalization was applied"
                : " · normalization none";
            var rateConversion = exported.SampleRateConversion is null ? string.Empty : $" · {exported.SampleRateConversion}";
            StatusText.Text = $"{container.Name} master exported from {rendered.TakeIds.Count} takes · {StudioMetricsSummary(exported.Metrics)}{overload}{rateConversion} · {exported.OutputPath}";
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Studio master export cancelled · saved project was not changed";
        }
        catch (Exception exception)
        {
            StatusText.Text = $"Studio master export failed without changing the saved project: {exception.Message}";
        }
        finally
        {
            receiverTakeCancellation.Dispose();
            receiverTakeCancellation = null;
            SetReceiverTakeBusy(false);
        }
    }

    private static ulong ComputeStudioDitherSeed(
        string sceneRevisionSha256,
        IReadOnlyList<Guid> takeIds,
        StudioSampleEncoding encoding)
    {
        var identity = $"{sceneRevisionSha256}|{encoding}|{string.Join(',', takeIds.Select(id => id.ToString("D")))}";
        return BinaryPrimitives.ReadUInt64LittleEndian(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
    }

    private static string StudioMetricsSummary(StudioAudioMetrics metrics)
    {
        var loudness = metrics.IntegratedLoudnessLufs is { } lufs ? $"{lufs:0.0} LUFS" : "LUFS unavailable (short/silent)";
        var truePeak = metrics.TruePeakDbTp is { } dbTp ? $"{dbTp:0.00} dBTP" : "true peak −∞ dBTP";
        var range = metrics.LoudnessRangeLu is { } lra ? $" · LRA {lra:0.0} LU" : string.Empty;
        return $"{loudness} · {truePeak}{range}";
    }

    private void SetReceiverTakeBusy(bool busy)
    {
        SetReceiverTakeActionsEnabled(!busy && activeReceiverTake is not null);
        ReceiverTakeList.IsEnabled = !busy && receiverTakeSessions.Count > 0;
        ReceiverMasterHearButton.IsEnabled = !busy && receiverTakeSessions.Any(session => session.Take.IncludedInMaster);
        ReceiverMasterRenderButton.IsEnabled = !busy && receiverTakeSessions.Any(session => session.Take.IncludedInMaster);
        StudioContainerBox.IsEnabled = !busy;
        StudioEncodingBox.IsEnabled = !busy;
        var studioContainer = (StudioContainerBox.SelectedItem as StudioContainerChoice)?.Container ?? StudioExportContainer.BroadcastWave;
        StudioDitherBox.IsEnabled = !busy &&
            (StudioEncodingBox.SelectedItem as StudioEncodingChoice)?.Encoding != StudioSampleEncoding.Float32 &&
            !StudioExportChoices.IsLossy(studioContainer);
    }

    private void SetReceiverTakeActionsEnabled(bool enabled)
    {
        ReceiverTakeIncluded.IsEnabled = enabled;
        ReceiverTakeLinked.IsEnabled = enabled;
        ReceiverTakeLeftMuted.IsEnabled = enabled;
        ReceiverTakeLeftGain.IsEnabled = enabled;
        var rightEnabled = enabled && ReceiverTakeLinked.IsChecked != true;
        ReceiverTakeRightMuted.IsEnabled = rightEnabled;
        ReceiverTakeRightGain.IsEnabled = rightEnabled;
        ReceiverTakeOffset.IsEnabled = enabled;
        ReceiverTakeTrimStart.IsEnabled = enabled;
        ReceiverTakeTrimEnd.IsEnabled = enabled;
        ReceiverTakeApplyButton.IsEnabled = enabled;
        ReceiverTakeRevertButton.IsEnabled = enabled;
        ReceiverTakeHearButton.IsEnabled = enabled;
        ReceiverTakeHearLeftButton.IsEnabled = enabled;
        ReceiverTakeHearRightButton.IsEnabled = enabled;
        ReceiverTakeExportChannelsButton.IsEnabled = enabled;
        ReceiverMasterHearButton.IsEnabled = enabled && receiverTakeSessions.Any(session => session.Take.IncludedInMaster);
        ReceiverMasterRenderButton.IsEnabled = enabled && receiverTakeSessions.Any(session => session.Take.IncludedInMaster);
    }

    private void OnPreviewTick(object? sender, EventArgs e)
    {
        GpuViewport.ContinuousRendering = true;
        var elapsed = (DateTimeOffset.UtcNow - previewStarted).TotalSeconds;
        if (elapsed >= previewDurationSeconds)
        {
            var advancesScene = previewAdvancesScene;
            var completionStatus = previewCompletionStatus;
            StopPreviewTransport();
            if (advancesScene)
            {
                UpdatePlayhead(scene.Timeline.DurationSeconds);
            }
            StatusText.Text = completionStatus;
            return;
        }
        if (!MaintainAudioPreview(DateTimeOffset.UtcNow, elapsed))
        {
            return;
        }

        if (previewAdvancesScene)
        {
            UpdatePlayhead(elapsed);
        }
    }

    private bool MaintainAudioPreview(DateTimeOffset now, double timelineSeconds)
    {
        if (audioPreview is null)
        {
            return true;
        }

        try
        {
            var currentStatus = audioPreview.Status;
            if (currentStatus.Running)
            {
                UpdateAudioPerformanceBadge(currentStatus, now);
                if (audioDeviceRecovery.IsActive)
                {
                    audioDeviceRecovery.Cancel();
                    StatusText.Text = $"Audio restored · Windows resumed the output device · {currentStatus.SampleRate:N0} Hz";
                }
                return true;
            }
        }
        catch (Exception exception)
        {
            System.Diagnostics.Trace.TraceWarning($"PulseGen could not read preview device status before recovery: {exception.Message}");
        }

        if (audioDeviceRecovery.Begin(now))
        {
            SetEngineState("AUDIO RECONNECTING", Avalonia.Media.Brushes.Goldenrod,
                "Windows stopped or rerouted the output device. PulseGen retained the preview and is reopening the current default endpoint.");
            StatusText.Text = $"Audio output changed · reconnecting to the current Windows device (attempt 1/{audioDeviceRecovery.MaximumAttempts})…";
        }
        if (!audioDeviceRecovery.IsAttemptDue(now))
        {
            return true;
        }

        var attempt = audioDeviceRecovery.NextAttemptNumber;
        try
        {
            var recovered = audioPreview.RecoverAt(timelineSeconds);
            if (!recovered.Running)
            {
                throw new InvalidOperationException("Windows reopened the endpoint, but playback did not enter the running state.");
            }
            var result = audioDeviceRecovery.CompleteAttempt(recovered: true, DateTimeOffset.UtcNow);
            UpdateAudioPerformanceBadge(recovered, DateTimeOffset.UtcNow, force: true);
            StatusText.Text =
                $"Audio restored · reconnected on attempt {result.AttemptsCompleted}/{result.MaximumAttempts} · resumed at {timelineSeconds:0.000} s · {recovered.SampleRate:N0} Hz";
            return true;
        }
        catch (Exception exception)
        {
            var completedAt = DateTimeOffset.UtcNow;
            var result = audioDeviceRecovery.CompleteAttempt(recovered: false, completedAt);
            if (result.Outcome == AudioDeviceRecoveryOutcome.Waiting)
            {
                var delay = result.NextAttemptAtUtc!.Value - completedAt;
                SetEngineState("AUDIO RETRYING", Avalonia.Media.Brushes.Goldenrod,
                    $"Reconnect attempt {attempt} failed. The retained preview remains local while PulseGen waits {delay.TotalSeconds:0.##} seconds before trying again.");
                StatusText.Text =
                    $"Audio device unavailable · reconnect attempt {attempt}/{result.MaximumAttempts} failed · next try in {delay.TotalSeconds:0.##} s: {exception.Message}";
                return true;
            }

            var interruptionStatus = previewInterruptedStatus;
            var attempts = result.AttemptsCompleted;
            audioPreview.Dispose();
            audioPreview = null;
            audioDeviceRecovery.Cancel();
            if (!previewAdvancesScene)
            {
                StopPreviewTransport();
                SetEngineState("AUDIO OFFLINE", Avalonia.Media.Brushes.IndianRed,
                    $"The current preview could not reopen a Windows output device after {attempts} attempts.");
                StatusText.Text = $"{interruptionStatus} · automatic reconnect failed after {attempts} attempts: {exception.Message}";
                return false;
            }
            SetEngineState("AUDIO OFFLINE", Avalonia.Media.Brushes.IndianRed,
                $"The current preview could not reopen a Windows output device after {attempts} attempts.");
            StatusText.Text =
                $"Visual-only preview · audio could not reconnect after {attempts} attempts: {exception.Message}";
            return true;
        }
    }

    private void StopPreviewTransport()
    {
        previewTimer.Stop();
        GpuViewport.ContinuousRendering = false;
        audioDeviceRecovery.Cancel();
        nextAudioPerformanceRefreshAtUtc = default;
        audioPreview?.Dispose();
        audioPreview = null;
        spectralAuditionPlaying = false;
        receiverTakeAuditionPlaying = false;
        PlayButton.Content = "▶  Preview";
        SpectralAuditionButton.Content = "▶  Hear";
        ReceiverTakeHearButton.Content = "▶ Stereo";
        ReceiverTakeHearLeftButton.Content = "▶ Left";
        ReceiverTakeHearRightButton.Content = "▶ Right";
        ReceiverMasterHearButton.Content = "▶ Master";
        SetEngineState("ENGINE READY", Avalonia.Media.Brushes.LightSeaGreen,
            "During preview this shows how much of each hardware audio block PulseGen needs to finish its physics rendering.");
    }

    private void UpdateAudioPerformanceBadge(
        PreviewDeviceStatus status,
        DateTimeOffset now,
        bool force = false)
    {
        if (!force && now < nextAudioPerformanceRefreshAtUtc)
        {
            return;
        }
        nextAudioPerformanceRefreshAtUtc = now + TimeSpan.FromMilliseconds(250);
        var summary = AudioPerformanceDiagnostics.Summarize(status);
        var brush = summary.Severity switch
        {
            AudioPerformanceSeverity.Healthy => Avalonia.Media.Brushes.LightSeaGreen,
            AudioPerformanceSeverity.Warning => Avalonia.Media.Brushes.Goldenrod,
            AudioPerformanceSeverity.Critical => Avalonia.Media.Brushes.IndianRed,
            _ => Avalonia.Media.Brushes.LightSteelBlue
        };
        SetEngineState(summary.BadgeText, brush, summary.Detail);
    }

    private void SetEngineState(string text, Avalonia.Media.IBrush foreground, string detail)
    {
        EngineStateText.Text = text;
        EngineStateText.Foreground = foreground;
        ToolTip.SetTip(EngineStateText, detail);
    }

    private void UpdateViewportPerformance(ViewportPerformanceStatus status)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => UpdateViewportPerformance(status));
            return;
        }

        if (!status.Ready)
        {
            ViewportPerformanceText.Text = status.Error is null ? "GPU STARTING" : "GPU OFFLINE";
            ViewportPerformanceText.Foreground = status.Error is null
                ? Avalonia.Media.Brushes.LightSteelBlue
                : Avalonia.Media.Brushes.IndianRed;
        }
        else if (status.TriangleCount == 0 || status.FrameCount < 2)
        {
            ViewportPerformanceText.Text = status.TriangleCount == 0
                ? "GPU READY"
                : $"GPU WARMING · {status.TriangleCount / 1000.0:0}K TRI";
            ViewportPerformanceText.Foreground = Avalonia.Media.Brushes.LightSeaGreen;
        }
        else
        {
            var imported = status.ImportedMeshCount == 0
                ? string.Empty
                : $" · {status.ImportedMeshCount} MESH";
            ViewportPerformanceText.Text = $"{status.FramesPerSecond:0} FPS · {status.TriangleCount / 1000.0:0}K TRI{imported}";
            ViewportPerformanceText.Foreground = status.FramesPerSecond >= 57
                ? Avalonia.Media.Brushes.LightSeaGreen
                : status.FramesPerSecond >= 50
                    ? Avalonia.Media.Brushes.Goldenrod
                    : Avalonia.Media.Brushes.IndianRed;
        }

        ToolTip.SetTip(
            ViewportPerformanceText,
            status.Error ??
            $"{status.Backend}\nGPU scene: {status.TriangleCount:N0} triangles. " +
            $"Imported surfaces: {status.ImportedMeshCount:N0} meshes / {status.ImportedTriangleCount:N0} triangles. " +
            $"CPU submission averages {status.CpuSubmitMillisecondsAverage:0.000} ms and peaks at {status.CpuSubmitMillisecondsMaximum:0.000} ms. " +
            $"Longest observed frame interval: {status.FrameIntervalMillisecondsMaximum:0.000} ms.");
    }

    private async void OnEditorOpened(object? sender, EventArgs e)
    {
        Opened -= OnEditorOpened;
        IsEnabled = false;
        AutosaveStateText.Text = "CHECKING RECOVERY…";
        StatusText.Text = "Checking protected local autosave history…";
        try
        {
            var recovered = await recoveryService.InitializeAsync(scene, lastProjectDirectory, recoveryCancellation.Token);
            recoveryReady = true;
            if (recovered.Recovered)
            {
                initializingControls = true;
                ApplySceneToControls(recovered.Scene, recovered.ProjectDirectory);
                sceneDirty = true;
                sceneEditedAtUtc = null;
                recoveredAutosaveActive = true;
                discardRecoveryArmed = false;
                DiscardRecoveryButton.Content = "Discard recovery";
                DiscardRecoveryButton.IsVisible = true;
                AutosaveStateText.Text = recovered.AutosavedAtUtc is { } created
                    ? $"RECOVERED · {created.ToLocalTime():t}"
                    : "RECOVERED AUTOSAVE";
                AutosaveStateText.Foreground = Avalonia.Media.Brushes.Goldenrod;
                Title = $"PulseGen — {scene.Name} · Recovered";
                SceneNameText.Text = $"{scene.Name} · scene v{scene.SchemaVersion} · RECOVERED";
                var reason = recovered.Kind == EditorRecoveryKind.UnexpectedExit
                    ? "PulseGen did not close cleanly"
                    : "the previous window closed with unsaved work";
                var missing = recovered.ProjectDirectoryMissing
                    ? " The original project folder is unavailable, so linked assets may need to be restored before saving."
                    : string.Empty;
                StatusText.Text = $"Recovered the latest valid autosave because {reason}. It is in memory only until you choose Save.{missing}";
            }
            else
            {
                AutosaveStateText.Text = "AUTOSAVE READY";
                AutosaveStateText.Foreground = Avalonia.Media.Brushes.LightSeaGreen;
                StatusText.Text = "Ready · protected local autosave is active";
            }
            autosaveTimer.Start();
        }
        catch (OperationCanceledException) when (recoveryCancellation.IsCancellationRequested)
        {
            // Window shutdown cancelled startup recovery.
        }
        catch (Exception exception)
        {
            recoveryReady = false;
            AutosaveStateText.Text = "AUTOSAVE UNAVAILABLE";
            AutosaveStateText.Foreground = Avalonia.Media.Brushes.IndianRed;
            StatusText.Text = $"Editor ready, but protected autosave could not start: {exception.Message}";
        }
        finally
        {
            initializingControls = false;
            IsEnabled = true;
        }
    }

    private async void OnAutosaveTick(object? sender, EventArgs e)
    {
        if (!recoveryReady || autosaveInProgress || !sceneDirty || sceneEditedAtUtc is not { } editedAt ||
            DateTimeOffset.UtcNow - editedAt < AutosaveDelay)
        {
            return;
        }

        autosaveInProgress = true;
        AutosaveStateText.Text = "AUTOSAVING…";
        AutosaveStateText.Foreground = Avalonia.Media.Brushes.LightSteelBlue;
        try
        {
            var result = await recoveryService.AutosaveAsync(scene, lastProjectDirectory, cancellationToken: recoveryCancellation.Token);
            var currentRevision = ProjectStore.ComputeSceneRevisionSha256(scene);
            if (string.Equals(currentRevision, result.SceneRevisionSha256, StringComparison.Ordinal))
            {
                sceneEditedAtUtc = null;
                AutosaveStateText.Text = result.CreatedAtUtc is { } created
                    ? $"AUTOSAVED · {created.ToLocalTime():t}"
                    : "AUTOSAVE CURRENT";
                AutosaveStateText.Foreground = Avalonia.Media.Brushes.LightSeaGreen;
            }
            else
            {
                sceneEditedAtUtc = DateTimeOffset.UtcNow;
                AutosaveStateText.Text = "UNSAVED · AUTOSAVE PENDING";
                AutosaveStateText.Foreground = Avalonia.Media.Brushes.Goldenrod;
            }
        }
        catch (OperationCanceledException) when (recoveryCancellation.IsCancellationRequested)
        {
        }
        catch
        {
            sceneEditedAtUtc = DateTimeOffset.UtcNow;
            AutosaveStateText.Text = "AUTOSAVE RETRY PENDING";
            AutosaveStateText.Foreground = Avalonia.Media.Brushes.IndianRed;
        }
        finally
        {
            autosaveInProgress = false;
        }
    }

    private async void OnDiscardRecoveryClicked(object? sender, RoutedEventArgs e)
    {
        if (!recoveredAutosaveActive || !recoveryReady)
        {
            return;
        }
        if (!discardRecoveryArmed)
        {
            discardRecoveryArmed = true;
            DiscardRecoveryButton.Content = "Confirm discard";
            StatusText.Text = "Discarding will reload the last explicit save. The recovered snapshot remains in local history; click Confirm discard to continue.";
            return;
        }

        try
        {
            SceneDocument replacement;
            string? replacementDirectory;
            if (lastProjectDirectory is { } projectDirectory && File.Exists(Path.Combine(projectDirectory, "project.json")))
            {
                replacement = await ProjectStore.LoadAsync(projectDirectory, recoveryCancellation.Token);
                replacementDirectory = projectDirectory;
            }
            else
            {
                replacement = SampleScenes.CreateFlyby();
                replacementDirectory = null;
            }
            await recoveryService.ResetContextAsync(replacement, replacementDirectory, recoveryCancellation.Token);
            initializingControls = true;
            ApplySceneToControls(replacement, replacementDirectory);
            ClearRecoveredState();
            AutosaveStateText.Text = "AUTOSAVE READY";
            AutosaveStateText.Foreground = Avalonia.Media.Brushes.LightSeaGreen;
            StatusText.Text = replacementDirectory is null
                ? "Recovered work discarded · opened a fresh unsaved scene"
                : $"Recovered work discarded · reloaded the last explicit save · {replacementDirectory}";
        }
        catch (Exception exception)
        {
            discardRecoveryArmed = false;
            DiscardRecoveryButton.Content = "Discard recovery";
            StatusText.Text = $"Recovery was preserved because the saved project could not be reloaded safely: {exception.Message}";
        }
        finally
        {
            initializingControls = false;
        }
    }

    private async Task ResetRecoveryContextAsync(SceneDocument replacement, string? projectDirectory)
    {
        if (!recoveryReady)
        {
            return;
        }
        await recoveryService.ResetContextAsync(replacement, projectDirectory, recoveryCancellation.Token);
        AutosaveStateText.Text = "AUTOSAVE READY";
        AutosaveStateText.Foreground = Avalonia.Media.Brushes.LightSeaGreen;
    }

    private async Task MarkRecoverySavedAsync(SceneDocument savedScene, string projectDirectory)
    {
        if (!recoveryReady)
        {
            return;
        }
        await recoveryService.MarkSavedAsync(savedScene, projectDirectory, recoveryCancellation.Token);
        AutosaveStateText.Text = "SAVED · AUTOSAVE READY";
        AutosaveStateText.Foreground = Avalonia.Media.Brushes.LightSeaGreen;
    }

    private void ClearRecoveredState()
    {
        var wasRecovered = recoveredAutosaveActive;
        recoveredAutosaveActive = false;
        discardRecoveryArmed = false;
        DiscardRecoveryButton.Content = "Discard recovery";
        DiscardRecoveryButton.IsVisible = false;
        if (wasRecovered)
        {
            Title = $"PulseGen — {scene.Name}";
            SceneNameText.Text = $"{scene.Name} · scene v{scene.SchemaVersion}";
        }
    }

    private void DisableRecovery(Exception exception)
    {
        recoveryReady = false;
        autosaveTimer.Stop();
        AutosaveStateText.Text = "AUTOSAVE UNAVAILABLE";
        AutosaveStateText.Foreground = Avalonia.Media.Brushes.IndianRed;
        System.Diagnostics.Trace.TraceError($"PulseGen autosave disabled: {exception}");
    }

    protected override async void OnClosing(WindowClosingEventArgs e)
    {
        if (closeFinalized || !recoveryReady)
        {
            if (!recoveryReady)
            {
                recoveryCancellation.Cancel();
            }
            base.OnClosing(e);
            return;
        }

        base.OnClosing(e);
        if (e.Cancel)
        {
            return;
        }
        if (closingInProgress)
        {
            e.Cancel = true;
            return;
        }
        e.Cancel = true;
        closingInProgress = true;
        autosaveTimer.Stop();
        AutosaveStateText.Text = sceneDirty ? "RETAINING UNSAVED WORK…" : "CLOSING CLEANLY…";
        try
        {
            await recoveryService.CompleteAsync(scene, lastProjectDirectory, sceneDirty, CancellationToken.None);
        }
        catch
        {
            // Leave the session active. A prior snapshot can still be recovered after process exit.
        }
        closeFinalized = true;
        Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        autosaveTimer.Stop();
        reflectionDiagnosticTimer.Stop();
        reflectionDiagnosticVersion++;
        reflectionMonitorConfigurationVersion++;
        var retainedReflectionMonitor = reflectionMonitor;
        reflectionMonitor = null;
        reflectionMonitorSessionVersion = -1;
        retainedReflectionMonitor?.Dispose();
        recoveryCancellation.Cancel();
        StopPreviewTransport();
        analysisCancellation?.Cancel();
        spectralCancellation?.Cancel();
        receiverTakeCancellation?.Cancel();
        syncCancellation?.Cancel();
        atlasIdentity?.Dispose();
        atlasHttp.Dispose();
        workspaceIndex.Dispose();
        recoveryCancellation.Dispose();
        base.OnClosed(e);
    }

    private void UpdatePlayhead(double timeSeconds)
    {
        var duration = scene.Timeline.DurationSeconds;
        var time = Math.Clamp(timeSeconds, 0, duration);
        Viewport.TimeSeconds = time;
        var path = SceneSelectionResolver.MotionPath(scene, currentSelection);
        var emitter = SceneSelectionResolver.Emitter(scene, currentSelection);
        var receiver = currentSelection.Kind == SceneSelectionKind.Receiver
            ? scene.ReceiverRigs.FirstOrDefault(item => item.Id == currentSelection.Id)
            : null;
        var position = path is not null
            ? SceneKinematics.PositionAt(path, time)
            : emitter?.Transform.PositionMetres ?? receiver?.Transform.PositionMetres ?? Vector3d.Zero;
        var velocity = path is not null
            ? SceneKinematics.VelocityAt(path, time)
            : Vector3d.Zero;
        PositionText.Text = $"t  {time:0.000} s";
        PositionX.Text = $"{position.X,8:0.000} m";
        PositionY.Text = $"{position.Y,8:0.000} m";
        PositionZ.Text = $"{position.Z,8:0.000} m";
        VelocityText.Text = $"{velocity.Length:0.00} m/s";
        var timelineWidth = (Playhead.Parent as Control)?.Bounds.Width ?? 0;
        Canvas.SetLeft(Playhead, Math.Max(0, timelineWidth * (time / duration)));
        ScheduleReflectionDiagnostics();
    }

    private void ScheduleReflectionDiagnostics()
    {
        reflectionDiagnosticVersion++;
        var receiver = currentSelection.Kind == SceneSelectionKind.Receiver
            ? scene.ReceiverRigs.FirstOrDefault(item => item.Id == currentSelection.Id && item.Enabled)
            : scene.ReceiverRigs.FirstOrDefault(item => item.Enabled);
        var profile = scene.RenderProfiles.FirstOrDefault(item => item.Id == activeRenderProfileId)
            ?? scene.RenderProfiles.FirstOrDefault();
        if (lastProjectDirectory is null || receiver is null || profile is null || profile.ReflectionOrder == 0 ||
            !scene.Geometry.Any(item => item.Kind == GeometryKind.ImportedMesh && item.Reflects))
        {
            reflectionDiagnosticPending = false;
            reflectionDiagnosticTimer.Stop();
            Viewport.ReflectionPaths = [];
            ReflectionPathText.Text = "ECHO ROUTES —";
            return;
        }
        reflectionDiagnosticPending = true;
        if (!reflectionDiagnosticInFlight && !reflectionDiagnosticTimer.IsEnabled)
        {
            reflectionDiagnosticTimer.Start();
        }
    }

    private void InvalidateReflectionMonitorConfiguration()
    {
        reflectionMonitorConfigurationVersion++;
        if (reflectionDiagnosticInFlight || reflectionMonitor is not { } retained)
        {
            return;
        }

        reflectionMonitor = null;
        reflectionMonitorSessionVersion = -1;
        _ = Task.Run(retained.Dispose);
    }

    private async void OnReflectionDiagnosticTick(object? sender, EventArgs e)
    {
        reflectionDiagnosticTimer.Stop();
        if (reflectionDiagnosticInFlight || !reflectionDiagnosticPending || lastProjectDirectory is null)
        {
            return;
        }
        reflectionDiagnosticPending = false;
        reflectionDiagnosticInFlight = true;
        var version = reflectionDiagnosticVersion;
        var configurationVersion = reflectionMonitorConfigurationVersion;
        try
        {
            var receiver = currentSelection.Kind == SceneSelectionKind.Receiver
                ? scene.ReceiverRigs.FirstOrDefault(item => item.Id == currentSelection.Id && item.Enabled)
                : scene.ReceiverRigs.FirstOrDefault(item => item.Enabled);
            var profile = scene.RenderProfiles.FirstOrDefault(item => item.Id == activeRenderProfileId)
                ?? scene.RenderProfiles.First();
            if (receiver is null)
            {
                return;
            }
            var time = Viewport.TimeSeconds;
            var projectDirectory = lastProjectDirectory;
            var retainedMonitor = reflectionMonitor;
            var retainedMonitorVersion = reflectionMonitorSessionVersion;
            reflectionMonitor = null;
            reflectionMonitorSessionVersion = -1;
            var requiresConfiguration = retainedMonitor is null ||
                retainedMonitorVersion != configurationVersion ||
                retainedMonitor.AtmosphereIntegrationSegments != profile.AtmosphereIntegrationSegments;
            var snapshot = requiresConfiguration
                ? ProjectStore.DeserializeManifest(ProjectStore.SerializeManifest(scene))
                : null;
            var result = await Task.Run(() =>
            {
                var activeMonitor = retainedMonitor;
                try
                {
                    if (requiresConfiguration)
                    {
                        activeMonitor?.Dispose();
                        activeMonitor = NativeSceneRenderer.CreateReflectionMonitor(
                            snapshot!,
                            projectDirectory,
                            profile.AtmosphereIntegrationSegments);
                    }
                    var routes = activeMonitor!.Query(receiver.Id, time, profile);
                    return (Monitor: activeMonitor, Routes: routes);
                }
                catch
                {
                    activeMonitor?.Dispose();
                    throw;
                }
            });

            if (!closingInProgress && configurationVersion == reflectionMonitorConfigurationVersion)
            {
                reflectionMonitor = result.Monitor;
                reflectionMonitorSessionVersion = configurationVersion;
            }
            else
            {
                await Task.Run(result.Monitor.Dispose);
            }
            if (version != reflectionDiagnosticVersion)
            {
                return;
            }
            var routes = result.Routes;
            Viewport.ReflectionPaths = routes;
            var firstOrder = routes.Count(item => item.ReflectionOrder == 1);
            var secondOrder = routes.Count - firstOrder;
            ReflectionPathText.Text = routes.Count == 0
                ? "ECHO ROUTES 0"
                : $"ECHO ROUTES {routes.Count} · {firstOrder}×1 + {secondOrder}×2";
            ReflectionPathText.Foreground = routes.Count == 0
                ? new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#718198"))
                : new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#74CFC8"));
            ToolTip.SetTip(
                ReflectionPathText,
                $"Retained acoustic scene · live query {result.Monitor.CompletedQueryCount:N0} · no mesh reread between playhead updates.");
        }
        catch (Exception exception)
        {
            if (version == reflectionDiagnosticVersion)
            {
                Viewport.ReflectionPaths = [];
                ReflectionPathText.Text = "ECHO ROUTES unavailable";
                ToolTip.SetTip(ReflectionPathText, exception.Message);
            }
        }
        finally
        {
            reflectionDiagnosticInFlight = false;
            if (reflectionDiagnosticPending && !closingInProgress)
            {
                reflectionDiagnosticTimer.Start();
            }
        }
    }

    private void SelectKeyframe(int index)
    {
        var path = SceneSelectionResolver.MotionPath(scene, currentSelection);
        if (path is null || path.Keyframes.Count == 0)
        {
            var emitter = SceneSelectionResolver.Emitter(scene, currentSelection);
            var position = emitter?.Transform.PositionMetres ?? Vector3d.Zero;
            SelectedPointText.Text = "STATIC SOURCE · no motion path";
            KeyframeX.Text = position.X.ToString("0.000", CultureInfo.InvariantCulture);
            KeyframeY.Text = position.Y.ToString("0.000", CultureInfo.InvariantCulture);
            KeyframeZ.Text = position.Z.ToString("0.000", CultureInfo.InvariantCulture);
            KeyframeX.IsEnabled = false;
            KeyframeY.IsEnabled = false;
            KeyframeZ.IsEnabled = false;
            return;
        }
        KeyframeX.IsEnabled = true;
        KeyframeY.IsEnabled = true;
        KeyframeZ.IsEnabled = true;
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
