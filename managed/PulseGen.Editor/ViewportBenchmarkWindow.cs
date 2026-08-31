using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using PulseGen.Contracts;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace PulseGen.Editor;

internal sealed record ViewportBenchmarkOptions(
    double DurationSeconds,
    double MinimumFramesPerSecond,
    string ReceiptPath)
{
    public static ViewportBenchmarkOptions? TryParse(IReadOnlyList<string> arguments)
    {
        if (!arguments.Contains("--viewport-benchmark", StringComparer.Ordinal))
        {
            return null;
        }

        string? Value(string name)
        {
            var index = arguments.IndexOf(name);
            if (index < 0)
            {
                return null;
            }
            if (index + 1 >= arguments.Count || arguments[index + 1].StartsWith("--", StringComparison.Ordinal))
            {
                throw new ArgumentException($"{name} requires a value.", name);
            }
            return arguments[index + 1];
        }

        var duration = Value("--seconds") is { } secondsText
            ? double.Parse(secondsText, CultureInfo.InvariantCulture)
            : 6;
        var minimumFps = Value("--minimum-fps") is { } fpsText
            ? double.Parse(fpsText, CultureInfo.InvariantCulture)
            : 57;
        var receipt = Value("--receipt") ?? Path.Combine(
            Environment.CurrentDirectory,
            "out",
            "viewport-benchmark",
            "viewport-receipt.json");
        if (!double.IsFinite(duration) || duration is < 1 or > 30)
        {
            throw new ArgumentOutOfRangeException("--seconds", "Viewport benchmark duration must be between 1 and 30 seconds.");
        }
        if (!double.IsFinite(minimumFps) || minimumFps is < 1 or > 240)
        {
            throw new ArgumentOutOfRangeException("--minimum-fps", "Viewport benchmark FPS threshold must be between 1 and 240.");
        }
        return new(duration, minimumFps, Path.GetFullPath(receipt));
    }
}

internal sealed class ViewportBenchmarkWindow : Window
{
    private readonly ViewportBenchmarkOptions options;
    private readonly IClassicDesktopStyleApplicationLifetime lifetime;
    private readonly GpuWorldViewport viewport;
    private readonly TextBlock statusText;
    private readonly DispatcherTimer warmupTimer;
    private readonly DispatcherTimer measurementTimer;
    private readonly DispatcherTimer timeoutTimer;
    private readonly Stopwatch preparationStopwatch = Stopwatch.StartNew();
    private bool warmupStarted;
    private bool measuring;
    private bool completed;
    private double preparationMilliseconds;

    public ViewportBenchmarkWindow(
        ViewportBenchmarkOptions options,
        IClassicDesktopStyleApplicationLifetime lifetime)
    {
        this.options = options;
        this.lifetime = lifetime;
        Title = "PulseGen — 250K Triangle Viewport Acceptance";
        Width = 1280;
        Height = 720;
        MinWidth = 960;
        MinHeight = 540;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        viewport = new GpuWorldViewport
        {
            ContinuousRendering = true,
            Scene = SampleScenes.CreateViewportStress()
        };
        viewport.PerformanceUpdated += OnPerformanceUpdated;
        statusText = new TextBlock
        {
            Text = "Preparing deterministic 250,000-triangle terrain…",
            Foreground = Brushes.White,
            FontSize = 13
        };
        var badge = new Border
        {
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new(18),
            Padding = new(12, 8),
            CornerRadius = new(7),
            Background = new SolidColorBrush(Color.FromArgb(220, 13, 19, 27)),
            BorderBrush = new SolidColorBrush(Color.Parse("#314052")),
            BorderThickness = new(1),
            Child = statusText
        };
        Content = new Grid
        {
            Background = new SolidColorBrush(Color.Parse("#090D12")),
            Children = { viewport, badge }
        };

        warmupTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        warmupTimer.Tick += (_, _) => BeginMeasurement();
        measurementTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(options.DurationSeconds) };
        measurementTimer.Tick += (_, _) => CompleteBenchmark("Measurement complete.");
        timeoutTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(options.DurationSeconds + 15) };
        timeoutTimer.Tick += (_, _) => CompleteBenchmark("Timed out while waiting for a usable OpenGL viewport.");
        Opened += (_, _) => timeoutTimer.Start();
        Closed += (_, _) =>
        {
            warmupTimer.Stop();
            measurementTimer.Stop();
            timeoutTimer.Stop();
        };
    }

    private void OnPerformanceUpdated(ViewportPerformanceStatus status)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => OnPerformanceUpdated(status));
            return;
        }
        if (completed)
        {
            return;
        }
        if (status.Error is not null)
        {
            CompleteBenchmark(status.Error);
            return;
        }

        statusText.Text = status.TriangleCount == 250_000
            ? $"{status.FramesPerSecond:0.0} FPS · {status.TriangleCount:N0} triangles · CPU submit {status.CpuSubmitMillisecondsAverage:0.000} ms"
            : "Preparing deterministic 250,000-triangle terrain…";
        if (!warmupStarted && status.Ready && status.TriangleCount == 250_000 && status.FrameCount > 1)
        {
            preparationMilliseconds = preparationStopwatch.Elapsed.TotalMilliseconds;
            warmupStarted = true;
            warmupTimer.Start();
        }
    }

    private void BeginMeasurement()
    {
        warmupTimer.Stop();
        if (completed)
        {
            return;
        }
        viewport.ResetPerformanceMeasurement();
        measuring = true;
        measurementTimer.Start();
        statusText.Text = $"Measuring {options.DurationSeconds:0.#} seconds at the 60 FPS target…";
    }

    private void CompleteBenchmark(string completionMessage)
    {
        if (completed)
        {
            return;
        }
        completed = true;
        warmupTimer.Stop();
        measurementTimer.Stop();
        timeoutTimer.Stop();
        viewport.ContinuousRendering = false;
        var status = viewport.PerformanceStatus;
        var passed = measuring &&
            status.Ready &&
            status.Error is null &&
            status.TriangleCount == 250_000 &&
            status.FramesPerSecond >= options.MinimumFramesPerSecond &&
            status.CpuSubmitMillisecondsAverage < 4;
        var receipt = new
        {
            marker = passed ? "PULSEGEN_VIEWPORT_STRESS_OK" : "PULSEGEN_VIEWPORT_STRESS_FAILED",
            scene = "Viewport 250K Triangle Stress",
            status.Backend,
            status.TriangleCount,
            status.FrameCount,
            status.ElapsedSeconds,
            status.FramesPerSecond,
            minimumFramesPerSecond = options.MinimumFramesPerSecond,
            status.CpuSubmitMillisecondsAverage,
            status.CpuSubmitMillisecondsMaximum,
            status.FrameIntervalMillisecondsMaximum,
            status.SlowFrameCount,
            preparationMilliseconds,
            status.Error,
            completionMessage
        };
        var exitCode = passed ? 0 : 1;
        try
        {
            WriteReceipt(options.ReceiptPath, JsonSerializer.Serialize(receipt, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            exitCode = 1;
        }
        lifetime.Shutdown(exitCode);
    }

    private static void WriteReceipt(string destination, string json)
    {
        var directory = Path.GetDirectoryName(destination)
            ?? throw new InvalidOperationException("Viewport benchmark receipt requires a parent directory.");
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".{Path.GetFileName(destination)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temporary, json);
            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }
}

internal static class ArgumentListExtensions
{
    public static int IndexOf(this IReadOnlyList<string> values, string value)
    {
        for (var index = 0; index < values.Count; index++)
        {
            if (string.Equals(values[index], value, StringComparison.Ordinal))
            {
                return index;
            }
        }
        return -1;
    }
}
