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
using System.Globalization;
using System.Text.Json;

namespace PulseGen.Editor;

public sealed partial class MainWindow : Window
{
    private sealed record MaterialBandEditorRow(
        double FrequencyHz,
        TextBox Absorption,
        TextBox Scattering,
        TextBox Transmission);

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
    private SceneSelection currentSelection;
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

    public MainWindow()
    {
        InitializeComponent();
        Viewport.PlayheadChanged += (_, time) => UpdatePlayhead(time);
        Viewport.PathPointSelected += (_, index) => SelectKeyframe(index);
        Viewport.SceneObjectSelected += selection => SelectSceneObject(selection, refreshTree: true);
        Viewport.SpatialObjectMoved += selection => PopulateInspector(selection);
        Viewport.SceneEditRejected += message =>
        {
            StatusText.Text = message;
            GestureButton.Content = "●  Gesture";
            PopulateInspector(currentSelection);
        };
        Viewport.SceneEdited += (_, message) => MarkSceneEdited(message);
        previewTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Render, OnPreviewTick);
        previewTimer.Stop();
        WaveformBox.ItemsSource = new[] { "Sine", "Triangle", "Saw", "Square", "Pulse", "WhiteNoise", "PinkNoise", "BrownNoise", "Impulse", "Additive", "Turbulence" };
        MaterialPresetBox.ItemsSource = AcousticMaterialPresets.All;
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

        RefreshReceiverSummary();
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
                        new TreeViewItem { Header = "⌇  RECEIVERS", IsExpanded = true, ItemsSource = receiverItems }
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
                        GeometryRotationX.IsEnabled = !room;
                        GeometryRotationY.IsEnabled = !room;
                        GeometryRotationZ.IsEnabled = !room;
                        GeometryReflectsBox.IsEnabled = geometry.Kind != GeometryKind.ImportedMesh;
                        GeometryConstraintText.Text = room
                            ? "The current modal-room solver keeps rooms axis-aligned. Position and size remain fully editable."
                            : geometry.Kind == GeometryKind.ImportedMesh
                                ? "Dimensions describe the visible authored-bounds proxy; scale and rotation transform the verified mesh itself. Imported-mesh echoes remain disabled."
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
        FieldQualityText.Text = "1 / path leg";
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

        var usedBy = scene.Geometry.Where(item => item.MaterialId == material.Id).Select(item => item.Name).ToArray();
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
            "These averages cover every shown frequency band. Clear echo is audible now; sound sent away leaves the current mirror-like path. Roughness also softens high notes in the clear echo.";
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
        sceneDirty = true;
        lastRenderDirectory = null;
        if (currentSelection.Kind == SceneSelectionKind.Emitter)
        {
            SelectKeyframe(Viewport.SelectedKeyframeIndex);
        }
        RefreshAtmosphereSummary();
        Viewport.Scene = scene;
        Viewport.Selection = currentSelection;
        Viewport.ActiveMotionPathId = SceneSelectionResolver.MotionPath(scene, currentSelection)?.Id;
        StatusText.Text = message;
        GestureButton.Content = "●  Gesture";
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
        Viewport.Selection = currentSelection;
        StatusText.Text = $"{description} rejected · {error?.Message ?? "the scene object is no longer available"}";
    }

    private void OnAddPlaneClicked(object? sender, RoutedEventArgs e) => AddGeometry(GeometryKind.Plane);

    private void OnAddBoxClicked(object? sender, RoutedEventArgs e) => AddGeometry(GeometryKind.Box);

    private void OnAddRoomClicked(object? sender, RoutedEventArgs e) => AddGeometry(GeometryKind.ParametricRoom);

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
            Occludes = GeometryOccludesBox.IsChecked == true,
            Reflects = original.Kind == GeometryKind.ImportedMesh ? false : GeometryReflectsBox.IsChecked == true
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
                startTimeSeconds: Viewport.TimeSeconds,
                projectDirectory: lastProjectDirectory);
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
                Reflects = false
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

            currentSelection = SceneSelection.Geometry(geometry.Id);
            CommitSceneEdit($"Imported {imported.FileName}");
            RefreshSceneTree();
            PopulateInspector(currentSelection);
            StatusText.Text = $"Imported {imported.FileName} · direct occlusion active · authored bounds proxy 2 m · save to commit the manifest";
        }
        catch (Exception exception)
        {
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
            var receipt = await Task.Run(() => NativeSceneRenderer.Render(scene, output, projectDirectory: lastProjectDirectory));
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
