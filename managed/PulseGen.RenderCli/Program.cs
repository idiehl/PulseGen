using System.Text.Json;
using PulseGen.Contracts;
using PulseGen.Engine.Client;

var arguments = ParseArguments(args);
if (arguments.ContainsKey("project") && arguments.ContainsKey("sample"))
{
    throw new ArgumentException("Choose either --project or --sample, not both.");
}

var selectedSample = arguments.GetValueOrDefault("sample", "flyby");
var scene = arguments.TryGetValue("project", out var projectDirectory)
    ? await ProjectStore.LoadAsync(projectDirectory)
    : selectedSample.ToLowerInvariant() switch
    {
        "flyby" => SampleScenes.CreateFlyby(),
        "surface-echo" => SampleScenes.CreateSurfaceEcho(),
        "moving-receiver" => SampleScenes.CreateMovingReceiver(),
        "atmosphere-field" => SampleScenes.CreateAtmosphereField(),
        "realtime-stress" => SampleScenes.CreateRealtimeStress(),
        _ => throw new ArgumentException($"Unknown sample '{selectedSample}'. Expected flyby, surface-echo, moving-receiver, atmosphere-field, or realtime-stress.")
    };

if (arguments.TryGetValue("duration", out var durationText))
{
    var duration = double.Parse(durationText, System.Globalization.CultureInfo.InvariantCulture);
    if (duration is <= 0 or > 3600)
    {
        throw new ArgumentOutOfRangeException("duration", "Render duration must be in (0, 3600] seconds.");
    }
    scene = scene with { Timeline = scene.Timeline with { DurationSeconds = duration } };
}

if (arguments.TryGetValue("waveform", out var waveformText))
{
    var waveform = Enum.Parse<WaveformKind>(waveformText, ignoreCase: true);
    var emitter = scene.Emitters[0];
    var nodeIndex = emitter.SignalGraph.Nodes.FindIndex(node => node.Kind == SignalNodeKind.Oscillator);
    var graph = emitter.SignalGraph with { Nodes = [.. emitter.SignalGraph.Nodes] };
    graph.Nodes[nodeIndex] = graph.Nodes[nodeIndex] with { Waveform = waveform };
    var emitters = new List<Emitter>(scene.Emitters) { [0] = emitter with { SignalGraph = graph } };
    scene = scene with { Emitters = emitters };
}

if (arguments.TryGetValue("sample-format", out var sampleFormatText))
{
    var format = Enum.Parse<RenderSampleFormat>(sampleFormatText, ignoreCase: true);
    var profile = scene.RenderProfiles[0] with
    {
        SampleFormat = format,
        Dither = format == RenderSampleFormat.Float32 ? DitherMode.None : DitherMode.Tpdf
    };
    scene = scene with { RenderProfiles = [profile, .. scene.RenderProfiles.Skip(1)] };
}

if (arguments.TryGetValue("write-project", out var destination))
{
    var hash = await ProjectStore.SaveAsync(scene, destination);
    Console.WriteLine($"PULSEGEN_PROJECT_OK {Path.GetFullPath(destination)} sha256={hash}");
}

if (arguments.TryGetValue("preview-seconds", out var previewSecondsText))
{
    var previewSeconds = double.Parse(previewSecondsText, System.Globalization.CultureInfo.InvariantCulture);
    if (!double.IsFinite(previewSeconds) || previewSeconds is <= 0 or > 30 || previewSeconds > scene.Timeline.DurationSeconds)
    {
        throw new ArgumentOutOfRangeException("preview-seconds", "Preview diagnostics must run for (0, 30] seconds without exceeding the scene timeline.");
    }
    var receiverIndex = arguments.TryGetValue("receiver-index", out var receiverIndexText)
        ? uint.Parse(receiverIndexText, System.Globalization.CultureInfo.InvariantCulture)
        : 0U;
    var enabledReceiverCount = scene.ReceiverRigs.Count(receiver => receiver.Enabled);
    if (receiverIndex >= scene.ReceiverRigs.Count || !scene.ReceiverRigs[(int)receiverIndex].Enabled)
    {
        throw new ArgumentOutOfRangeException(
            "receiver-index",
            $"Receiver index {receiverIndex} does not identify an enabled rig; the scene has {enabledReceiverCount} enabled receiver rigs.");
    }

    var profile = scene.RenderProfiles[0];
    var silentScene = scene with
    {
        Emitters = scene.Emitters.Select(emitter => emitter with { GainLinear = 0 }).ToList()
    };
    using var preview = NativeSceneRenderer.StartPreview(
        silentScene,
        receiverIndex,
        (uint)profile.SampleRate,
        (uint)profile.BufferFrames,
        (uint)profile.ReflectionOrder,
        (uint)profile.RayCount,
        profile.ImpulseResponseSeconds,
        (uint)profile.Seed,
        projectDirectory: arguments.ContainsKey("project") ? projectDirectory : null);
    await Task.Delay(TimeSpan.FromSeconds(previewSeconds));
    var status = preview.Status;
    var clean = status.Running && status.CallbackCount > 0 && status.CallbackDeadlineMissCount == 0;
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        marker = clean ? "PULSEGEN_PREVIEW_DIAGNOSTICS_OK" : "PULSEGEN_PREVIEW_DIAGNOSTICS_FAILED",
        engine = NativeSceneRenderer.EngineVersion,
        scene = scene.Name,
        muted = true,
        previewSeconds,
        receiverIndex,
        emitterCount = scene.Emitters.Count,
        receiverCount = enabledReceiverCount,
        status.Running,
        status.SampleRate,
        status.PeriodFrames,
        status.FramesProcessed,
        status.CallbackCount,
        status.CallbackDurationMillisecondsAverage,
        status.CallbackDurationMillisecondsMaximum,
        status.CallbackLoadPercentMaximum,
        status.CallbackDeadlineMissCount,
        status.CallbackFramesMaximum
    }));
    if (arguments.ContainsKey("require-clean-audio") && !clean)
    {
        throw new InvalidOperationException(
            $"The preview ended with running={status.Running}, callbacks={status.CallbackCount}, and {status.CallbackDeadlineMissCount} audio render deadline misses.");
    }
}

if (!arguments.ContainsKey("no-render"))
{
    var defaultOutputName = arguments.ContainsKey("project") ? "project" : selectedSample.ToLowerInvariant();
    var output = arguments.GetValueOrDefault("output", Path.Combine("out", "renders", defaultOutputName));
    var receipt = NativeSceneRenderer.Render(
        scene,
        output,
        projectDirectory: arguments.ContainsKey("project") ? projectDirectory : null);
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        marker = "PULSEGEN_RENDER_OK",
        engine = NativeSceneRenderer.EngineVersion,
        output = Path.GetFullPath(output),
        receipt.FilesWritten,
        receipt.FramesRendered,
        receipt.SampleRate,
        receipt.PeakLeft,
        receipt.PeakRight,
        receipt.RmsLeft,
        receipt.RmsRight,
        receipt.SpeedOfSoundMetresPerSecond,
        receipt.DeterministicSeed,
        receipt.RenderReceiptPath
    }));
}

static Dictionary<string, string> ParseArguments(string[] values)
{
    var parsed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    for (var index = 0; index < values.Length; index++)
    {
        var value = values[index];
        if (!value.StartsWith("--", StringComparison.Ordinal))
        {
            throw new ArgumentException($"Unexpected argument: {value}");
        }

        var key = value[2..];
        if (index + 1 < values.Length && !values[index + 1].StartsWith("--", StringComparison.Ordinal))
        {
            parsed[key] = values[++index];
        }
        else
        {
            parsed[key] = "true";
        }
    }
    return parsed;
}
