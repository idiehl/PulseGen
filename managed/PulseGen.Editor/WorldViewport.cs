using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using PulseGen.Contracts;
using PulseGen.Engine.Client;
using System.Globalization;

namespace PulseGen.Editor;

public sealed class WorldViewport : Control
{
    private enum CameraGesture
    {
        None,
        Orbit,
        Pan
    }

    private static readonly Pen GridPen = new(new SolidColorBrush(Color.Parse("#1A2633")), 1);
    private static readonly Pen PathPen = new(new SolidColorBrush(Color.Parse("#56D6C8")), 2.5);
    private static readonly Pen InactivePathPen = new(new SolidColorBrush(Color.Parse("#2D6D68")), 1.2);
    private static readonly IBrush PathPointBrush = new SolidColorBrush(Color.Parse("#8CF0E2"));
    private static readonly IBrush EmitterBrush = new SolidColorBrush(Color.Parse("#F2B766"));
    private static readonly IBrush ReceiverBrush = new SolidColorBrush(Color.Parse("#A899DE"));
    private static readonly Pen WindZonePen = new(new SolidColorBrush(Color.FromArgb(120, 74, 169, 226)), 1.1, new DashStyle([5, 4], 0));
    private static readonly Pen WindArrowPen = new(new SolidColorBrush(Color.Parse("#62BEEB")), 2.0);
    private static readonly Pen SelectedWindZonePen = new(new SolidColorBrush(Color.Parse("#8EE8FF")), 2.2, new DashStyle([7, 3], 0));
    private static readonly Pen SelectedObjectPen = new(new SolidColorBrush(Color.Parse("#F6D48A")), 2.6);
    private static readonly Pen FirstReflectionPen = new(new SolidColorBrush(Color.FromArgb(220, 71, 220, 210)), 1.8, new DashStyle([7, 3], 0));
    private static readonly Pen SecondReflectionPen = new(new SolidColorBrush(Color.FromArgb(225, 199, 139, 246)), 2.0, new DashStyle([4, 3], 0));
    private static readonly IBrush ReflectionPointBrush = new SolidColorBrush(Color.Parse("#FFE29B"));
    private static readonly Pen AtmosphereLayerPen = new(new SolidColorBrush(Color.FromArgb(105, 101, 206, 196)), 1.0, new DashStyle([5, 5], 0));
    private static readonly Pen SelectedAtmosphereLayerPen = new(new SolidColorBrush(Color.Parse("#7BE5D8")), 2.0);
    private static readonly IBrush RoomBrush = new SolidColorBrush(Color.Parse("#121D27"));
    private static readonly Pen RoomPen = new(new SolidColorBrush(Color.Parse("#34465B")), 1.3);
    private double timeSeconds;
    private int? draggingKeyframeIndex;
    private Guid? draggingMotionPathId;
    private MotionPath? draggingMotionPathOriginal;
    private SceneSelection? draggingSpatialObject;
    private bool dragMoved;
    private Vector3d dragOriginalPosition;
    private WorldDragProjection? dragProjection;
    private Point dragStartScreen;
    private IPointer? capturedPointer;
    private WorldInteractionMode interactionMode;
    private WorldMoveConstraint moveConstraint;
    private bool isGestureRecording;
    private SceneSelection selection;
    private Guid? activeMotionPathId;
    private bool gestureCapturing;
    private DateTimeOffset gestureStarted;
    private readonly List<MotionKeyframe> gesturePoints = [];
    private SceneDocument? scene;
    private IReadOnlyList<ImportedMeshReflectionPathData> reflectionPaths = [];
    private ViewportCameraController camera = new();
    private CameraGesture cameraGesture;
    private Point cameraGesturePoint;

    public WorldViewport()
    {
        Focusable = true;
        camera.Changed += OnCameraChanged;
    }

    public SceneDocument? Scene
    {
        get => scene;
        set
        {
            if (!ReferenceEquals(scene, value)) CancelInteraction();
            scene = value;
            InvalidateVisual();
        }
    }

    public bool IsGestureRecording
    {
        get => isGestureRecording;
        set
        {
            if (isGestureRecording == value) return;
            CancelInteraction();
            isGestureRecording = value;
            if (value) interactionMode = WorldInteractionMode.Select;
            InvalidateVisual();
        }
    }

    public WorldInteractionMode InteractionMode
    {
        get => interactionMode;
        set
        {
            if (interactionMode == value && !isGestureRecording) return;
            CancelInteraction();
            isGestureRecording = false;
            interactionMode = value;
            InvalidateVisual();
        }
    }

    public WorldMoveConstraint MoveConstraint
    {
        get => moveConstraint;
        set
        {
            if (moveConstraint == value) return;
            CancelInteraction();
            moveConstraint = value;
            InvalidateVisual();
        }
    }

    public bool SnapToGrid { get; set; }

    public double SnapStepMetres { get; set; } = 0.1;

    internal Vector3d? SelectedPosition
    {
        get
        {
            var path = ActiveMotionPath();
            if (path is not null && SelectedKeyframeIndex >= 0 && SelectedKeyframeIndex < path.Keyframes.Count)
                return path.Keyframes[SelectedKeyframeIndex].PositionMetres;
            return TryGetSpatialPosition(Selection, out var position) ? position : null;
        }
    }

    public void FocusSelection()
    {
        CancelInteraction();
        if (SelectedPosition is { } position) camera.Focus(position);
        else camera.Fit();
    }

    internal bool TrySetSelectedPosition(Vector3d position)
    {
        if (Scene is null || !double.IsFinite(position.X) || !double.IsFinite(position.Y) || !double.IsFinite(position.Z)) return false;
        CancelInteraction();
        var path = ActiveMotionPath();
        if (path is not null && SelectedKeyframeIndex >= 0 && SelectedKeyframeIndex < path.Keyframes.Count)
        {
            if ((path.Keyframes[SelectedKeyframeIndex].PositionMetres - position).Length <= 1.0e-9) return true;
            EditStarted?.Invoke(this, EventArgs.Empty);
            SetPathPoint(path, SelectedKeyframeIndex, position, announce: true, validate: true);
            return path.Keyframes[SelectedKeyframeIndex].PositionMetres == position;
        }
        if (!TryGetSpatialPosition(Selection, out var original)) return false;
        if ((original - position).Length <= 1.0e-9) return true;
        EditStarted?.Invoke(this, EventArgs.Empty);
        MoveSpatialObject(Selection, position);
        if (FirstSceneError() is { } error)
        {
            MoveSpatialObject(Selection, original);
            NotifySelectedPosition();
            SceneEditRejected?.Invoke($"Position edit rejected · {error.Message}");
            return false;
        }
        SpatialObjectMoved?.Invoke(Selection);
        NotifySelectedPosition();
        AnnounceSceneEdit($"{SpatialObjectName(Selection)} positioned precisely · scene is dirty");
        return true;
    }

    private void NotifySelectedPosition() => SelectedPositionChanged?.Invoke(SelectedPosition);

    public IReadOnlyList<ImportedMeshReflectionPathData> ReflectionPaths
    {
        get => reflectionPaths;
        set
        {
            reflectionPaths = value ?? [];
            InvalidateVisual();
        }
    }

    internal ViewportCameraController Camera
    {
        get => camera;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            if (ReferenceEquals(camera, value))
            {
                return;
            }
            camera.Changed -= OnCameraChanged;
            camera = value;
            camera.Changed += OnCameraChanged;
            InvalidateVisual();
        }
    }

    public int SelectedKeyframeIndex { get; private set; }

    internal Guid? ActiveMotionPathId
    {
        get => activeMotionPathId;
        set
        {
            if (activeMotionPathId == value)
            {
                return;
            }

            activeMotionPathId = value;
            SelectedKeyframeIndex = 0;
            NotifySelectedPosition();
            InvalidateVisual();
        }
    }

    internal SceneSelection Selection
    {
        get => selection;
        set
        {
            selection = value;
            NotifySelectedPosition();
            InvalidateVisual();
        }
    }

    public double TimeSeconds
    {
        get => timeSeconds;
        set
        {
            timeSeconds = value;
            InvalidateVisual();
        }
    }

    public event EventHandler? EditStarted;

    internal event Action<Vector3d?>? SelectedPositionChanged;

    public event EventHandler<int>? PathPointSelected;

    public event EventHandler<string>? SceneEdited;

    internal event Action<string>? SceneEditRejected;

    internal event Action<SceneSelection>? SceneObjectSelected;

    internal event Action<SceneSelection>? SpatialObjectMoved;

    internal event Func<Point, Guid?>? GeometryPickRequested;

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (Bounds.Width < 10 || Bounds.Height < 10)
        {
            return;
        }

        // Avalonia hit-tests custom controls against their drawing operations. The transparent
        // world-sized surface lets empty-space orbit/pan/zoom receive input above the GPU view.
        context.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));
        DrawGrid(context);
        if (Scene is null)
        {
            return;
        }

        DrawAtmosphereLayers(context);
        DrawWindZones(context);
        DrawRoom(context);
        DrawAcousticGeometry(context);
        foreach (var path in Scene.MotionPaths)
        {
            DrawPath(context, path);
        }
        DrawReflectionPaths(context);

        foreach (var receiver in Scene.ReceiverRigs.Where(receiver => receiver.Enabled))
        {
            var path = Scene.MotionPaths.FirstOrDefault(candidate => candidate.Id == receiver.MotionPathId);
            var position = path is null ? receiver.Transform.PositionMetres : SceneKinematics.PositionAt(path, TimeSeconds);
            DrawReceiver(context, receiver, position, Selection.Kind == SceneSelectionKind.Receiver && Selection.Id == receiver.Id);
        }

        foreach (var emitter in Scene.Emitters.Where(emitter => emitter.Enabled))
        {
            var path = Scene.MotionPaths.FirstOrDefault(candidate => candidate.Id == emitter.MotionPathId);
            var position = path is null ? emitter.Transform.PositionMetres : SceneKinematics.PositionAt(path, TimeSeconds);
            DrawEmitter(context, position, Selection.Kind == SceneSelectionKind.Emitter && Selection.Id == emitter.Id);
        }
        DrawSelectionGuides(context);
        DrawAxisLegend(context);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        var point = e.GetPosition(this);
        var pointerProperties = e.GetCurrentPoint(this).Properties;
        // A second button cannot switch coordinate systems in the middle of an edit.
        if (dragProjection is not null || gestureCapturing || cameraGesture != CameraGesture.None) return;
        if (pointerProperties.IsRightButtonPressed || pointerProperties.IsMiddleButtonPressed)
        {
            cameraGesture = pointerProperties.IsMiddleButtonPressed || e.KeyModifiers.HasFlag(KeyModifiers.Shift)
                ? CameraGesture.Pan
                : CameraGesture.Orbit;
            cameraGesturePoint = point;
            capturedPointer = e.Pointer;
            e.Pointer.Capture(this);
            e.Handled = true;
            return;
        }
        if (!pointerProperties.IsLeftButtonPressed) return;
        if (BeginPrimaryInteraction(point))
        {
            capturedPointer = e.Pointer;
            e.Pointer.Capture(this);
        }
        e.Handled = true;
    }

    // This boundary is shared by actual pointer input and headless regression tests.
    internal bool BeginPrimaryInteraction(Point point)
    {
        if (dragProjection is not null || gestureCapturing || cameraGesture != CameraGesture.None) return false;
        if (!IsGestureRecording && InteractionMode is WorldInteractionMode.Orbit or WorldInteractionMode.Pan)
        {
            cameraGesture = InteractionMode == WorldInteractionMode.Orbit ? CameraGesture.Orbit : CameraGesture.Pan;
            cameraGesturePoint = point;
            return true;
        }
        if (Scene is null) return false;
        if (IsGestureRecording)
        {
            if (ActiveMotionPath() is null || !camera.TryProjectToPlane(point, Bounds.Size, new(0, 1.7, 0), new(0, 1, 0), out var position) ||
                Math.Abs(camera.Ray(point, Bounds.Size).Direction.Y) < 0.08)
            {
                SceneEditRejected?.Invoke("Choose Top or Perspective view to draw a path on the horizontal plane.");
                return false;
            }
            EditStarted?.Invoke(this, EventArgs.Empty);
            gesturePoints.Clear();
            gestureStarted = DateTimeOffset.UtcNow;
            gestureCapturing = true;
            gesturePoints.Add(new MotionKeyframe { TimeSeconds = 0, PositionMetres = position });
            return true;
        }

        var path = ActiveMotionPath();
        if (path is not null)
        {
            var nearest = FindNearestPathPoint(path, point);
            if (nearest.Index >= 0 && nearest.Distance <= 14)
            {
                SelectedKeyframeIndex = nearest.Index;
                PathPointSelected?.Invoke(this, nearest.Index);
                NotifySelectedPosition();
                InvalidateVisual();
                if (InteractionMode != WorldInteractionMode.Move || !BeginMove(point, path.Keyframes[nearest.Index].PositionMetres)) return false;
                draggingKeyframeIndex = nearest.Index;
                draggingMotionPathId = path.Id;
                draggingMotionPathOriginal = CloneMotionPath(path);
                return true;
            }
        }

        var worldSelection = FindWorldSelection(point);
        if (worldSelection.Kind == SceneSelectionKind.None && GeometryPickRequested?.Invoke(point) is { } geometryId)
            worldSelection = SceneSelection.Geometry(geometryId);
        if (worldSelection.Kind == SceneSelectionKind.None) return false; // Empty world never seeks the track.
        Selection = worldSelection;
        SceneObjectSelected?.Invoke(worldSelection);
        NotifySelectedPosition();
        if (InteractionMode == WorldInteractionMode.Move && TryGetSpatialPosition(worldSelection, out var spatialPosition) && BeginMove(point, spatialPosition))
        {
            draggingSpatialObject = worldSelection;
            return true;
        }
        // Animated sources are moved using their explicit path points, not an ambiguous position at playback time.
        return false;
    }

    private bool BeginMove(Point point, Vector3d original)
    {
        if (!WorldDragProjection.TryBegin(camera, point, Bounds.Size, original, MoveConstraint, out dragProjection))
        {
            SceneEditRejected?.Invoke("This movement plane is edge-on. Choose a matching Top, Front or Side view, or another axis.");
            return false;
        }
        dragOriginalPosition = original;
        dragStartScreen = point;
        dragMoved = false;
        return true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (UpdatePrimaryInteraction(e.GetPosition(this))) e.Handled = true;
    }

    internal bool UpdatePrimaryInteraction(Point point)
    {
        if (cameraGesture != CameraGesture.None)
        {
            var deltaX = point.X - cameraGesturePoint.X;
            var deltaY = point.Y - cameraGesturePoint.Y;
            cameraGesturePoint = point;
            if (cameraGesture == CameraGesture.Orbit)
            {
                camera.Orbit(deltaX, deltaY);
            }
            else
            {
                camera.Pan(deltaX, deltaY, Bounds.Size);
            }
            return true;
        }
        if (Scene is null) return false;

        if (gestureCapturing)
        {
            var elapsed = (DateTimeOffset.UtcNow - gestureStarted).TotalSeconds;
            if (elapsed - gesturePoints[^1].TimeSeconds >= 1.0 / 120.0 &&
                Math.Abs(camera.Ray(point, Bounds.Size).Direction.Y) >= 0.08 &&
                camera.TryProjectToPlane(point, Bounds.Size, new(0, 1.7, 0), new(0, 1, 0), out var position))
            {
                gesturePoints.Add(new MotionKeyframe { TimeSeconds = elapsed, PositionMetres = position });
                InvalidateVisual();
            }
            return true;
        }
        if (dragProjection is null) return false;
        if (!dragMoved && Distance(point, dragStartScreen) < 3) return true;
        if (!dragProjection.TryMove(camera, point, Bounds.Size, SnapToGrid, SnapStepMetres, out var candidate)) return true;
        if (!dragMoved && (candidate - dragOriginalPosition).Length < 1.0e-9) return true;
        if (!dragMoved) EditStarted?.Invoke(this, EventArgs.Empty);
        dragMoved = true;
        if (draggingKeyframeIndex is { } index)
        {
            var path = draggingMotionPathId is { } pathId
                ? Scene.MotionPaths.FirstOrDefault(item => item.Id == pathId)
                : null;
            if (path is null || index < 0 || index >= path.Keyframes.Count)
            {
                return true;
            }
            SetPathPoint(path, index, candidate, announce: false, validate: false);
            PathPointSelected?.Invoke(this, index);
        }
        else if (draggingSpatialObject is { } spatialSelection)
        {
            MoveSpatialObject(spatialSelection, candidate);
            SpatialObjectMoved?.Invoke(spatialSelection);
        }
        NotifySelectedPosition();
        return true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (cameraGesture != CameraGesture.None && e.InitialPressMouseButton is not MouseButton.Left and not MouseButton.Right and not MouseButton.Middle) return;
        if (cameraGesture == CameraGesture.None && e.InitialPressMouseButton != MouseButton.Left) return;
        CompletePrimaryInteraction();
        e.Handled = true;
    }

    internal void CompletePrimaryInteraction()
    {
        if (gestureCapturing)
        {
            var elapsed = Math.Max((DateTimeOffset.UtcNow - gestureStarted).TotalSeconds, 0.01);
            if (gesturePoints.Count == 1 || elapsed > gesturePoints[^1].TimeSeconds)
            {
                gesturePoints.Add(new MotionKeyframe { TimeSeconds = elapsed, PositionMetres = gesturePoints[^1].PositionMetres });
            }
            // A click is not a new path. Raw capture data is retained only after actual movement.
            gestureCapturing = false;
            isGestureRecording = false;
            if (gesturePoints.Any(item => (item.PositionMetres - gesturePoints[0].PositionMetres).Length > 1.0e-6)) CommitGesture();
        }
        else if (dragMoved && draggingKeyframeIndex is not null)
        {
            var error = FirstSceneError();
            if (error is not null && draggingMotionPathId is { } pathId && draggingMotionPathOriginal is not null)
            {
                RestoreMotionPath(pathId, draggingMotionPathOriginal);
                SceneEditRejected?.Invoke($"Motion keyframe move rejected · {error.Message}");
                PathPointSelected?.Invoke(this, draggingKeyframeIndex.Value);
            }
            else if (ActiveDragPositionChanged())
            {
                AnnounceSceneEdit($"Motion keyframe {draggingKeyframeIndex.Value + 1} moved in SI world coordinates · scene is dirty");
            }
        }
        else if (draggingSpatialObject is { } spatialSelection && dragMoved)
        {
            if (FirstSceneError() is { } error)
            {
                MoveSpatialObject(spatialSelection, dragOriginalPosition);
                SpatialObjectMoved?.Invoke(spatialSelection);
                SceneEditRejected?.Invoke($"Move cancelled · {error.Message}");
            }
            else if (ActiveDragPositionChanged())
                AnnounceSceneEdit($"{SpatialObjectName(spatialSelection)} moved · scene is dirty");
        }
        ClearInteraction();
    }

    private bool ActiveDragPositionChanged()
    {
        if (draggingMotionPathId is { } pathId && draggingKeyframeIndex is { } index &&
            Scene?.MotionPaths.FirstOrDefault(item => item.Id == pathId) is { } path)
            return (path.Keyframes[index].PositionMetres - dragOriginalPosition).Length > 1.0e-9;
        return draggingSpatialObject is { } spatial && TryGetSpatialPosition(spatial, out var position) &&
            (position - dragOriginalPosition).Length > 1.0e-9;
    }

    internal void CancelInteraction()
    {
        if (draggingMotionPathId is { } pathId && draggingMotionPathOriginal is not null)
        {
            RestoreMotionPath(pathId, draggingMotionPathOriginal);
            if (draggingKeyframeIndex is { } index) PathPointSelected?.Invoke(this, index);
        }
        if (draggingSpatialObject is { } spatial && dragMoved)
        {
            MoveSpatialObject(spatial, dragOriginalPosition);
            SpatialObjectMoved?.Invoke(spatial);
        }
        if (gestureCapturing) isGestureRecording = false;
        ClearInteraction();
    }

    private void ClearInteraction()
    {
        draggingKeyframeIndex = null;
        draggingMotionPathId = null;
        draggingMotionPathOriginal = null;
        draggingSpatialObject = null;
        dragProjection = null;
        dragMoved = false;
        gestureCapturing = false;
        cameraGesture = CameraGesture.None;
        var pointer = capturedPointer;
        capturedPointer = null;
        pointer?.Capture(null);
        NotifySelectedPosition();
        InvalidateVisual();
    }

    private void AnnounceSceneEdit(string message)
    {
        // Subscribers rebuild linked views and may replace the document. The committed edit must no longer look cancellable.
        ClearInteraction();
        SceneEdited?.Invoke(this, message);
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        if (capturedPointer is not null) CancelInteraction();
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (dragProjection is not null || gestureCapturing) { e.Handled = true; return; }
        camera.Zoom(e.Delta.Y);
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Escape)
        {
            CancelInteraction();
            isGestureRecording = false;
            e.Handled = true;
            return;
        }
        if (e.Key == Key.F && e.KeyModifiers == KeyModifiers.None)
        {
            CancelInteraction();
            camera.Fit();
            e.Handled = true;
        }
    }

    public void SetPathPoint(int index, Vector3d position)
    {
        var path = ActiveMotionPath();
        if (path is not null)
        {
            EditStarted?.Invoke(this, EventArgs.Empty);
            SetPathPoint(path, index, position, announce: true, validate: true);
        }
    }

    private void SetPathPoint(MotionPath path, int index, Vector3d position, bool announce, bool validate)
    {
        if (Scene is null || index < 0 || index >= path.Keyframes.Count ||
            !double.IsFinite(position.X) || !double.IsFinite(position.Y) || !double.IsFinite(position.Z))
        {
            return;
        }
        var original = path.Keyframes[index];
        if (original.PositionMetres == position) return;
        path.Keyframes[index] = path.Keyframes[index] with { PositionMetres = position };
        if (validate && FirstSceneError() is { } error)
        {
            path.Keyframes[index] = original;
            InvalidateVisual();
            SceneEditRejected?.Invoke($"Motion keyframe edit rejected · {error.Message}");
            PathPointSelected?.Invoke(this, index);
            NotifySelectedPosition();
            return;
        }
        SelectedKeyframeIndex = index;
        InvalidateVisual();
        NotifySelectedPosition();
        if (announce)
        {
            PathPointSelected?.Invoke(this, index);
            AnnounceSceneEdit($"Motion keyframe {index + 1} set to ({position.X:0.###}, {position.Y:0.###}, {position.Z:0.###}) m · scene is dirty");
        }
    }

    private void DrawGrid(DrawingContext context)
    {
        // These are real world axes, not a decorative screen-space horizon.
        const int extent = 40;
        for (var step = -extent; step <= extent; step += 2)
        {
            if (camera.ViewPreset == ViewportViewPreset.Front)
            {
                context.DrawLine(GridPen, Project(new(step, -extent, 0)), Project(new(step, extent, 0)));
                context.DrawLine(GridPen, Project(new(-extent, step, 0)), Project(new(extent, step, 0)));
            }
            else if (camera.ViewPreset == ViewportViewPreset.Right)
            {
                context.DrawLine(GridPen, Project(new(0, -extent, step)), Project(new(0, extent, step)));
                context.DrawLine(GridPen, Project(new(0, step, -extent)), Project(new(0, step, extent)));
            }
            else
            {
                context.DrawLine(GridPen, Project(new(step, 0, -extent)), Project(new(step, 0, extent)));
                context.DrawLine(GridPen, Project(new(-extent, 0, step)), Project(new(extent, 0, step)));
            }
        }
        context.DrawLine(new Pen(AxisXBrush, 1.2), Project(new(-extent, 0, 0)), Project(new(extent, 0, 0)));
        context.DrawLine(new Pen(AxisYBrush, 1.2), Project(new(0, -extent, 0)), Project(new(0, extent, 0)));
        context.DrawLine(new Pen(AxisZBrush, 1.2), Project(new(0, 0, -extent)), Project(new(0, 0, extent)));
    }

    private static readonly IBrush AxisXBrush = new SolidColorBrush(Color.Parse("#EE827C"));
    private static readonly IBrush AxisYBrush = new SolidColorBrush(Color.Parse("#83D6A2"));
    private static readonly IBrush AxisZBrush = new SolidColorBrush(Color.Parse("#78AEF0"));
    private static readonly IBrush GuideTextBrush = new SolidColorBrush(Color.Parse("#D5DFE8"));
    private static readonly IBrush GuideBackgroundBrush = new SolidColorBrush(Color.FromArgb(230, 15, 23, 32));

    private void DrawSelectionGuides(DrawingContext context)
    {
        if (SelectedPosition is not { } position) return;
        var point = Project(position);
        var foot = Project(new(position.X, 0, position.Z));
        var guidePen = new Pen(EmitterBrush, 1.3, new DashStyle([4, 3], 0));
        context.DrawLine(guidePen, point, foot);
        // A grounded footprint and drop line remain visible through room surfaces, helping read depth/height.
        var footprintSize = Math.Max(0.12, camera.DistanceMetres * 0.005);
        context.DrawLine(guidePen, Project(new(position.X - footprintSize, 0, position.Z)), Project(new(position.X + footprintSize, 0, position.Z)));
        context.DrawLine(guidePen, Project(new(position.X, 0, position.Z - footprintSize)), Project(new(position.X, 0, position.Z + footprintSize)));
        context.DrawEllipse(new SolidColorBrush(Color.FromArgb(60, 242, 183, 102)), new Pen(EmitterBrush, 1.2), foot, 7, 3.5);

        var axisLength = Math.Max(0.1, camera.DistanceMetres * 0.055);
        DrawAxis(context, point, Project(position + new Vector3d(axisLength, 0, 0)), "X", AxisXBrush);
        DrawAxis(context, point, Project(position + new Vector3d(0, axisLength, 0)), "Y", AxisYBrush);
        DrawAxis(context, point, Project(position + new Vector3d(0, 0, axisLength)), "Z", AxisZBrush);
        context.DrawEllipse(null, new Pen(EmitterBrush, 1.8), point, 10, 10);

        if (point.X < 0 || point.X > Bounds.Width || point.Y < 0 || point.Y > Bounds.Height) return;
        var text = string.Create(CultureInfo.InvariantCulture, $"X {position.X:0.###}   Y {position.Y:0.###}   Z {position.Z:0.###} m");
        var formatted = Text(text, GuideTextBrush, 12);
        var label = new Point(Math.Clamp(point.X + 15, 6, Math.Max(6, Bounds.Width - formatted.Width - 20)), Math.Clamp(point.Y - 33, 6, Math.Max(6, Bounds.Height - 35)));
        context.DrawRectangle(GuideBackgroundBrush, new Pen(RoomPen.Brush, 1), new Rect(label.X - 6, label.Y - 4, formatted.Width + 12, formatted.Height + 8), 4, 4);
        context.DrawText(formatted, label);
    }

    private void DrawAxisLegend(DrawingContext context)
    {
        var centre = new Point(52, Math.Max(100, Bounds.Height - 98));
        var targetProjection = Project(camera.Target);
        Point Offset(Vector3d axis)
        {
            var endpoint = Project(camera.Target + axis * (camera.DistanceMetres * 0.075));
            return new(centre.X + endpoint.X - targetProjection.X, centre.Y + endpoint.Y - targetProjection.Y);
        }
        context.DrawEllipse(GuideBackgroundBrush, null, centre, 44, 44);
        DrawAxis(context, centre, Offset(new(1, 0, 0)), "X", AxisXBrush);
        DrawAxis(context, centre, Offset(new(0, 1, 0)), "Y", AxisYBrush);
        DrawAxis(context, centre, Offset(new(0, 0, 1)), "Z", AxisZBrush);
        context.DrawText(Text("Y = height · metres", GuideTextBrush, 11), new Point(12, centre.Y + 46));
    }

    private static void DrawAxis(DrawingContext context, Point start, Point end, string name, IBrush brush)
    {
        if (Distance(start, end) < 4) return;
        context.DrawLine(new Pen(brush, 2), start, end);
        context.DrawEllipse(brush, null, end, 3, 3);
        context.DrawText(Text(name, brush, 12), new Point(end.X + 5, end.Y - 8));
    }

    private static FormattedText Text(string text, IBrush brush, double size) =>
        new(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Typeface.Default, size, brush);

    private void DrawAtmosphereLayers(DrawingContext context)
    {
        if (Scene is null)
        {
            return;
        }
        for (var index = 0; index < Scene.Atmosphere.Layers.Count; index++)
        {
            var layer = Scene.Atmosphere.Layers[index];
            var selected = Selection.Kind == SceneSelectionKind.AtmosphereLayer && Selection.Index == index;
            var pen = selected ? SelectedAtmosphereLayerPen : AtmosphereLayerPen;
            var baseStart = Project(new Vector3d(-32, layer.BaseAltitudeMetres, -10));
            var baseEnd = Project(new Vector3d(32, layer.BaseAltitudeMetres, -10));
            var topStart = Project(new Vector3d(-32, layer.TopAltitudeMetres, -10));
            var topEnd = Project(new Vector3d(32, layer.TopAltitudeMetres, -10));
            var fill = new SolidColorBrush(Color.FromArgb(selected ? (byte)22 : (byte)9, 82, 196, 184));
            DrawFace(context, [baseStart, baseEnd, topEnd, topStart], fill, pen);
            context.DrawEllipse(pen.Brush, null, baseStart, selected ? 4 : 2.5, selected ? 4 : 2.5);
        }
    }

    private void DrawWindZones(DrawingContext context)
    {
        if (Scene is null)
        {
            return;
        }
        foreach (var zone in Scene.Atmosphere.WindZones)
        {
            var selected = Selection.Kind == SceneSelectionKind.WindZone && Selection.Id == zone.Id;
            var zonePen = selected ? SelectedWindZonePen : WindZonePen;
            var dimensions = new Vector3d(
                zone.DimensionsMetres.X * zone.Transform.Scale.X,
                zone.DimensionsMetres.Y * zone.Transform.Scale.Y,
                zone.DimensionsMetres.Z * zone.Transform.Scale.Z);
            var half = dimensions * 0.5;
            var localCorners = new[]
            {
                new Vector3d(-half.X, -half.Y, -half.Z), new Vector3d(half.X, -half.Y, -half.Z),
                new Vector3d(half.X, half.Y, -half.Z), new Vector3d(-half.X, half.Y, -half.Z),
                new Vector3d(-half.X, -half.Y, half.Z), new Vector3d(half.X, -half.Y, half.Z),
                new Vector3d(half.X, half.Y, half.Z), new Vector3d(-half.X, half.Y, half.Z)
            };
            var corners = localCorners
                .Select(corner => Project(Rotate(corner, zone.Transform.RotationDegrees) + zone.Transform.PositionMetres))
                .ToArray();
            int[][] edges =
            [
                [0, 1], [1, 2], [2, 3], [3, 0],
                [4, 5], [5, 6], [6, 7], [7, 4],
                [0, 4], [1, 5], [2, 6], [3, 7]
            ];
            foreach (var edge in edges)
            {
                context.DrawLine(zonePen, corners[edge[0]], corners[edge[1]]);
            }

            var speed = zone.VelocityMetresPerSecond.Length;
            var start = Project(zone.Transform.PositionMetres);
            context.DrawEllipse(zonePen.Brush, null, start, selected ? 5 : 2.5, selected ? 5 : 2.5);
            if (speed <= 1.0e-9)
            {
                continue;
            }
            var arrowLength = Math.Min(8, Math.Max(1.5, speed * 0.3));
            var arrowVector = zone.VelocityMetresPerSecond * (arrowLength / speed);
            var end = Project(zone.Transform.PositionMetres + arrowVector);
            context.DrawLine(WindArrowPen, start, end);
            context.DrawEllipse(WindArrowPen.Brush, null, end, 3, 3);
        }
    }

    private void DrawRoom(DrawingContext context)
    {
        var geometry = Scene?.Geometry.FirstOrDefault(item => item.Kind == GeometryKind.ParametricRoom);
        if (geometry is null)
        {
            return;
        }

        var dimensions = new Vector3d(
            geometry.DimensionsMetres.X * geometry.Transform.Scale.X,
            geometry.DimensionsMetres.Y * geometry.Transform.Scale.Y,
            geometry.DimensionsMetres.Z * geometry.Transform.Scale.Z);
        var halfX = dimensions.X * 0.5;
        var halfY = dimensions.Y * 0.5;
        var halfZ = dimensions.Z * 0.5;
        var centre = geometry.Transform.PositionMetres;
        var selected = Selection.Kind == SceneSelectionKind.Geometry && Selection.Id == geometry.Id;
        var roomPen = selected ? SelectedObjectPen : RoomPen;
        var points = new[]
        {
            Project(centre + new Vector3d(-halfX, -halfY, -halfZ)), Project(centre + new Vector3d(halfX, -halfY, -halfZ)),
            Project(centre + new Vector3d(halfX, -halfY, halfZ)), Project(centre + new Vector3d(-halfX, -halfY, halfZ))
        };
        var shape = new StreamGeometry();
        using (var drawing = shape.Open())
        {
            drawing.BeginFigure(points[0], true);
            drawing.LineTo(points[1]);
            drawing.LineTo(points[2]);
            drawing.LineTo(points[3]);
            drawing.EndFigure(true);
        }
        context.DrawGeometry(RoomBrush, roomPen, shape);

        var ceiling = new[]
        {
            Project(centre + new Vector3d(-halfX, halfY, -halfZ)), Project(centre + new Vector3d(halfX, halfY, -halfZ)),
            Project(centre + new Vector3d(halfX, halfY, halfZ)), Project(centre + new Vector3d(-halfX, halfY, halfZ))
        };
        var wallBrush = new SolidColorBrush(Color.FromArgb(24, 62, 85, 108));
        DrawFace(context, [points[0], points[1], ceiling[1], ceiling[0]], wallBrush);
        DrawFace(context, [points[1], points[2], ceiling[2], ceiling[1]], wallBrush);
        DrawFace(context, [points[3], points[0], ceiling[0], ceiling[3]], wallBrush);
        for (var index = 0; index < ceiling.Length; index++)
        {
            context.DrawLine(roomPen, ceiling[index], ceiling[(index + 1) % ceiling.Length]);
            context.DrawLine(roomPen, points[index], ceiling[index]);
        }
        var centrePoint = Project(centre);
        context.DrawEllipse(roomPen.Brush, null, centrePoint, selected ? 5 : 2.5, selected ? 5 : 2.5);
    }

    private void DrawAcousticGeometry(DrawingContext context)
    {
        if (Scene is null)
        {
            return;
        }
        foreach (var geometry in Scene.Geometry.Where(item => item.Kind is GeometryKind.Plane or GeometryKind.Box or GeometryKind.ImportedMesh))
        {
            var selected = Selection.Kind == SceneSelectionKind.Geometry && Selection.Id == geometry.Id;
            var material = Scene.Materials.FirstOrDefault(candidate => candidate.Id == geometry.MaterialId);
            var absorption = material?.Absorption.DefaultIfEmpty(0.2).Average() ?? 0.2;
            var scattering = material?.Scattering.DefaultIfEmpty(0.1).Average() ?? 0.1;
            var importedMesh = geometry.Kind == GeometryKind.ImportedMesh;
            var fill = importedMesh
                ? new SolidColorBrush(Color.FromArgb(34, 77, 176, 220))
                : new SolidColorBrush(Color.FromArgb(
                    (byte)Math.Clamp(30 + (70 * (1 - absorption)), 20, 100),
                    (byte)Math.Clamp(48 + (90 * scattering), 0, 255),
                    (byte)Math.Clamp(112 + (80 * (1 - absorption)), 0, 255),
                    (byte)Math.Clamp(126 + (80 * scattering), 0, 255)));
            var pen = selected
                ? SelectedObjectPen
                : importedMesh
                    ? new Pen(new SolidColorBrush(Color.Parse("#4DC4E8")), 1.4, new DashStyle([6, 3], 0))
                    : new Pen(
                        new SolidColorBrush(geometry.Occludes ? Color.Parse("#EFBD72") : Color.Parse("#698397")),
                        geometry.Reflects ? 1.8 : 1.0,
                        geometry.Reflects ? null : new DashStyle([4, 3], 0));

            var dimensions = new Vector3d(
                geometry.DimensionsMetres.X * geometry.Transform.Scale.X,
                geometry.DimensionsMetres.Y * geometry.Transform.Scale.Y,
                geometry.DimensionsMetres.Z * geometry.Transform.Scale.Z);
            if (geometry.Kind == GeometryKind.Plane)
            {
                var halfX = dimensions.X * 0.5;
                var halfY = dimensions.Y * 0.5;
                var points = new[]
                {
                    Project(TransformPoint(geometry, new(-halfX, -halfY, 0))),
                    Project(TransformPoint(geometry, new(halfX, -halfY, 0))),
                    Project(TransformPoint(geometry, new(halfX, halfY, 0))),
                    Project(TransformPoint(geometry, new(-halfX, halfY, 0)))
                };
                DrawFace(context, points, fill, pen);
                var centrePoint = Project(geometry.Transform.PositionMetres);
                context.DrawEllipse(pen.Brush, null, centrePoint, selected ? 5 : 2.5, selected ? 5 : 2.5);
                continue;
            }

            var half = dimensions * 0.5;
            var corners = new[]
            {
                new Vector3d(-half.X, -half.Y, -half.Z), new Vector3d(half.X, -half.Y, -half.Z),
                new Vector3d(half.X, half.Y, -half.Z), new Vector3d(-half.X, half.Y, -half.Z),
                new Vector3d(-half.X, -half.Y, half.Z), new Vector3d(half.X, -half.Y, half.Z),
                new Vector3d(half.X, half.Y, half.Z), new Vector3d(-half.X, half.Y, half.Z)
            }.Select(corner => Project(TransformPoint(geometry, corner))).ToArray();
            int[][] faces = [[0, 1, 2, 3], [4, 5, 6, 7], [0, 1, 5, 4], [1, 2, 6, 5], [2, 3, 7, 6], [3, 0, 4, 7]];
            foreach (var face in faces)
            {
                DrawFace(context, face.Select(index => corners[index]).ToArray(), fill, pen);
            }
            var objectCentre = Project(geometry.Transform.PositionMetres);
            context.DrawEllipse(pen.Brush, null, objectCentre, selected ? 5 : 2.5, selected ? 5 : 2.5);
        }
    }

    private static Vector3d TransformPoint(AcousticGeometry geometry, Vector3d local) =>
        Rotate(local, geometry.Transform.RotationDegrees) + geometry.Transform.PositionMetres;

    private static Vector3d Rotate(Vector3d value, Vector3d rotationDegrees)
    {
        var x = rotationDegrees.X * Math.PI / 180;
        var cosine = Math.Cos(x);
        var sine = Math.Sin(x);
        value = new(value.X, (value.Y * cosine) - (value.Z * sine), (value.Y * sine) + (value.Z * cosine));

        var y = rotationDegrees.Y * Math.PI / 180;
        cosine = Math.Cos(y);
        sine = Math.Sin(y);
        value = new((value.X * cosine) + (value.Z * sine), value.Y, (-value.X * sine) + (value.Z * cosine));

        var z = rotationDegrees.Z * Math.PI / 180;
        cosine = Math.Cos(z);
        sine = Math.Sin(z);
        return new((value.X * cosine) - (value.Y * sine), (value.X * sine) + (value.Y * cosine), value.Z);
    }

    private void DrawPath(DrawingContext context, MotionPath path)
    {
        if (path.Keyframes.Count < 2)
        {
            return;
        }

        var active = path.Id == ActiveMotionPathId;
        var pathPen = active ? PathPen : InactivePathPen;
        Point? previous = null;
        const int segments = 100;
        var start = path.Keyframes[0].TimeSeconds;
        var end = path.Keyframes[^1].TimeSeconds;
        for (var index = 0; index <= segments; index++)
        {
            var point = Project(SceneKinematics.PositionAt(path, start + ((end - start) * index / segments)));
            if (previous is not null)
            {
                context.DrawLine(pathPen, previous.Value, point);
            }
            previous = point;
        }

        for (var index = 0; index < path.Keyframes.Count; index++)
        {
            var keyframe = path.Keyframes[index];
            var selected = active && index == SelectedKeyframeIndex;
            var radius = selected ? 6.0 : active ? 3.5 : 2.0;
            var brush = selected ? EmitterBrush : active ? PathPointBrush : pathPen.Brush;
            context.DrawEllipse(brush, null, Project(keyframe.PositionMetres), radius, radius);
        }

        if (gestureCapturing && gesturePoints.Count > 1)
        {
            for (var index = 1; index < gesturePoints.Count; index++)
            {
                context.DrawLine(new Pen(EmitterBrush, 2), Project(gesturePoints[index - 1].PositionMetres), Project(gesturePoints[index].PositionMetres));
            }
        }
    }

    private void DrawEmitter(DrawingContext context, Vector3d position, bool selected)
    {
        var point = Project(position);
        context.DrawEllipse(new SolidColorBrush(Color.FromArgb(40, 242, 183, 102)), null, point, 20, 20);
        context.DrawEllipse(EmitterBrush, selected ? SelectedObjectPen : new Pen(Brushes.White, 1), point, selected ? 8 : 7, selected ? 8 : 7);
    }

    private void DrawReflectionPaths(DrawingContext context)
    {
        foreach (var route in ReflectionPaths)
        {
            if (route.Points.Count is < 3 or > 4)
            {
                continue;
            }
            var pen = route.ReflectionOrder == 2 ? SecondReflectionPen : FirstReflectionPen;
            for (var index = 1; index < route.Points.Count; index++)
            {
                var start = Project(route.Points[index - 1]);
                var end = Project(route.Points[index]);
                context.DrawLine(new Pen(new SolidColorBrush(Color.FromArgb(65, 3, 8, 12)), pen.Thickness + 3.5), start, end);
                context.DrawLine(pen, start, end);
            }
            for (var index = 1; index < route.Points.Count - 1; index++)
            {
                var point = Project(route.Points[index]);
                context.DrawEllipse(ReflectionPointBrush, new Pen(pen.Brush, 1.2), point, 4.2, 4.2);
            }
        }
    }

    private void DrawReceiver(DrawingContext context, ReceiverRig receiver, Vector3d position, bool selected)
    {
        Vector3d leftPosition;
        Vector3d rightPosition;
        if (receiver.Kind == ReceiverRigKind.StereoMicrophone &&
            receiver.LeftCapsule is { } left && receiver.RightCapsule is { } right)
        {
            leftPosition = ReceiverRigGeometry.CapsuleWorldPosition(receiver, left, position);
            rightPosition = ReceiverRigGeometry.CapsuleWorldPosition(receiver, right, position);
        }
        else
        {
            var localLeft = new Vector3d(-receiver.SpacingMetres * 0.5, 0, 0);
            var localRight = new Vector3d(receiver.SpacingMetres * 0.5, 0, 0);
            leftPosition = position + ReceiverRigGeometry.RotateYaw(localLeft, receiver.Transform.RotationDegrees.Y);
            rightPosition = position + ReceiverRigGeometry.RotateYaw(localRight, receiver.Transform.RotationDegrees.Y);
        }
        var leftPoint = Project(leftPosition);
        var rightPoint = Project(rightPosition);
        if (Distance(leftPoint, rightPoint) < 10)
        {
            var centre = Project(position);
            leftPoint = new Point(centre.X - 5, centre.Y);
            rightPoint = new Point(centre.X + 5, centre.Y);
        }
        var pen = selected ? SelectedObjectPen : new Pen(ReceiverBrush, 2);
        context.DrawLine(pen, leftPoint, rightPoint);
        context.DrawEllipse(ReceiverBrush, selected ? SelectedObjectPen : null, leftPoint, selected ? 5 : 4, selected ? 5 : 4);
        context.DrawEllipse(ReceiverBrush, selected ? SelectedObjectPen : null, rightPoint, selected ? 5 : 4, selected ? 5 : 4);
    }

    private Point Project(Vector3d point)
        => camera.Project(point, Bounds.Size);

    private static void DrawFace(DrawingContext context, IReadOnlyList<Point> points, IBrush brush, Pen? pen = null)
    {
        var geometry = new StreamGeometry();
        using (var drawing = geometry.Open())
        {
            drawing.BeginFigure(points[0], true);
            for (var index = 1; index < points.Count; index++)
            {
                drawing.LineTo(points[index]);
            }
            drawing.EndFigure(true);
        }
        context.DrawGeometry(brush, pen ?? RoomPen, geometry);
    }

    private void OnCameraChanged(object? sender, EventArgs e) => InvalidateVisual();

    private (int Index, double Distance) FindNearestPathPoint(MotionPath path, Point point)
    {
        var selected = -1;
        var best = double.PositiveInfinity;
        for (var index = 0; index < path.Keyframes.Count; index++)
        {
            var projected = Project(path.Keyframes[index].PositionMetres);
            var deltaX = projected.X - point.X;
            var deltaY = projected.Y - point.Y;
            var distance = Math.Sqrt((deltaX * deltaX) + (deltaY * deltaY));
            if (distance < best)
            {
                best = distance;
                selected = index;
            }
        }
        return (selected, best);
    }

    private SceneSelection FindWorldSelection(Point point)
    {
        if (Scene is null)
        {
            return default;
        }
        var bestSelection = default(SceneSelection);
        var bestDistance = double.PositiveInfinity;

        void Consider(SceneSelection candidate, Vector3d centre)
        {
            var distance = Distance(Project(centre), point);
            var threshold = candidate == Selection ? 24.0 : 14.0;
            if (distance <= threshold && distance < bestDistance)
            {
                bestSelection = candidate;
                bestDistance = distance;
            }
        }

        foreach (var emitter in Scene.Emitters.Where(item => item.Enabled))
        {
            var path = Scene.MotionPaths.FirstOrDefault(candidate => candidate.Id == emitter.MotionPathId);
            var position = path is null ? emitter.Transform.PositionMetres : SceneKinematics.PositionAt(path, TimeSeconds);
            Consider(SceneSelection.Emitter(emitter.Id), position);
        }
        foreach (var receiver in Scene.ReceiverRigs.Where(item => item.Enabled))
        {
            var path = Scene.MotionPaths.FirstOrDefault(candidate => candidate.Id == receiver.MotionPathId);
            var position = path is null ? receiver.Transform.PositionMetres : SceneKinematics.PositionAt(path, TimeSeconds);
            var receiverSelection = SceneSelection.Receiver(receiver.Id);
            Consider(receiverSelection, position);
            if (receiver.Kind == ReceiverRigKind.StereoMicrophone &&
                receiver.LeftCapsule is { } left && receiver.RightCapsule is { } right)
            {
                Consider(receiverSelection, ReceiverRigGeometry.CapsuleWorldPosition(receiver, left, position));
                Consider(receiverSelection, ReceiverRigGeometry.CapsuleWorldPosition(receiver, right, position));
            }
        }
        foreach (var geometry in Scene.Geometry)
        {
            Consider(SceneSelection.Geometry(geometry.Id), geometry.Transform.PositionMetres);
        }
        foreach (var zone in Scene.Atmosphere.WindZones)
        {
            Consider(SceneSelection.WindZone(zone.Id), zone.Transform.PositionMetres);
        }

        for (var index = 0; index < Scene.Atmosphere.Layers.Count; index++)
        {
            var layer = Scene.Atmosphere.Layers[index];
            var baseDistance = DistanceToSegment(
                point,
                Project(new Vector3d(-32, layer.BaseAltitudeMetres, -10)),
                Project(new Vector3d(32, layer.BaseAltitudeMetres, -10)));
            var topDistance = DistanceToSegment(
                point,
                Project(new Vector3d(-32, layer.TopAltitudeMetres, -10)),
                Project(new Vector3d(32, layer.TopAltitudeMetres, -10)));
            var distance = Math.Min(baseDistance, topDistance);
            if (distance <= 6 && distance < bestDistance)
            {
                bestSelection = SceneSelection.AtmosphereLayer(index);
                bestDistance = distance;
            }
        }
        return bestSelection;
    }

    private bool TryGetSpatialPosition(SceneSelection spatialSelection, out Vector3d position)
    {
        position = default;
        if (Scene is null)
        {
            return false;
        }
        if (spatialSelection.Kind == SceneSelectionKind.Geometry)
        {
            var geometry = Scene.Geometry.FirstOrDefault(item => item.Id == spatialSelection.Id);
            if (geometry is not null)
            {
                position = geometry.Transform.PositionMetres;
                return true;
            }
        }
        else if (spatialSelection.Kind == SceneSelectionKind.WindZone)
        {
            var zone = Scene.Atmosphere.WindZones.FirstOrDefault(item => item.Id == spatialSelection.Id);
            if (zone is not null)
            {
                position = zone.Transform.PositionMetres;
                return true;
            }
        }
        else if (spatialSelection.Kind == SceneSelectionKind.Receiver)
        {
            var receiver = Scene.ReceiverRigs.FirstOrDefault(item => item.Id == spatialSelection.Id);
            if (receiver is not null && receiver.MotionPathId is null)
            {
                position = receiver.Transform.PositionMetres;
                return true;
            }
        }
        else if (spatialSelection.Kind == SceneSelectionKind.Emitter)
        {
            var emitter = Scene.Emitters.FirstOrDefault(item => item.Id == spatialSelection.Id);
            if (emitter is not null && emitter.MotionPathId is null)
            {
                position = emitter.Transform.PositionMetres;
                return true;
            }
        }
        return false;
    }

    private void MoveSpatialObject(SceneSelection spatialSelection, Vector3d position)
    {
        if (Scene is null || !double.IsFinite(position.X) || !double.IsFinite(position.Y) || !double.IsFinite(position.Z))
        {
            return;
        }
        if (spatialSelection.Kind == SceneSelectionKind.Geometry)
        {
            var index = Scene.Geometry.FindIndex(item => item.Id == spatialSelection.Id);
            if (index >= 0)
            {
                var geometry = Scene.Geometry[index];
                Scene.Geometry[index] = geometry with { Transform = geometry.Transform with { PositionMetres = position } };
            }
        }
        else if (spatialSelection.Kind == SceneSelectionKind.WindZone)
        {
            var index = Scene.Atmosphere.WindZones.FindIndex(item => item.Id == spatialSelection.Id);
            if (index >= 0)
            {
                var zone = Scene.Atmosphere.WindZones[index];
                Scene.Atmosphere.WindZones[index] = zone with { Transform = zone.Transform with { PositionMetres = position } };
            }
        }
        else if (spatialSelection.Kind == SceneSelectionKind.Receiver)
        {
            var index = Scene.ReceiverRigs.FindIndex(item => item.Id == spatialSelection.Id);
            if (index >= 0 && Scene.ReceiverRigs[index].MotionPathId is null)
            {
                var receiver = Scene.ReceiverRigs[index];
                Scene.ReceiverRigs[index] = receiver with { Transform = receiver.Transform with { PositionMetres = position } };
            }
        }
        else if (spatialSelection.Kind == SceneSelectionKind.Emitter)
        {
            var index = Scene.Emitters.FindIndex(item => item.Id == spatialSelection.Id);
            if (index >= 0 && Scene.Emitters[index].MotionPathId is null)
            {
                var emitter = Scene.Emitters[index];
                Scene.Emitters[index] = emitter with { Transform = emitter.Transform with { PositionMetres = position } };
            }
        }
        InvalidateVisual();
    }

    private string SpatialObjectName(SceneSelection spatialSelection)
    {
        if (Scene is null)
        {
            return "Spatial object";
        }
        return spatialSelection.Kind switch
        {
            SceneSelectionKind.Geometry => Scene.Geometry.FirstOrDefault(item => item.Id == spatialSelection.Id)?.Name ?? "Geometry",
            SceneSelectionKind.WindZone => Scene.Atmosphere.WindZones.FirstOrDefault(item => item.Id == spatialSelection.Id)?.Name ?? "Wind zone",
            SceneSelectionKind.Receiver => Scene.ReceiverRigs.FirstOrDefault(item => item.Id == spatialSelection.Id)?.Name ?? "Receiver",
            SceneSelectionKind.Emitter => Scene.Emitters.FirstOrDefault(item => item.Id == spatialSelection.Id)?.Name ?? "Source",
            _ => "Spatial object"
        };
    }

    private static double Distance(Point left, Point right)
    {
        var x = left.X - right.X;
        var y = left.Y - right.Y;
        return Math.Sqrt((x * x) + (y * y));
    }

    private static double DistanceToSegment(Point point, Point start, Point end)
    {
        var segmentX = end.X - start.X;
        var segmentY = end.Y - start.Y;
        var lengthSquared = (segmentX * segmentX) + (segmentY * segmentY);
        if (lengthSquared <= double.Epsilon)
        {
            return Distance(point, start);
        }
        var amount = Math.Clamp(
            (((point.X - start.X) * segmentX) + ((point.Y - start.Y) * segmentY)) / lengthSquared,
            0,
            1);
        return Distance(point, new Point(start.X + (segmentX * amount), start.Y + (segmentY * amount)));
    }

    private MotionPath? ActiveMotionPath()
    {
        if (Scene is null || ActiveMotionPathId is not { } pathId)
        {
            return null;
        }

        return Scene.MotionPaths.FirstOrDefault(path => path.Id == pathId);
    }

    private ValidationIssue? FirstSceneError() => Scene is null
        ? new ValidationIssue("scene", "No scene is loaded.")
        : SceneValidator.Validate(Scene)
            .FirstOrDefault(issue => issue.Severity == ValidationSeverity.Error);

    private static MotionPath CloneMotionPath(MotionPath path) => path with
    {
        Keyframes = [.. path.Keyframes],
        RawGesture = [.. path.RawGesture],
        VelocityProfile = [.. path.VelocityProfile]
    };

    private void RestoreMotionPath(Guid pathId, MotionPath original)
    {
        if (Scene is null)
        {
            return;
        }

        var index = Scene.MotionPaths.FindIndex(path => path.Id == pathId);
        if (index >= 0)
        {
            Scene.MotionPaths[index] = original;
            InvalidateVisual();
        }
    }

    private void CommitGesture()
    {
        var existing = ActiveMotionPath();
        if (Scene is null || existing is null || gesturePoints.Count < 2)
        {
            return;
        }
        var simplified = SimplifyGesture(gesturePoints, 0.20);
        if (simplified.Count < 2)
        {
            simplified = [gesturePoints[0], gesturePoints[^1]];
        }
        var replacement = existing with
        {
            Kind = MotionPathKind.RecordedGesture,
            RawGesture = [.. gesturePoints],
            Keyframes = simplified
        };
        if (!SceneAuthoringOperations.TryReplaceMotionPath(Scene, existing.Id, replacement, out var error))
        {
            InvalidateVisual();
            SceneEditRejected?.Invoke($"Gesture rejected · {error?.Message ?? "the active path no longer exists"}");
            return;
        }
        SelectedKeyframeIndex = 0;
        InvalidateVisual();
        NotifySelectedPosition();
        PathPointSelected?.Invoke(this, 0);
        AnnounceSceneEdit($"Gesture captured · {gesturePoints.Count} raw samples retained · {simplified.Count} editable keyframes");
    }

    private static List<MotionKeyframe> SimplifyGesture(IReadOnlyList<MotionKeyframe> points, double toleranceMetres)
    {
        var keep = new bool[points.Count];
        keep[0] = true;
        keep[^1] = true;
        SimplifyRange(points, 0, points.Count - 1, toleranceMetres * toleranceMetres, keep);
        return points.Where((_, index) => keep[index]).ToList();
    }

    private static void SimplifyRange(IReadOnlyList<MotionKeyframe> points, int first, int last, double toleranceSquared, bool[] keep)
    {
        if (last <= first + 1)
        {
            return;
        }
        var start = points[first].PositionMetres;
        var end = points[last].PositionMetres;
        var segment = end - start;
        var segmentLengthSquared = (segment.X * segment.X) + (segment.Y * segment.Y) + (segment.Z * segment.Z);
        var furthest = -1;
        var maximumDistance = 0.0;
        for (var index = first + 1; index < last; index++)
        {
            var relative = points[index].PositionMetres - start;
            var amount = segmentLengthSquared <= double.Epsilon
                ? 0
                : Math.Clamp(((relative.X * segment.X) + (relative.Y * segment.Y) + (relative.Z * segment.Z)) / segmentLengthSquared, 0, 1);
            var nearest = start + (segment * amount);
            var difference = points[index].PositionMetres - nearest;
            var distanceSquared = (difference.X * difference.X) + (difference.Y * difference.Y) + (difference.Z * difference.Z);
            if (distanceSquared > maximumDistance)
            {
                maximumDistance = distanceSquared;
                furthest = index;
            }
        }
        if (furthest >= 0 && maximumDistance > toleranceSquared)
        {
            keep[furthest] = true;
            SimplifyRange(points, first, furthest, toleranceSquared, keep);
            SimplifyRange(points, furthest, last, toleranceSquared, keep);
        }
    }
}
