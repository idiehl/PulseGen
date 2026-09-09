using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using PulseGen.Contracts;
using System.Globalization;

namespace PulseGen.Editor;

public sealed partial class MainWindow
{
    private sealed record CameraViewChoice(ViewportViewPreset Preset, string Label)
    {
        public override string ToString() => Label;
    }

    private sealed record MovePlaneChoice(WorldMoveConstraint Constraint, string Label)
    {
        public override string ToString() => Label;
    }

    private bool updatingCameraView;

    private void InitializeWorkspaceInteraction()
    {
        CameraViewBox.ItemsSource = new[]
        {
            new CameraViewChoice(ViewportViewPreset.Perspective, "Perspective · 3D"),
            new CameraViewChoice(ViewportViewPreset.Top, "Top · X / Z"),
            new CameraViewChoice(ViewportViewPreset.Front, "Front · X / Y"),
            new CameraViewChoice(ViewportViewPreset.Right, "Side · Z / Y")
        };
        CameraViewBox.SelectedIndex = 0;
        MoveConstraintBox.ItemsSource = new[]
        {
            new MovePlaneChoice(WorldMoveConstraint.GroundXZ, "Ground · X / Z"),
            new MovePlaneChoice(WorldMoveConstraint.FrontXY, "Front · X / Y"),
            new MovePlaneChoice(WorldMoveConstraint.SideYZ, "Side · Y / Z"),
            new MovePlaneChoice(WorldMoveConstraint.X, "X only"),
            new MovePlaneChoice(WorldMoveConstraint.Y, "Y only · height"),
            new MovePlaneChoice(WorldMoveConstraint.Z, "Z only")
        };
        MoveConstraintBox.SelectedIndex = 0;
        Viewport.EditStarted += (_, _) => StopPreviewTransport();
        Viewport.SelectedPositionChanged += UpdateWorldPositionFields;
        SceneTimeline.SeekStarted += (_, _) => StopPreviewTransport();
        SceneTimeline.SeekRequested += (_, time) => SeekScene(time);
        viewportCamera.Changed += (_, _) =>
        {
            updatingCameraView = true;
            CameraViewBox.SelectedItem = CameraViewBox.Items.OfType<CameraViewChoice>()
                .FirstOrDefault(item => item.Preset == viewportCamera.ViewPreset);
            updatingCameraView = false;
            var pivot = viewportCamera.Target;
            CameraPivotText.Text = $"Pivot {pivot.X:0.#}, {pivot.Y:0.#}, {pivot.Z:0.#} m";
        };
    }

    private void OnWorldToolClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string value } && Enum.TryParse<WorldInteractionMode>(value, out var mode))
            SetWorldTool(mode);
    }

    private void SetWorldTool(WorldInteractionMode mode, bool disarmGesture = true)
    {
        if (disarmGesture) DisarmWorldGesture();
        Viewport.InteractionMode = mode;
        WorldSelectButton.Classes.Set("active", mode == WorldInteractionMode.Select);
        WorldMoveButton.Classes.Set("active", mode == WorldInteractionMode.Move);
        WorldOrbitButton.Classes.Set("active", mode == WorldInteractionMode.Orbit);
        WorldPanButton.Classes.Set("active", mode == WorldInteractionMode.Pan);
        WorldToolHelpText.Text = mode switch
        {
            WorldInteractionMode.Move => "Drag a point in the chosen plane or axis. Dashed pin shows the ground below; Esc cancels.",
            WorldInteractionMode.Orbit => "Drag to rotate around the fixed pivot. Focus selection chooses a new pivot; wheel zooms.",
            WorldInteractionMode.Pan => "Drag to shift the camera and pivot together. Scene positions and playback time stay unchanged.",
            _ => "Select a point without moving it. Choose Move to edit; playback has its own ruler below."
        };
        Viewport.Focus();
    }

    private void DisarmWorldGesture()
    {
        Viewport.CancelInteraction();
        Viewport.IsGestureRecording = false;
        GestureButton.Content = "● Record path";
        GestureButton.Classes.Set("active", false);
    }

    private void OnCameraViewChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (initializingControls || updatingCameraView || CameraViewBox.SelectedItem is not CameraViewChoice choice) return;
        Viewport.CancelInteraction();
        viewportCamera.SetViewPreset(choice.Preset);
        // A flat view gets its matching move plane, so a point never jumps into an edge-on plane.
        var plane = choice.Preset switch
        {
            ViewportViewPreset.Front => WorldMoveConstraint.FrontXY,
            ViewportViewPreset.Right => WorldMoveConstraint.SideYZ,
            _ => WorldMoveConstraint.GroundXZ
        };
        MoveConstraintBox.SelectedItem = MoveConstraintBox.Items.OfType<MovePlaneChoice>()
            .First(item => item.Constraint == plane);
        Viewport.Focus();
    }

    private void OnMoveConstraintChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (MoveConstraintBox.SelectedItem is MovePlaneChoice choice) Viewport.MoveConstraint = choice.Constraint;
    }

    private void OnWorldSnapChanged(object? sender, RoutedEventArgs e) => Viewport.SnapToGrid = WorldSnapCheck.IsChecked == true;

    private void OnFocusSelectionClicked(object? sender, RoutedEventArgs e)
    {
        Viewport.FocusSelection();
        Viewport.Focus();
    }

    private void OnZoomInClicked(object? sender, RoutedEventArgs e) { Viewport.CancelInteraction(); viewportCamera.Zoom(2); }
    private void OnZoomOutClicked(object? sender, RoutedEventArgs e) { Viewport.CancelInteraction(); viewportCamera.Zoom(-2); }

    private void UpdateWorldPositionFields(Vector3d? position)
    {
        GestureButton.Content = Viewport.IsGestureRecording ? "● Cancel recording" : "● Record path";
        GestureButton.Classes.Set("active", Viewport.IsGestureRecording);
        var path = SceneSelectionResolver.MotionPath(scene, currentSelection);
        WorldSelectionLabelText.Text = path is not null ? $"Point {Viewport.SelectedKeyframeIndex + 1} (m)" : "Position (m)";
        ToolTip.SetTip(WorldSelectionLabelText, path is not null && Viewport.SelectedKeyframeIndex < path.Keyframes.Count
            ? $"Path point at {path.Keyframes[Viewport.SelectedKeyframeIndex].TimeSeconds:0.000} seconds. Editing its position does not seek playback."
            : "World position in metres. Playback time is controlled separately below.");
        var available = position.HasValue;
        WorldPositionX.IsEnabled = available;
        WorldPositionY.IsEnabled = available;
        WorldPositionZ.IsEnabled = available;
        ApplyWorldPositionButton.IsEnabled = available;
        if (WorldPositionX.IsFocused || WorldPositionY.IsFocused || WorldPositionZ.IsFocused) return;
        WorldPositionX.Text = position?.X.ToString("0.###", CultureInfo.InvariantCulture) ?? "—";
        WorldPositionY.Text = position?.Y.ToString("0.###", CultureInfo.InvariantCulture) ?? "—";
        WorldPositionZ.Text = position?.Z.ToString("0.###", CultureInfo.InvariantCulture) ?? "—";
    }

    private void OnApplyWorldPositionClicked(object? sender, RoutedEventArgs e)
    {
        if (!TryFiniteNumber(WorldPositionX.Text, out var x) || !TryFiniteNumber(WorldPositionY.Text, out var y) ||
            !TryFiniteNumber(WorldPositionZ.Text, out var z))
        {
            StatusText.Text = "Enter a finite X, Y and Z position in metres. No point was moved.";
            return;
        }
        Viewport.TrySetSelectedPosition(new(x, y, z));
        UpdateWorldPositionFields(Viewport.SelectedPosition);
    }

    private void SeekScene(double seconds)
    {
        StopPreviewTransport();
        DisarmWorldGesture();
        UpdatePlayhead(seconds);
        SeekTimeBox.Text = Viewport.TimeSeconds.ToString("0.000", CultureInfo.InvariantCulture);
        StatusText.Text = $"Playback at {Viewport.TimeSeconds:0.000} s · Play continues here; scene positions are unchanged";
    }

    private void OnRewindClicked(object? sender, RoutedEventArgs e) => SeekScene(0);
    private void OnBackOneSecondClicked(object? sender, RoutedEventArgs e) => SeekScene(SceneTransportNavigation.Step(Viewport.TimeSeconds, scene.Timeline.DurationSeconds, -1));
    private void OnForwardOneSecondClicked(object? sender, RoutedEventArgs e) => SeekScene(SceneTransportNavigation.Step(Viewport.TimeSeconds, scene.Timeline.DurationSeconds, 1));
    private void OnSeekTimeClicked(object? sender, RoutedEventArgs e) => CommitSeekTime();

    private void OnSeekTimeKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        CommitSeekTime();
        e.Handled = true;
    }

    private void CommitSeekTime()
    {
        if (TryFiniteNumber(SeekTimeBox.Text, out var value)) SeekScene(value);
        else StatusText.Text = "Enter a time in seconds, such as 1.250. Playback position was not changed.";
    }

    private static bool TryFiniteNumber(string? text, out double value) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && double.IsFinite(value);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled || e.KeyModifiers != KeyModifiers.None) return;
        if (e.Source is Control source && source.GetVisualAncestors().Prepend(source)
            .Any(control => control is TextBox or ComboBox or Button or NumericUpDown)) return;
        if (e.Key == Key.Space)
        {
            OnPlayClicked(this, new RoutedEventArgs());
            e.Handled = true;
        }
        else if (e.Key == Key.Home)
        {
            SeekScene(0);
            e.Handled = true;
        }
    }
}
