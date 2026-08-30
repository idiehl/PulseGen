using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace PulseGen.Editor;

public sealed class SpectralTrackView : Control
{
    private static readonly IBrush BackgroundBrush = new SolidColorBrush(Color.Parse("#090D12"));
    private static readonly Pen GridPen = new(new SolidColorBrush(Color.Parse("#1E2C3A")), 1);
    private static readonly Pen LeftPen = new(new SolidColorBrush(Color.Parse("#58D8CB")), 1.15);
    private static readonly Pen RightPen = new(new SolidColorBrush(Color.Parse("#9A8FD3")), 1.15);
    private static readonly Pen MutedPen = new(new SolidColorBrush(Color.Parse("#3D4958")), 0.9, new DashStyle([3, 3], 0));
    private static readonly Pen SelectedPen = new(new SolidColorBrush(Color.Parse("#F2B766")), 2.5);
    private IReadOnlyList<SpectralPartialTrack> tracks = [];
    private IReadOnlyDictionary<string, SpectralTrackEdit> edits = new Dictionary<string, SpectralTrackEdit>();
    private string? selectedTrackId;
    private double durationSeconds = 1;
    private int sampleRate = 48000;

    internal IReadOnlyList<SpectralPartialTrack> Tracks
    {
        get => tracks;
        set
        {
            tracks = value;
            InvalidateVisual();
        }
    }

    internal IReadOnlyDictionary<string, SpectralTrackEdit> Edits
    {
        get => edits;
        set
        {
            edits = value;
            InvalidateVisual();
        }
    }

    internal string? SelectedTrackId
    {
        get => selectedTrackId;
        set
        {
            selectedTrackId = value;
            InvalidateVisual();
        }
    }

    internal double DurationSeconds
    {
        get => durationSeconds;
        set
        {
            durationSeconds = Math.Max(value, 1.0e-6);
            InvalidateVisual();
        }
    }

    internal int SampleRate
    {
        get => sampleRate;
        set
        {
            sampleRate = Math.Max(value, 8000);
            InvalidateVisual();
        }
    }

    internal event Action<SpectralPartialTrack>? TrackSelected;

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        context.FillRectangle(BackgroundBrush, Bounds);
        if (Bounds.Width < 20 || Bounds.Height < 20)
        {
            return;
        }

        for (var division = 1; division < 8; division++)
        {
            var x = Bounds.Width * division / 8;
            context.DrawLine(GridPen, new Point(x, 0), new Point(x, Bounds.Height));
        }
        foreach (var frequency in new[] { 50.0, 100, 250, 500, 1000, 2000, 5000, 10000 })
        {
            if (frequency < sampleRate / 2.0)
            {
                var y = FrequencyToY(frequency);
                context.DrawLine(GridPen, new Point(0, y), new Point(Bounds.Width, y));
            }
        }

        foreach (var track in VisibleTracks().Where(track => track.Id != selectedTrackId))
        {
            DrawTrack(context, track, ResolvePen(track));
        }
        var selected = tracks.FirstOrDefault(track => track.Id == selectedTrackId);
        if (selected is not null)
        {
            DrawTrack(context, selected, SelectedPen);
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var point = e.GetPosition(this);
        SpectralPartialTrack? nearest = null;
        var nearestDistance = 12.0;
        foreach (var track in VisibleTracks())
        {
            var points = SampledPoints(track);
            for (var index = 1; index < points.Count; index++)
            {
                var distance = DistanceToSegment(point, points[index - 1], points[index]);
                if (distance < nearestDistance)
                {
                    nearestDistance = distance;
                    nearest = track;
                }
            }
        }
        if (nearest is not null)
        {
            SelectedTrackId = nearest.Id;
            TrackSelected?.Invoke(nearest);
            e.Handled = true;
        }
    }

    private IReadOnlyList<SpectralPartialTrack> VisibleTracks() => tracks.Count <= 512
        ? tracks
        : tracks.OrderByDescending(track => track.PeakAmplitude).Take(512).ToArray();

    private Pen ResolvePen(SpectralPartialTrack track) =>
        edits.TryGetValue(track.Id, out var edit) && !edit.Enabled
            ? MutedPen
            : track.Channel == 0 ? LeftPen : RightPen;

    private void DrawTrack(DrawingContext context, SpectralPartialTrack track, Pen pen)
    {
        var points = SampledPoints(track);
        for (var index = 1; index < points.Count; index++)
        {
            context.DrawLine(pen, points[index - 1], points[index]);
        }
    }

    private List<Point> SampledPoints(SpectralPartialTrack track)
    {
        var maximumPoints = Math.Max(2, (int)Bounds.Width / 2);
        var step = Math.Max(1, track.Points.Count / maximumPoints);
        var result = new List<Point>(Math.Min(track.Points.Count, maximumPoints + 1));
        for (var index = 0; index < track.Points.Count; index += step)
        {
            result.Add(Project(track.Points[index]));
        }
        if (track.Points.Count > 0 && (track.Points.Count - 1) % step != 0)
        {
            result.Add(Project(track.Points[^1]));
        }
        return result;
    }

    private Point Project(SpectralPartialPoint point) => new(
        Math.Clamp(point.TimeSeconds / durationSeconds, 0, 1) * Bounds.Width,
        FrequencyToY(point.FrequencyHz));

    private double FrequencyToY(double frequency)
    {
        const double minimum = 20;
        var maximum = Math.Max(minimum * 2, sampleRate / 2.0);
        var amount = (Math.Log(Math.Max(frequency, minimum)) - Math.Log(minimum)) /
                     (Math.Log(maximum) - Math.Log(minimum));
        return Bounds.Height * (1 - Math.Clamp(amount, 0, 1));
    }

    private static double DistanceToSegment(Point point, Point start, Point end)
    {
        var delta = end - start;
        var lengthSquared = (delta.X * delta.X) + (delta.Y * delta.Y);
        if (lengthSquared <= double.Epsilon)
        {
            return Math.Sqrt(((point.X - start.X) * (point.X - start.X)) + ((point.Y - start.Y) * (point.Y - start.Y)));
        }
        var amount = Math.Clamp((((point.X - start.X) * delta.X) + ((point.Y - start.Y) * delta.Y)) / lengthSquared, 0, 1);
        var nearest = start + (delta * amount);
        return Math.Sqrt(((point.X - nearest.X) * (point.X - nearest.X)) + ((point.Y - nearest.Y) * (point.Y - nearest.Y)));
    }
}
