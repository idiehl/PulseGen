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
    private static readonly IBrush PathPointBrush = new SolidColorBrush(Color.Parse("#8CF0E2"));
    private static readonly IBrush EmitterBrush = new SolidColorBrush(Color.Parse("#F2B766"));
    private static readonly IBrush ReceiverBrush = new SolidColorBrush(Color.Parse("#A899DE"));
    private static readonly IBrush RoomBrush = new SolidColorBrush(Color.Parse("#121D27"));
    private static readonly Pen RoomPen = new(new SolidColorBrush(Color.Parse("#34465B")), 1.3);
    private double timeSeconds;
    private int? draggingKeyframeIndex;
    private bool gestureCapturing;
    private DateTimeOffset gestureStarted;
    private readonly List<MotionKeyframe> gesturePoints = [];

    public SceneDocument? Scene { get; set; }

    public bool IsGestureRecording { get; set; }

    public int SelectedKeyframeIndex { get; private set; }

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

        DrawRoom(context);
        DrawAcousticGeometry(context);
        foreach (var path in Scene.MotionPaths)
        {
            DrawPath(context, path);
        }

        foreach (var receiver in Scene.ReceiverRigs.Where(receiver => receiver.Enabled))
        {
            DrawReceiver(context, receiver);
        }

        foreach (var emitter in Scene.Emitters.Where(emitter => emitter.Enabled))
        {
            var path = Scene.MotionPaths.FirstOrDefault(candidate => candidate.Id == emitter.MotionPathId);
            var position = path is null ? emitter.Transform.PositionMetres : SceneKinematics.PositionAt(path, TimeSeconds);
            DrawEmitter(context, position);
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

        var path = Scene.MotionPaths.FirstOrDefault();
        if (path is not null)
        {
            var nearest = FindNearestPathPoint(path, point);
            if (nearest.Index >= 0 && nearest.Distance <= 14)
            {
                draggingKeyframeIndex = nearest.Index;
                SelectedKeyframeIndex = nearest.Index;
                PathPointSelected?.Invoke(this, nearest.Index);
                e.Pointer.Capture(this);
                e.Handled = true;
                return;
            }
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
            var path = Scene.MotionPaths[0];
            var existing = path.Keyframes[index];
            SetPathPoint(index, InverseProject(point, existing.PositionMetres.Y), announce: false);
            PathPointSelected?.Invoke(this, index);
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
            SceneEdited?.Invoke(this, $"Motion keyframe {draggingKeyframeIndex.Value + 1} moved in SI world coordinates · scene is dirty");
        }

        draggingKeyframeIndex = null;
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    public void SetPathPoint(int index, Vector3d position) => SetPathPoint(index, position, announce: true);

    private void SetPathPoint(int index, Vector3d position, bool announce)
    {
        if (Scene is null || Scene.MotionPaths.Count == 0 || index < 0 || index >= Scene.MotionPaths[0].Keyframes.Count ||
            !double.IsFinite(position.X) || !double.IsFinite(position.Y) || !double.IsFinite(position.Z))
        {
            return;
        }
        var path = Scene.MotionPaths[0];
        path.Keyframes[index] = path.Keyframes[index] with { PositionMetres = position };
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
        context.DrawGeometry(RoomBrush, RoomPen, shape);

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
            context.DrawLine(RoomPen, ceiling[index], ceiling[(index + 1) % ceiling.Length]);
            context.DrawLine(RoomPen, points[index], ceiling[index]);
        }
    }

    private void DrawAcousticGeometry(DrawingContext context)
    {
        if (Scene is null)
        {
            return;
        }
        foreach (var geometry in Scene.Geometry.Where(item => item.Kind is GeometryKind.Plane or GeometryKind.Box))
        {
            var material = Scene.Materials.FirstOrDefault(candidate => candidate.Id == geometry.MaterialId);
            var absorption = material?.Absorption.DefaultIfEmpty(0.2).Average() ?? 0.2;
            var scattering = material?.Scattering.DefaultIfEmpty(0.1).Average() ?? 0.1;
            var fill = new SolidColorBrush(Color.FromArgb(
                (byte)Math.Clamp(30 + (70 * (1 - absorption)), 20, 100),
                (byte)Math.Clamp(48 + (90 * scattering), 0, 255),
                (byte)Math.Clamp(112 + (80 * (1 - absorption)), 0, 255),
                (byte)Math.Clamp(126 + (80 * scattering), 0, 255)));
            var pen = new Pen(
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

        Point? previous = null;
        const int segments = 100;
        var start = path.Keyframes[0].TimeSeconds;
        var end = path.Keyframes[^1].TimeSeconds;
        for (var index = 0; index <= segments; index++)
        {
            var point = Project(SceneKinematics.PositionAt(path, start + ((end - start) * index / segments)));
            if (previous is not null)
            {
                context.DrawLine(PathPen, previous.Value, point);
            }
            previous = point;
        }

        for (var index = 0; index < path.Keyframes.Count; index++)
        {
            var keyframe = path.Keyframes[index];
            var radius = index == SelectedKeyframeIndex ? 6.0 : 3.5;
            context.DrawEllipse(index == SelectedKeyframeIndex ? EmitterBrush : PathPointBrush, null, Project(keyframe.PositionMetres), radius, radius);
        }

        if (gestureCapturing && gesturePoints.Count > 1)
        {
            for (var index = 1; index < gesturePoints.Count; index++)
            {
                context.DrawLine(new Pen(EmitterBrush, 2), Project(gesturePoints[index - 1].PositionMetres), Project(gesturePoints[index].PositionMetres));
            }
        }
    }

    private void DrawEmitter(DrawingContext context, Vector3d position)
    {
        var point = Project(position);
        context.DrawEllipse(new SolidColorBrush(Color.FromArgb(40, 242, 183, 102)), null, point, 20, 20);
        context.DrawEllipse(EmitterBrush, new Pen(Brushes.White, 1), point, 7, 7);
    }

    private void DrawReceiver(DrawingContext context, ReceiverRig receiver)
    {
        var point = Project(receiver.Transform.PositionMetres);
        var halfSpacing = Math.Max(5, receiver.SpacingMetres * 28);
        context.DrawLine(new Pen(ReceiverBrush, 2), new(point.X - halfSpacing, point.Y), new(point.X + halfSpacing, point.Y));
        context.DrawEllipse(ReceiverBrush, null, new(point.X - halfSpacing, point.Y), 4, 4);
        context.DrawEllipse(ReceiverBrush, null, new(point.X + halfSpacing, point.Y), 4, 4);
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

    private void CommitGesture()
    {
        if (Scene is null || Scene.MotionPaths.Count == 0 || gesturePoints.Count < 2)
        {
            return;
        }
        var simplified = SimplifyGesture(gesturePoints, 0.20);
        if (simplified.Count < 2)
        {
            simplified = [gesturePoints[0], gesturePoints[^1]];
        }
        var existing = Scene.MotionPaths[0];
        Scene.MotionPaths[0] = existing with
        {
            Kind = MotionPathKind.RecordedGesture,
            RawGesture = [.. gesturePoints],
            Keyframes = simplified
        };
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
