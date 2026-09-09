using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using PulseGen.Contracts;
using PulseGen.Engine.Client;

namespace PulseGen.Editor;

public sealed partial class MainWindow
{
    private bool SoundImportMustStop => closingInProgress || closeFinalized || recoveryCancellation.IsCancellationRequested;

    private async void OnImportSoundClicked(object? sender, RoutedEventArgs e)
    {
        if (SoundImportMustStop) return;
        if (lastProjectDirectory is not { } projectDirectory)
        {
            StatusText.Text = "Choose Save to create a project folder, then Import sound to keep the recording with your project";
            return;
        }
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose a mono or stereo recording", AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("Audio recordings")
            { Patterns = ["*.wav", "*.wave", "*.aif", "*.aiff", "*.flac", "*.mp3", "*.ogg", "*.opus"] }]
        });
        if (files.Count == 0 || SoundImportMustStop) return;

        StopPreviewTransport();
        IsEnabled = false;
        try
        {
            var sourcePath = files[0].Path.LocalPath;
            StatusText.Text = "Inspecting a temporary copy of the recording…";
            var token = recoveryCancellation.Token;
            using var prepared = await PreparedAudioImport.CreateAsync(sourcePath, token);
            if (SoundImportMustStop) return;
            var asset = prepared.Asset;
            var path = SceneSelectionResolver.MotionPath(scene, currentSelection);
            IsEnabled = true;
            var choice = await new SampleImportWindow(asset, path?.Name).ShowDialog<SampleImportChoice?>(this);
            if (SoundImportMustStop) return;
            if (choice is null)
            {
                StatusText.Text = "Sound import cancelled · no source was added";
                return;
            }
            var imported = SampleSourceAuthoring.Add(scene, asset, choice, path?.Id);
            IsEnabled = false;
            StatusText.Text = "Adding the confirmed recording to the project…";
            var stored = await ProjectStore.ImportAssetAsync(projectDirectory, prepared.FilePath, AssetKind.Audio, asset.MediaType,
                provenance: asset.Provenance, cancellationToken: token);
            // Closing snapshots the current scene before awaiting recovery persistence.
            // Never publish a later import into a scene that shutdown has already captured.
            if (SoundImportMustStop) return;
            if (stored.Sha256 != asset.Sha256 || stored.SizeBytes != asset.SizeBytes)
                throw new InvalidDataException("The prepared recording changed before import.");
            scene = imported.Scene;
            currentSelection = SceneSelection.Emitter(imported.SelectedEmitterId);
            RefreshSourceSceneSummary();
            MarkSceneEdited($"Added {asset.FileName} · {SampleSourceAuthoring.MappingName(scene.Emitters.Single(item => item.Id == imported.SelectedEmitterId))} · choose Play to listen");
            RefreshSceneTree();
            PopulateInspector(currentSelection);
        }
        catch (OperationCanceledException) when (recoveryCancellation.IsCancellationRequested) { }
        catch (Exception exception)
        {
            StatusText.Text = $"Sound import failed: {exception.Message}";
        }
        finally { if (!SoundImportMustStop) IsEnabled = true; }
    }

    private void RefreshSourceSceneSummary()
    {
        EmitterCountText.Text = $"{scene.Emitters.Count} sources";
        DurationSummaryText.Text = $"{scene.Timeline.DurationSeconds:0.000} s";
        SceneTimeline.Scene = scene;
        UpdatePlayhead(Viewport.TimeSeconds);
    }

    private void RefreshSampleSourceInspector(Emitter emitter)
    {
        SourceEnabledCheck.IsChecked = emitter.Enabled;
        SourceMappingText.Text = SampleSourceAuthoring.MappingName(emitter);
        var bed = emitter.ChannelMapping == SourceChannelMapping.NonSpatialStereoBed;
        SourcePropagationText.Text = bed ? "Bypassed" : "Retarded";
        var sample = emitter.SignalGraph.Nodes.FirstOrDefault(node => node.SamplePlayback is not null)?.SamplePlayback;
        SignalOutputTitle.Text = bed ? "STEREO BED" : "POINT EMITTER";
        SignalOutputChannelsText.Text = bed ? "L / R direct" : "mono out";
        SignalPlaybackText.Text = sample is null ? "phase continuous" : sample.Loop ? "whole-file loop" : "one-shot playback";
        SamplePlaybackPanel.IsVisible = sample is not null;
        TimelineSourceText.Text = $"{emitter.Name} · {(emitter.Enabled ? "included" : "muted")}";
        if (sample is null) return;
        WaveformBox.SelectedItem = null;
        WaveformBox.IsEnabled = false;
        FrequencyBox.Text = "—";
        FrequencyBox.IsEnabled = false;
        var asset = scene.Assets.Single(item => string.Equals(item.Sha256, sample.AssetSha256, StringComparison.OrdinalIgnoreCase));
        SignalNodeTitle.Text = "AUDIO FILE";
        SignalFrequencyText.Text = $"{asset.SampleRate / 1000.0:0.###} kHz";
        SampleAssetText.Text = $"{asset.FileName}\n{asset.Channels} channel{(asset.Channels == 1 ? "" : "s")} · {asset.SampleRate:N0} Hz · {asset.DurationSeconds:0.###} s";
        SampleStartBox.Text = FormatNumber(sample.StartTimeSeconds);
        SampleOffsetBox.Text = FormatNumber(sample.OffsetSeconds);
        SampleLoopCheck.IsChecked = sample.Loop;
        SampleTimingText.Text = emitter.ChannelMapping == SourceChannelMapping.LinkedDualEmitters
            ? "Timing changes apply to both linked channels. Their paths, volume, and mute controls remain independent."
            : bed ? "Stereo background bypasses all source motion and room acoustics."
            : "Playback timing is independent of the motion path. The source holds its last position when the path ends.";
    }

    private void OnSourceEnabledChanged(object? sender, RoutedEventArgs e)
    {
        if (initializingControls || SceneSelectionResolver.Emitter(scene, currentSelection) is not { } source) return;
        var replacement = source with { Enabled = SourceEnabledCheck.IsChecked == true };
        if (!SceneAuthoringOperations.TryReplaceEmitter(scene, source.Id, replacement, out var error))
            RejectSceneEdit("Source inclusion", error);
        else CommitSceneEdit($"{source.Name} {(replacement.Enabled ? "included" : "muted")}");
        PopulateInspector(currentSelection);
    }

    private void CommitSampleGain(Emitter source)
    {
        if (!TryReadNumber(GainBox, out var gain) || gain is < 0 or > 16)
        {
            StatusText.Text = "Source volume must be a finite number from 0 to 16";
            PopulateInspector(currentSelection);
            return;
        }
        if (!SceneAuthoringOperations.TryReplaceEmitter(scene, source.Id, source with { GainLinear = gain }, out var error))
            RejectSceneEdit("Source volume", error);
        else CommitSceneEdit($"{source.Name} volume");
        PopulateInspector(currentSelection);
    }

    private void OnApplySamplePlaybackClicked(object? sender, RoutedEventArgs e)
    {
        if (SceneSelectionResolver.Emitter(scene, currentSelection) is not { } source) return;
        try
        {
            if (!TryReadNumber(SampleStartBox, out var start) || !TryReadNumber(SampleOffsetBox, out var offset))
                throw new InvalidDataException("Start and file offset must be finite seconds.");
            scene = SampleSourceAuthoring.UpdatePlayback(scene, source.Id, start, offset, SampleLoopCheck.IsChecked == true);
            CommitSceneEdit($"{source.Name} playback timing · scene duration remains {scene.Timeline.DurationSeconds:0.###} s");
        }
        catch (Exception exception) { StatusText.Text = $"Playback settings rejected: {exception.Message}"; }
        PopulateInspector(currentSelection);
    }
}
