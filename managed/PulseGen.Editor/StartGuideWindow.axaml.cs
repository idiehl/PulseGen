using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace PulseGen.Editor;

public sealed partial class StartGuideWindow : Window
{
    public StartGuideWindow()
        : this("current scene", showOnLaunch: true)
    {
    }

    internal StartGuideWindow(string currentSceneName, bool showOnLaunch)
    {
        InitializeComponent();
        CurrentSceneText.Text = currentSceneName;
        ShowOnLaunchCheck.IsChecked = showOnLaunch;
        ReadyFeatureList.ItemsSource = StartGuideCatalog.ReadyFeatures;
        GatedFeatureList.ItemsSource = StartGuideCatalog.GatedFeatures;
    }

    public bool ShowOnLaunch => ShowOnLaunchCheck.IsChecked != false;

    private void OnFlybyClicked(object? sender, RoutedEventArgs e) =>
        Close(new StartGuideResult(StartGuideAction.ExploreFlyby, ShowOnLaunch));

    private void OnEchoClicked(object? sender, RoutedEventArgs e) =>
        Close(new StartGuideResult(StartGuideAction.ExploreSurfaceEcho, ShowOnLaunch));

    private void OnOpenProjectClicked(object? sender, RoutedEventArgs e) =>
        Close(new StartGuideResult(StartGuideAction.OpenProject, ShowOnLaunch));

    private void OnContinueClicked(object? sender, RoutedEventArgs e) =>
        Close(new StartGuideResult(StartGuideAction.Continue, ShowOnLaunch));

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Close(new StartGuideResult(StartGuideAction.Continue, ShowOnLaunch));
        }
    }
}
