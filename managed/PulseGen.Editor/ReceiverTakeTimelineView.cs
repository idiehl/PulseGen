using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace PulseGen.Editor;

public sealed class ReceiverTakeTimelineView : Control
{
    private static readonly IBrush BackgroundBrush = new SolidColorBrush(Color.Parse("#090D12"));
    private static readonly IBrush IncludedBrush = new SolidColorBrush(Color.Parse("#132A2A"));
    private static readonly IBrush ExcludedBrush = new SolidColorBrush(Color.Parse("#171B23"));
    private static readonly Pen GridPen = new(new SolidColorBrush(Color.Parse("#1E2C3A")), 1);
    private static readonly Pen LeftPen = new(new SolidColorBrush(Color.Parse("#58D8CB")), 1.15);
    private static readonly Pen RightPen = new(new SolidColorBrush(Color.Parse("#9A8FD3")), 1.15);
    private static readonly Pen MutedPen = new(new SolidColorBrush(Color.Parse("#46515E")), 1, new DashStyle([3, 3], 0));
    private static readonly Pen SelectedPen = new(new SolidColorBrush(Color.Parse("#F2B766")), 2);
    private IReadOnlyList<ReceiverTakeSession> sessions = [];
    private Guid? selectedTakeId;
    private double timelineDurationSeconds = 1;

    internal IReadOnlyList<ReceiverTakeSession> Sessions
    {
        get => sessions;
        set
        {
            sessions = value;
            InvalidateVisual();
        }
    }

    internal Guid? SelectedTakeId
    {
        get => selectedTakeId;
        set
        {
            selectedTakeId = value;
            InvalidateVisual();
        }
    }

    internal double TimelineDurationSeconds
    {
        get => timelineDurationSeconds;
        set
        {
            timelineDurationSeconds = Math.Max(value, 1.0e-6);
            InvalidateVisual();
        }
    }

    internal event Action<ReceiverTakeSession>? TakeSelected;

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        context.FillRectangle(BackgroundBrush, Bounds);
        if (Bounds.Width < 30 || Bounds.Height < 20)
        {
            return;
        }

        for (var division = 1; division < 8; division++)
        {
            var x = Bounds.Width * division / 8;
            context.DrawLine(GridPen, new Point(x, 0), new Point(x, Bounds.Height));
        }
        if (sessions.Count == 0)
        {
            return;
        }

        var laneHeight = Bounds.Height / sessions.Count;
        for (var index = 0; index < sessions.Count; index++)
        {
            var session = sessions[index];
            var top = index * laneHeight;
            var startX = TimeToX(session.Take.TimelineOffsetSeconds);
            var endX = TimeToX(session.ArrangedEndSeconds);
            var lane = new Rect(startX, top + 3, Math.Max(2, endX - startX), Math.Max(4, laneHeight - 6));
            context.FillRectangle(session.Take.IncludedInMaster ? IncludedBrush : ExcludedBrush, lane);
            context.DrawLine(GridPen, new Point(0, top + laneHeight), new Point(Bounds.Width, top + laneHeight));
            DrawEnvelope(context, session, session.LeftEnvelope, top, laneHeight / 2, session.Take.LeftMuted ? MutedPen : LeftPen);
            DrawEnvelope(context, session, session.RightEnvelope, top + laneHeight / 2, laneHeight / 2, session.Take.RightMuted ? MutedPen : RightPen);
            if (session.Take.Id == selectedTakeId)
            {
                context.DrawRectangle(SelectedPen, lane);
            }
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (sessions.Count == 0 || Bounds.Height <= 0)
        {
            return;
        }
        var point = e.GetPosition(this);
        var index = Math.Clamp((int)(point.Y / (Bounds.Height / sessions.Count)), 0, sessions.Count - 1);
        SelectedTakeId = sessions[index].Take.Id;
        TakeSelected?.Invoke(sessions[index]);
        e.Handled = true;
    }

    private void DrawEnvelope(
        DrawingContext context,
        ReceiverTakeSession session,
        IReadOnlyList<float> envelope,
        double top,
        double height,
        Pen pen)
    {
        if (envelope.Count < 2 || session.DurationSeconds <= 0)
        {
            return;
        }
        var trimEnd = session.EffectiveTrimEndSeconds;
        Point? previous = null;
        for (var index = 0; index < envelope.Count; index++)
        {
            var sourceTime = session.DurationSeconds * index / (envelope.Count - 1);
            if (sourceTime < session.Take.TrimStartSeconds || sourceTime > trimEnd)
            {
                continue;
            }
            var arrangedTime = session.Take.TimelineOffsetSeconds + sourceTime - session.Take.TrimStartSeconds;
            var x = TimeToX(arrangedTime);
            var amount = Math.Clamp(envelope[index], 0, 1);
            var y = top + (height / 2) - (amount * Math.Max(1, (height / 2) - 2));
            var point = new Point(x, y);
            if (previous is { } start)
            {
                context.DrawLine(pen, start, point);
            }
            previous = point;
        }
    }

    private double TimeToX(double seconds) => Math.Clamp(seconds / timelineDurationSeconds, 0, 1) * Bounds.Width;
}
