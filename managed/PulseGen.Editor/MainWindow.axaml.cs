using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using PulseGen.Contracts;
using PulseGen.Engine.Client;

namespace PulseGen.Editor;

public sealed partial class MainWindow : Window
{
    private readonly SceneDocument scene = SampleScenes.CreateFlyby();
    private readonly DispatcherTimer previewTimer;
    private DateTimeOffset previewStarted;

    public MainWindow()
    {
        InitializeComponent();
        Viewport.Scene = scene;
        Viewport.PlayheadChanged += (_, time) => UpdatePlayhead(time);
        previewTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Render, OnPreviewTick);
        UpdatePlayhead(0);
    }

    private void OnPlayClicked(object? sender, RoutedEventArgs e)
    {
        previewStarted = DateTimeOffset.UtcNow - TimeSpan.FromSeconds(Viewport.TimeSeconds);
        previewTimer.Start();
        PlayButton.Content = "▶  Playing";
        StatusText.Text = "Real-time kinematic preview · 48 kHz profile";
    }

    private void OnStopClicked(object? sender, RoutedEventArgs e)
    {
        previewTimer.Stop();
        PlayButton.Content = "▶  Preview";
        StatusText.Text = "Stopped · scene state preserved";
    }

    private async void OnRenderClicked(object? sender, RoutedEventArgs e)
    {
        previewTimer.Stop();
        var output = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "PulseGen",
            "Renders",
            $"Flyby-{DateTime.UtcNow:yyyyMMdd-HHmmss}");

        StatusText.Text = "Rendering isolated receiver takes…";
        try
        {
            var receipt = await Task.Run(() => NativeSceneRenderer.Render(scene, output));
            StatusText.Text = $"Rendered {receipt.FilesWritten} stereo takes · peak {Math.Max(receipt.PeakLeft, receipt.PeakRight):0.000} · {output}";
        }
        catch (Exception exception)
        {
            StatusText.Text = $"Render unavailable: {exception.Message}";
        }
    }

    private void OnPreviewTick(object? sender, EventArgs e)
    {
        var elapsed = (DateTimeOffset.UtcNow - previewStarted).TotalSeconds;
        if (elapsed >= scene.Timeline.DurationSeconds)
        {
            elapsed = 0;
            previewStarted = DateTimeOffset.UtcNow;
        }

        UpdatePlayhead(elapsed);
    }

    private void UpdatePlayhead(double timeSeconds)
    {
        var duration = scene.Timeline.DurationSeconds;
        var time = Math.Clamp(timeSeconds, 0, duration);
        Viewport.TimeSeconds = time;
        var position = SceneKinematics.PositionAt(scene.MotionPaths[0], time);
        var velocity = SceneKinematics.VelocityAt(scene.MotionPaths[0], time);
        PositionText.Text = $"t  {time:0.000} s";
        PositionX.Text = $"{position.X,8:0.000} m";
        PositionY.Text = $"{position.Y,8:0.000} m";
        PositionZ.Text = $"{position.Z,8:0.000} m";
        VelocityText.Text = $"{velocity.Length:0.00} m/s";
        var timelineWidth = (Playhead.Parent as Control)?.Bounds.Width ?? 0;
        Canvas.SetLeft(Playhead, Math.Max(0, timelineWidth * (time / duration)));
    }
}
