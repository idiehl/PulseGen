using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using PulseGen.Contracts;
using PulseGen.Engine.Client;
using PulseGen.ModelHost.Client;
using System.Globalization;

namespace PulseGen.Editor;

public sealed partial class MainWindow : Window
{
    private SceneDocument scene = SampleScenes.CreateFlyby();
    private readonly DispatcherTimer previewTimer;
    private DateTimeOffset previewStarted;
    private int selectedKeyframeIndex;
    private bool initializingControls = true;
    private string? lastRenderDirectory;
    private CancellationTokenSource? analysisCancellation;
    private NativeSceneRenderer.PreviewSession? audioPreview;

    public MainWindow()
    {
        InitializeComponent();
        Viewport.Scene = scene;
        Viewport.PlayheadChanged += (_, time) => UpdatePlayhead(time);
        Viewport.PathPointSelected += (_, index) => SelectKeyframe(index);
        Viewport.SceneEdited += (_, message) =>
        {
            StopPreviewTransport();
            SelectKeyframe(Viewport.SelectedKeyframeIndex);
            StatusText.Text = message;
            GestureButton.Content = "●  Record gesture";
        };
        previewTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Render, OnPreviewTick);
        previewTimer.Stop();
        WaveformBox.ItemsSource = new[] { "Sine", "Triangle", "Saw", "Square", "Pulse", "WhiteNoise", "PinkNoise", "BrownNoise", "Impulse", "Additive", "Turbulence" };
        WaveformBox.SelectedItem = "Sine";
        initializingControls = false;
        UpdatePlayhead(0);
        SelectKeyframe(0);
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
                startTimeSeconds: Viewport.TimeSeconds);
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
        Viewport.IsGestureRecording = !Viewport.IsGestureRecording;
        GestureButton.Content = Viewport.IsGestureRecording ? "●  Draw in world…" : "●  Record gesture";
        StatusText.Text = Viewport.IsGestureRecording
            ? "Gesture capture armed · drag across the 3D world; raw samples will be retained"
            : "Gesture capture cancelled";
    }

    private async void OnSaveClicked(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Choose a folder for the PhysicsFlyby.pulseproj project",
            AllowMultiple = false
        });
        if (folders.Count == 0)
        {
            return;
        }

        var projectDirectory = Path.Combine(folders[0].Path.LocalPath, "PhysicsFlyby.pulseproj");
        try
        {
            scene = scene with { UpdatedAtUtc = DateTimeOffset.UtcNow };
            Viewport.Scene = scene;
            var hash = await ProjectStore.SaveAsync(scene, projectDirectory);
            StatusText.Text = $"Project saved atomically · {hash[..12]}… · {projectDirectory}";
        }
        catch (Exception exception)
        {
            StatusText.Text = $"Project save failed: {exception.Message}";
        }
    }

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

        var emitter = scene.Emitters[0];
        var nodeIndex = emitter.SignalGraph.Nodes.FindIndex(node => node.Kind == SignalNodeKind.Oscillator);
        if (nodeIndex < 0)
        {
            return;
        }
        emitter.SignalGraph.Nodes[nodeIndex] = emitter.SignalGraph.Nodes[nodeIndex] with { Waveform = waveform };
        if (waveform == WaveformKind.Additive && !emitter.SignalGraph.Nodes[nodeIndex].NumericParameters.ContainsKey("partial.0.amplitude"))
        {
            var parameters = emitter.SignalGraph.Nodes[nodeIndex].NumericParameters;
            parameters["partial.0.ratio"] = 1;
            parameters["partial.0.amplitude"] = 1;
            parameters["partial.1.ratio"] = 2;
            parameters["partial.1.amplitude"] = 0.35;
            parameters["partial.2.ratio"] = 3;
            parameters["partial.2.amplitude"] = 0.18;
        }
        SignalNodeTitle.Text = selected.ToUpperInvariant();
        StatusText.Text = $"Primitive changed to {selected} · scene is dirty";
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

        var emitter = scene.Emitters[0];
        var node = emitter.SignalGraph.Nodes.First(item => item.Kind == SignalNodeKind.Oscillator);
        node.NumericParameters["frequencyHz"] = frequency;
        scene.Emitters[0] = emitter with { GainLinear = gain };
        FrequencyBox.Text = frequency.ToString("0.###", CultureInfo.InvariantCulture);
        GainBox.Text = gain.ToString("0.###", CultureInfo.InvariantCulture);
        SignalFrequencyText.Text = $"{frequency:0.00} Hz";
        StatusText.Text = "Primitive parameters committed · scene is dirty";
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
            $"Flyby-{DateTime.UtcNow:yyyyMMdd-HHmmss}");

        StatusText.Text = "Rendering isolated receiver takes…";
        try
        {
            var receipt = await Task.Run(() => NativeSceneRenderer.Render(scene, output));
            lastRenderDirectory = output;
            StatusText.Text = $"Rendered {receipt.FilesWritten} stereo takes · peak {Math.Max(receipt.PeakLeft, receipt.PeakRight):0.000} · {output}";
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
            AnalyzeButton.Content = "⌁  Analyze take";
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
        base.OnClosed(e);
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

    private void SelectKeyframe(int index)
    {
        var path = scene.MotionPaths[0];
        if (path.Keyframes.Count == 0)
        {
            return;
        }
        selectedKeyframeIndex = Math.Clamp(index, 0, path.Keyframes.Count - 1);
        var keyframe = path.Keyframes[selectedKeyframeIndex];
        SelectedPointText.Text = $"KEYFRAME {selectedKeyframeIndex + 1} · t {keyframe.TimeSeconds:0.000} s";
        KeyframeX.Text = keyframe.PositionMetres.X.ToString("0.000", CultureInfo.InvariantCulture);
        KeyframeY.Text = keyframe.PositionMetres.Y.ToString("0.000", CultureInfo.InvariantCulture);
        KeyframeZ.Text = keyframe.PositionMetres.Z.ToString("0.000", CultureInfo.InvariantCulture);
    }
}
