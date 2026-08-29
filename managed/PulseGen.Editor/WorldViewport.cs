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

    public SceneDocument? Scene { get; set; }

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
        var time = Math.Clamp(point.X / Math.Max(1, Bounds.Width), 0, 1) * Scene.Timeline.DurationSeconds;
        PlayheadChanged?.Invoke(this, time);
        e.Handled = true;
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

        var halfX = geometry.DimensionsMetres.X * 0.5;
        var halfZ = geometry.DimensionsMetres.Z * 0.5;
        var points = new[]
        {
            Project(new(-halfX, 0, -halfZ)), Project(new(halfX, 0, -halfZ)),
            Project(new(halfX, 0, halfZ)), Project(new(-halfX, 0, halfZ))
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

        foreach (var keyframe in path.Keyframes)
        {
            context.DrawEllipse(PathPointBrush, null, Project(keyframe.PositionMetres), 3.5, 3.5);
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
}
