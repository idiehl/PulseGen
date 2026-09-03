using Avalonia;

namespace PulseGen.Editor;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        var packageSelfTest = PackageSelfTestOptions.TryParse(args);
        if (packageSelfTest is not null)
        {
            return PackageSelfTest.RunAsync(packageSelfTest).GetAwaiter().GetResult();
        }

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        return 0;
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}
