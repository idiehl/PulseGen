using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace PulseGen.Editor;

public sealed class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var benchmark = ViewportBenchmarkOptions.TryParse(desktop.Args ?? []);
            desktop.MainWindow = benchmark is null
                ? (desktop.Args?.Contains("--ui-review", StringComparer.Ordinal) == true
                    ? new MainWindow(Path.Combine(Path.GetTempPath(), "PulseGen-UI-review", Guid.NewGuid().ToString("N")))
                    : new MainWindow())
                : new ViewportBenchmarkWindow(benchmark, desktop);
        }

        base.OnFrameworkInitializationCompleted();
    }
}
