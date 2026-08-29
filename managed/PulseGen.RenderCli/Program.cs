using System.Text.Json;
using PulseGen.Contracts;
using PulseGen.Engine.Client;

var arguments = ParseArguments(args);
var scene = arguments.TryGetValue("project", out var projectDirectory)
    ? await ProjectStore.LoadAsync(projectDirectory)
    : SampleScenes.CreateFlyby();

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

if (!arguments.ContainsKey("no-render"))
{
    var output = arguments.GetValueOrDefault("output", Path.Combine("out", "renders", "flyby"));
    var receipt = NativeSceneRenderer.Render(scene, output);
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
