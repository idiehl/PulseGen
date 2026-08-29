using System.Text.Json;
using PulseGen.Contracts;
using PulseGen.Engine.Client;

var arguments = ParseArguments(args);
var scene = arguments.TryGetValue("project", out var projectDirectory)
    ? await ProjectStore.LoadAsync(projectDirectory)
    : SampleScenes.CreateFlyby();

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
        receipt.DeterministicSeed
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
