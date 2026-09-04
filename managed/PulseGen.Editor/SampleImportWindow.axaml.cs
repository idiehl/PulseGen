using Avalonia.Controls;
using Avalonia.Interactivity;
using PulseGen.Contracts;

namespace PulseGen.Editor;

public sealed partial class SampleImportWindow : Window
{
    private sealed record MappingChoice(SourceChannelMapping Mapping, string Name, string Description)
    {
        public override string ToString() => Name;
    }

    public SampleImportWindow() => InitializeComponent();

    internal SampleImportWindow(AssetRef asset, string? pathName) : this()
    {
        SourceDetails.Text = $"{asset.FileName}\n{asset.Channels} channel{(asset.Channels == 1 ? "" : "s")} · {asset.SampleRate:N0} Hz · {asset.DurationSeconds:0.###} seconds";
        PathDescription.Text = pathName is null
            ? "Spatial sources begin two metres in front of the world origin. Their new paths are editable."
            : $"Spatial sources receive their own editable copy of ‘{pathName}’. A left/right pair begins 20 cm apart.";
        MappingBox.ItemsSource = asset.Channels == 1 ? new[]
        {
            new MappingChoice(SourceChannelMapping.MonoPoint, "One moving source", "The mono recording comes from one point. Its path, distance, air, and echoes shape what each listener receives.")
        } : new[]
        {
            new MappingChoice(SourceChannelMapping.LinkedDualEmitters, "Separate left and right sources", "Each channel becomes a separate point with its own editable path and volume. Playback timing stays linked. Both channels can reach both ears through the room."),
            new MappingChoice(SourceChannelMapping.NonSpatialStereoBed, "Stereo background", "Keep the original left and right channels in every receiver take. This layer bypasses distance, motion, head or microphone coloration, and echoes."),
            new MappingChoice(SourceChannelMapping.ExplicitMonoFoldDown, "Mix to one moving source", "Average left and right as (L + R) / 2, then move that mono sound through the room. Opposite-phase content can cancel in this mix.")
        };
        if (asset.Channels == 1) MappingBox.SelectedIndex = 0;
    }

    private void OnMappingChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (MappingBox.SelectedItem is not MappingChoice choice) return;
        MappingDescription.Text = choice.Description;
        AddButton.IsEnabled = true;
    }

    private void OnCancelClicked(object? sender, RoutedEventArgs e) => Close();

    private void OnAddClicked(object? sender, RoutedEventArgs e)
    {
        if (MappingBox.SelectedItem is MappingChoice choice)
            Close(new SampleImportChoice(choice.Mapping, MuteOthersCheck.IsChecked == true));
    }
}
