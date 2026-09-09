using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using PulseGen.Contracts;

namespace PulseGen.Editor;

/// <summary>A time-only interface. Scrubbing never edits the scene or any spatial point.</summary>
public sealed class SceneTimelineControl : Control
{
    private const string Help = "Click or drag here to move through time. Home: start; End: finish; " +
                                "Left/Right: 0.1 second; Shift+Left/Right: 1 second. This does not move objects.";
    private static readonly IBrush BackgroundBrush = new SolidColorBrush(Color.Parse("#0B1118"));
    private static readonly IBrush TrackBrush = new SolidColorBrush(Color.Parse("#1A2936"));
    private static readonly IBrush ElapsedBrush = new SolidColorBrush(Color.Parse("#245951"));
    private static readonly IBrush TextBrush = new SolidColorBrush(Color.Parse("#A8BACB"));
    private static readonly IBrush PlayheadBrush = new SolidColorBrush(Color.Parse("#77E2D3"));
    private static readonly Pen GridPen = new(new SolidColorBrush(Color.Parse("#304351")), 1);
    private static readonly Pen PlayheadPen = new(PlayheadBrush, 2);
    private static readonly Pen FocusPen = new(PlayheadBrush, 1, new DashStyle([3, 3], 0));
    private SceneDocument? scene;
    private double timeSeconds;
    private IPointer? capturedPointer;

    public SceneTimelineControl()
    {
        Focusable = true;
        MinHeight = 58;
        AutomationProperties.SetName(this, "Scene timeline, seconds");
        AutomationProperties.SetHelpText(this, Help);
        ToolTip.SetTip(this, Help);
    }

    public SceneDocument? Scene
    {
        get => scene;
        set
        {
            if (!ReferenceEquals(scene, value))
            {
                ReleaseSeek();
            }
            scene = value;
            TimeSeconds = timeSeconds;
            InvalidateVisual();
        }
    }

    public double TimeSeconds
    {
        get => timeSeconds;
        set
        {
            var previous = timeSeconds;
            timeSeconds = SceneTimelineLayout.ClampTime(value, DurationSeconds);
            if (previous != timeSeconds)
            {
                ControlAutomationPeer.FromElement(this)?.RaisePropertyChangedEvent(
                    RangeValuePatternIdentifiers.ValueProperty, previous, timeSeconds);
            }
            InvalidateVisual();
        }
    }

    internal double DurationSeconds => SceneTimelineLayout.ValidDuration(scene?.Timeline.DurationSeconds ?? 0);
    internal bool IsSeeking { get; private set; }

    public event EventHandler<double>? SeekRequested;
    public event EventHandler? SeekStarted;
    public event EventHandler? SeekCompleted;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsKeyboardFocusWithinProperty)
        {
            InvalidateVisual();
        }
        if (change.Property == IsEnabledProperty && !IsEnabled)
        {
            ReleaseSeek();
        }
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var size = Bounds.Size;
        if (!double.IsFinite(size.Width) || !double.IsFinite(size.Height) || size.Width <= 0 || size.Height <= 0)
        {
            return;
        }
        context.FillRectangle(BackgroundBrush, new Rect(size));
        if (size.Width <= SceneTimelineLayout.EdgePadding * 2 || size.Height < 24)
        {
            return;
        }

        var left = SceneTimelineLayout.TimeToX(0, DurationSeconds, size.Width);
        var right = SceneTimelineLayout.TimeToX(DurationSeconds, DurationSeconds, size.Width);
        var trackTop = Math.Min(31, size.Height - 12);
        context.FillRectangle(TrackBrush, new Rect(left, trackTop, Math.Max(0, right - left), 8));
        var playhead = SceneTimelineLayout.TimeToX(timeSeconds, DurationSeconds, size.Width);
        context.FillRectangle(ElapsedBrush, new Rect(left, trackTop, Math.Max(0, playhead - left), 8));

        foreach (var tick in SceneTimelineLayout.CreateTicks(DurationSeconds, size.Width))
        {
            var x = SceneTimelineLayout.TimeToX(tick.TimeSeconds, DurationSeconds, size.Width);
            context.DrawLine(GridPen, new Point(x, trackTop - 5), new Point(x, size.Height - 7));
            var label = new FormattedText(tick.Label, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface("Segoe UI"), 11, TextBrush);
            var labelX = Math.Clamp(x - label.Width / 2, 2, Math.Max(2, size.Width - label.Width - 2));
            context.DrawText(label, new Point(labelX, 3));
        }

        if (DurationSeconds > 0)
        {
            context.DrawLine(PlayheadPen, new Point(playhead, trackTop - 6), new Point(playhead, size.Height - 4));
            var marker = new StreamGeometry();
            using (var drawing = marker.Open())
            {
                drawing.BeginFigure(new Point(playhead - 6, trackTop - 9), true);
                drawing.LineTo(new Point(playhead + 6, trackTop - 9));
                drawing.LineTo(new Point(playhead, trackTop - 2));
                drawing.EndFigure(true);
            }
            context.DrawGeometry(PlayheadBrush, null, marker);
        }
        if (IsKeyboardFocusWithin)
        {
            context.DrawRectangle(FocusPen, new Rect(2, 2, Math.Max(0, size.Width - 4), Math.Max(0, size.Height - 4)));
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || IsSeeking ||
            !BeginSeekAt(e.GetPosition(this).X, Bounds.Width))
        {
            return;
        }
        Focus();
        capturedPointer = e.Pointer;
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (!IsSeeking || !ReferenceEquals(capturedPointer, e.Pointer))
        {
            return;
        }
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            ContinueSeekAt(e.GetPosition(this).X, Bounds.Width);
        }
        else
        {
            ReleaseSeek();
        }
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (IsSeeking && ReferenceEquals(capturedPointer, e.Pointer) && e.InitialPressMouseButton == MouseButton.Left)
        {
            ContinueSeekAt(e.GetPosition(this).X, Bounds.Width);
            ReleaseSeek();
            e.Handled = true;
        }
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        capturedPointer = null;
        CompleteSeek();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        ReleaseSeek();
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (SeekByKey(e.Key, e.KeyModifiers))
        {
            e.Handled = true;
        }
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new TimelineAutomationPeer(this);

    internal bool BeginSeekAt(double x, double width)
    {
        if (!IsEffectivelyEnabled || DurationSeconds <= 0 || !double.IsFinite(width) ||
            width <= SceneTimelineLayout.EdgePadding * 2 || !double.IsFinite(x) || IsSeeking)
        {
            return false;
        }
        IsSeeking = true;
        SeekStarted?.Invoke(this, EventArgs.Empty);
        ContinueSeekAt(x, width);
        return true;
    }

    internal void ContinueSeekAt(double x, double width)
    {
        if (IsSeeking && double.IsFinite(x))
        {
            RequestTime(SceneTimelineLayout.XToTime(x, DurationSeconds, width));
        }
    }

    internal void CompleteSeek()
    {
        if (!IsSeeking)
        {
            return;
        }
        IsSeeking = false;
        SeekCompleted?.Invoke(this, EventArgs.Empty);
    }

    internal bool SeekByKey(Key key, KeyModifiers modifiers)
    {
        if (!IsEffectivelyEnabled || DurationSeconds <= 0 || IsSeeking ||
            !SceneTimelineLayout.TrySeekKey(key, modifiers, TimeSeconds, DurationSeconds, out var target))
        {
            return false;
        }
        SeekSingle(target);
        return true;
    }

    private void SeekSingle(double target)
    {
        SeekStarted?.Invoke(this, EventArgs.Empty);
        try
        {
            RequestTime(target);
        }
        finally
        {
            SeekCompleted?.Invoke(this, EventArgs.Empty);
        }
    }

    private void RequestTime(double target)
    {
        TimeSeconds = target;
        SeekRequested?.Invoke(this, timeSeconds);
    }

    private void ReleaseSeek()
    {
        var pointer = capturedPointer;
        capturedPointer = null;
        CompleteSeek();
        pointer?.Capture(null);
    }

    private sealed class TimelineAutomationPeer(SceneTimelineControl owner) : ControlAutomationPeer(owner), IRangeValueProvider
    {
        protected override string GetClassNameCore() => nameof(SceneTimelineControl);
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Slider;
        public bool IsReadOnly => !owner.IsEffectivelyEnabled || owner.DurationSeconds <= 0;
        public double Minimum => 0;
        public double Maximum => owner.DurationSeconds;
        public double Value => owner.TimeSeconds;
        public double SmallChange => 0.1;
        public double LargeChange => 1;
        public void SetValue(double value)
        {
            if (!IsReadOnly && double.IsFinite(value))
            {
                owner.SeekSingle(value);
            }
        }
    }
}

internal readonly record struct SceneTimelineTick(double TimeSeconds, string Label);

internal static class SceneTimelineLayout
{
    internal const double EdgePadding = 16;

    internal static double ValidDuration(double duration) => double.IsFinite(duration) && duration > 0 ? duration : 0;

    internal static double ClampTime(double seconds, double duration) =>
        double.IsFinite(seconds) ? Math.Clamp(seconds, 0, ValidDuration(duration)) : 0;

    internal static double TimeToX(double seconds, double duration, double width)
    {
        var validWidth = double.IsFinite(width) ? Math.Max(0, width) : 0;
        var left = Math.Min(EdgePadding, validWidth / 2);
        return left + (ValidDuration(duration) > 0 ? ClampTime(seconds, duration) / duration : 0) * Math.Max(0, validWidth - left * 2);
    }

    internal static double XToTime(double x, double duration, double width)
    {
        if (!double.IsFinite(x) || !double.IsFinite(width) || width <= EdgePadding * 2)
        {
            return 0;
        }
        return Math.Clamp((x - EdgePadding) / (width - EdgePadding * 2), 0, 1) * ValidDuration(duration);
    }

    internal static IReadOnlyList<SceneTimelineTick> CreateTicks(double duration, double width)
    {
        duration = ValidDuration(duration);
        if (duration <= 0 || !double.IsFinite(width) || width <= EdgePadding * 2)
        {
            return [];
        }
        var usableWidth = width - EdgePadding * 2;
        var targetCount = Math.Clamp(Math.Floor(usableWidth / 76), 1, 100);
        var rawStep = Math.Max(1.0e-6, duration / targetCount);
        var magnitude = Math.Pow(10, Math.Floor(Math.Log10(rawStep)));
        var fraction = rawStep / magnitude;
        var step = (fraction <= 1 ? 1 : fraction <= 2 ? 2 : fraction <= 5 ? 5 : 10) * magnitude;
        if (!double.IsFinite(step) || step <= 0)
        {
            step = duration;
        }
        var result = new List<SceneTimelineTick>();
        for (var index = 0; index <= 100; index++)
        {
            var seconds = index * step;
            if (!double.IsFinite(seconds) || seconds > duration)
            {
                break;
            }
            result.Add(new(seconds, FormatTick(seconds, step)));
        }
        // Always show the exact endpoint, without crowding the preceding tick label.
        if (result.Count > 0 && result[^1].TimeSeconds != duration)
        {
            if (result.Count > 1 && (duration - result[^1].TimeSeconds) / duration * usableWidth < 52)
            {
                result.RemoveAt(result.Count - 1);
            }
            if (usableWidth >= 76)
            {
                result.Add(new(duration, FormatTick(duration, Math.Min(step, 0.001))));
            }
        }
        return result;
    }

    internal static bool TrySeekKey(Key key, KeyModifiers modifiers, double current, double duration, out double target)
    {
        target = ClampTime(current, duration);
        if ((modifiers & ~KeyModifiers.Shift) != 0)
        {
            return false;
        }
        var step = modifiers.HasFlag(KeyModifiers.Shift) ? 1.0 : 0.1;
        var requested = key switch
        {
            Key.Home => 0,
            Key.End => ValidDuration(duration),
            Key.Left => target - step,
            Key.Right => target + step,
            _ => double.NaN
        };
        if (!double.IsFinite(requested))
        {
            return false;
        }
        target = ClampTime(requested, duration);
        return true;
    }

    private static string FormatTick(double seconds, double step)
    {
        var decimals = Math.Clamp((int)-Math.Floor(Math.Log10(Math.Max(step, 1.0e-6))), 0, 6);
        return seconds >= 1.0e7
            ? seconds.ToString("0.##E+0", CultureInfo.InvariantCulture) + " s"
            : seconds.ToString(decimals == 0 ? "0" : "0." + new string('#', decimals), CultureInfo.InvariantCulture) + " s";
    }
}
