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
                ? new MainWindow()
                : new ViewportBenchmarkWindow(benchmark, desktop);
        }

        base.OnFrameworkInitializationCompleted();
    }
}
