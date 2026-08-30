using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using PulseGen.Contracts;

namespace PulseGen.Editor;

public sealed class WorldViewport : Control
{
    private static readonly IBrush BackgroundBrush = new SolidColorBrush(Color.Parse("#090D12"));
    private static readonly Pen GridPen = new(new SolidColorBrush(Color.Parse("#1A2633")), 1);
    private static readonly Pen AxisPen = new(new SolidColorBrush(Color.Parse("#2C4D54")), 1.2);
    private static readonly Pen PathPen = new(new SolidColorBrush(Color.Parse("#56D6C8")), 2.5);
    private static readonly Pen InactivePathPen = new(new SolidColorBrush(Color.Parse("#2D6D68")), 1.2);
    private static readonly IBrush PathPointBrush = new SolidColorBrush(Color.Parse("#8CF0E2"));
    private static readonly IBrush EmitterBrush = new SolidColorBrush(Color.Parse("#F2B766"));
    private static readonly IBrush ReceiverBrush = new SolidColorBrush(Color.Parse("#A899DE"));
    private static readonly Pen WindZonePen = new(new SolidColorBrush(Color.FromArgb(120, 74, 169, 226)), 1.1, new DashStyle([5, 4], 0));
    private static readonly Pen WindArrowPen = new(new SolidColorBrush(Color.Parse("#62BEEB")), 2.0);
    private static readonly Pen SelectedWindZonePen = new(new SolidColorBrush(Color.Parse("#8EE8FF")), 2.2, new DashStyle([7, 3], 0));
    private static readonly Pen SelectedObjectPen = new(new SolidColorBrush(Color.Parse("#F6D48A")), 2.6);
    private static readonly Pen AtmosphereLayerPen = new(new SolidColorBrush(Color.FromArgb(105, 101, 206, 196)), 1.0, new DashStyle([5, 5], 0));
    private static readonly Pen SelectedAtmosphereLayerPen = new(new SolidColorBrush(Color.Parse("#7BE5D8")), 2.0);
    private static readonly IBrush RoomBrush = new SolidColorBrush(Color.Parse("#121D27"));
    private static readonly Pen RoomPen = new(new SolidColorBrush(Color.Parse("#34465B")), 1.3);
    private double timeSeconds;
    private int? draggingKeyframeIndex;
    private Guid? draggingMotionPathId;
    private MotionPath? draggingMotionPathOriginal;
    private SceneSelection? draggingSpatialObject;
    private Vector3d spatialDragOffset;
    private SceneSelection selection;
    private Guid? activeMotionPathId;
    private bool gestureCapturing;
    private DateTimeOffset gestureStarted;
    private readonly List<MotionKeyframe> gesturePoints = [];
    private SceneDocument? scene;

    public SceneDocument? Scene
    {
        get => scene;
        set
        {
            scene = value;
            InvalidateVisual();
        }
    }

    public bool IsGestureRecording { get; set; }

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
            InvalidateVisual();
        }
    }

    internal SceneSelection Selection
    {
        get => selection;
        set
        {
            selection = value;
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

    public event EventHandler<double>? PlayheadChanged;

    public event EventHandler<int>? PathPointSelected;

    public event EventHandler<string>? SceneEdited;

    internal event Action<string>? SceneEditRejected;

    internal event Action<SceneSelection>? SceneObjectSelected;

    internal event Action<SceneSelection>? SpatialObjectMoved;

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        context.FillRectangle(BackgroundBrush, Bounds);
        if (Bounds.Width < 10 || Bounds.Height < 10)
        {
            return;
        }

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
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (Scene is null)
        {
            return;
        }

        var point = e.GetPosition(this);
        if (IsGestureRecording)
        {
            gesturePoints.Clear();
            gestureStarted = DateTimeOffset.UtcNow;
            gestureCapturing = true;
            gesturePoints.Add(new MotionKeyframe { TimeSeconds = 0, PositionMetres = InverseProject(point, 1.7) });
            e.Pointer.Capture(this);
            e.Handled = true;
            return;
        }

        var path = ActiveMotionPath();
        if (path is not null)
        {
            var nearest = FindNearestPathPoint(path, point);
            if (nearest.Index >= 0 && nearest.Distance <= 14)
            {
                draggingKeyframeIndex = nearest.Index;
                draggingMotionPathId = path.Id;
                draggingMotionPathOriginal = CloneMotionPath(path);
                SelectedKeyframeIndex = nearest.Index;
                PathPointSelected?.Invoke(this, nearest.Index);
                e.Pointer.Capture(this);
                e.Handled = true;
                return;
            }
        }

        var worldSelection = FindWorldSelection(point);
        if (worldSelection.Kind != SceneSelectionKind.None)
        {
            Selection = worldSelection;
            SceneObjectSelected?.Invoke(worldSelection);
            if (worldSelection.IsSpatial && TryGetSpatialPosition(worldSelection, out var spatialPosition))
            {
                draggingSpatialObject = worldSelection;
                spatialDragOffset = spatialPosition - InverseProject(point, spatialPosition.Y);
                e.Pointer.Capture(this);
            }
            e.Handled = true;
            return;
        }

        var time = Math.Clamp(point.X / Math.Max(1, Bounds.Width), 0, 1) * Scene.Timeline.DurationSeconds;
        PlayheadChanged?.Invoke(this, time);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (Scene is null)
        {
            return;
        }

        var point = e.GetPosition(this);
        if (gestureCapturing)
        {
            var elapsed = (DateTimeOffset.UtcNow - gestureStarted).TotalSeconds;
            if (elapsed - gesturePoints[^1].TimeSeconds >= 1.0 / 120.0)
            {
                gesturePoints.Add(new MotionKeyframe { TimeSeconds = elapsed, PositionMetres = InverseProject(point, 1.7) });
                InvalidateVisual();
            }
            e.Handled = true;
            return;
        }

        if (draggingKeyframeIndex is { } index)
        {
            var path = draggingMotionPathId is { } pathId
                ? Scene.MotionPaths.FirstOrDefault(item => item.Id == pathId)
                : null;
            if (path is null || index < 0 || index >= path.Keyframes.Count)
            {
                return;
            }
            var existing = path.Keyframes[index];
            SetPathPoint(path, index, InverseProject(point, existing.PositionMetres.Y), announce: false, validate: false);
            PathPointSelected?.Invoke(this, index);
            e.Handled = true;
            return;
        }

        if (draggingSpatialObject is { } spatialSelection && TryGetSpatialPosition(spatialSelection, out var spatialPosition))
        {
            var position = InverseProject(point, spatialPosition.Y) + spatialDragOffset;
            MoveSpatialObject(spatialSelection, position);
            SpatialObjectMoved?.Invoke(spatialSelection);
            e.Handled = true;
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (gestureCapturing)
        {
            var elapsed = Math.Max((DateTimeOffset.UtcNow - gestureStarted).TotalSeconds, 0.01);
            var finalPosition = InverseProject(e.GetPosition(this), 1.7);
            if (gesturePoints.Count == 1 || elapsed > gesturePoints[^1].TimeSeconds)
            {
                gesturePoints.Add(new MotionKeyframe { TimeSeconds = elapsed, PositionMetres = finalPosition });
            }
            CommitGesture();
            gestureCapturing = false;
            IsGestureRecording = false;
        }
        else if (draggingKeyframeIndex is not null)
        {
            var error = FirstSceneError();
            if (error is not null && draggingMotionPathId is { } pathId && draggingMotionPathOriginal is not null)
            {
                RestoreMotionPath(pathId, draggingMotionPathOriginal);
                SceneEditRejected?.Invoke($"Motion keyframe move rejected · {error.Message}");
                PathPointSelected?.Invoke(this, draggingKeyframeIndex.Value);
            }
            else
            {
                SceneEdited?.Invoke(this, $"Motion keyframe {draggingKeyframeIndex.Value + 1} moved in SI world coordinates · scene is dirty");
            }
        }
        else if (draggingSpatialObject is { } spatialSelection)
        {
            var name = SpatialObjectName(spatialSelection);
            SceneEdited?.Invoke(this, $"{name} moved in SI world coordinates · scene is dirty");
        }

        draggingKeyframeIndex = null;
        draggingMotionPathId = null;
        draggingMotionPathOriginal = null;
        draggingSpatialObject = null;
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    public void SetPathPoint(int index, Vector3d position)
    {
        var path = ActiveMotionPath();
        if (path is not null)
        {
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
        path.Keyframes[index] = path.Keyframes[index] with { PositionMetres = position };
        if (validate && FirstSceneError() is { } error)
        {
            path.Keyframes[index] = original;
            InvalidateVisual();
            SceneEditRejected?.Invoke($"Motion keyframe edit rejected · {error.Message}");
            PathPointSelected?.Invoke(this, index);
            return;
        }
        SelectedKeyframeIndex = index;
        InvalidateVisual();
        if (announce)
        {
            PathPointSelected?.Invoke(this, index);
            SceneEdited?.Invoke(this, $"Motion keyframe {index + 1} set to ({position.X:0.###}, {position.Y:0.###}, {position.Z:0.###}) m · scene is dirty");
        }
    }

    private void DrawGrid(DrawingContext context)
    {
        var horizon = Bounds.Height * 0.46;
        for (var x = -30; x <= 30; x += 2)
        {
            context.DrawLine(GridPen, Project(new Vector3d(x, 0, -12)), Project(new Vector3d(x, 0, 14)));
        }

        for (var z = -12; z <= 14; z += 2)
        {
            context.DrawLine(GridPen, Project(new Vector3d(-32, 0, z)), Project(new Vector3d(32, 0, z)));
        }

        context.DrawLine(AxisPen, new Point(0, horizon), new Point(Bounds.Width, horizon));
    }

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

    private void DrawReceiver(DrawingContext context, ReceiverRig receiver, Vector3d position, bool selected)
    {
        var point = Project(position);
        var halfSpacing = Math.Max(5, receiver.SpacingMetres * 28);
        var pen = selected ? SelectedObjectPen : new Pen(ReceiverBrush, 2);
        context.DrawLine(pen, new(point.X - halfSpacing, point.Y), new(point.X + halfSpacing, point.Y));
        context.DrawEllipse(ReceiverBrush, selected ? SelectedObjectPen : null, new(point.X - halfSpacing, point.Y), selected ? 5 : 4, selected ? 5 : 4);
        context.DrawEllipse(ReceiverBrush, selected ? SelectedObjectPen : null, new(point.X + halfSpacing, point.Y), selected ? 5 : 4, selected ? 5 : 4);
    }

    private Point Project(Vector3d point)
    {
        var scale = Math.Clamp(Bounds.Width / 78.0, 8, 18);
        var x = (Bounds.Width * 0.5) + ((point.X + (point.Z * 0.35)) * scale);
        var y = (Bounds.Height * 0.58) - (point.Y * scale) + (point.Z * scale * 0.24);
        return new(x, y);
    }

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

    private Vector3d InverseProject(Point point, double yMetres)
    {
        var scale = Math.Clamp(Bounds.Width / 78.0, 8, 18);
        var z = (point.Y - (Bounds.Height * 0.58) + (yMetres * scale)) / (scale * 0.24);
        var x = ((point.X - (Bounds.Width * 0.5)) / scale) - (z * 0.35);
        return new(x, yMetres, z);
    }

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
            Consider(SceneSelection.Receiver(receiver.Id), position);
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
        PathPointSelected?.Invoke(this, 0);
        SceneEdited?.Invoke(this, $"Gesture captured · {gesturePoints.Count} raw samples retained · {simplified.Count} editable keyframes");
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
