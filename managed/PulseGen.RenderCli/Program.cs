using System.Diagnostics;
using System.Security.Cryptography;
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

if (arguments.TryGetValue("reflection-monitor-queries", out var reflectionQueryCountText))
{
    if (!arguments.ContainsKey("project") || string.IsNullOrWhiteSpace(projectDirectory))
    {
        throw new ArgumentException("Reflection-monitor benchmarking requires --project so imported assets can be verified.");
    }
    var reflectionQueryCount = int.Parse(reflectionQueryCountText, System.Globalization.CultureInfo.InvariantCulture);
    if (reflectionQueryCount is < 2 or > 1000)
    {
        throw new ArgumentOutOfRangeException("reflection-monitor-queries", "Reflection-monitor query count must be between 2 and 1000.");
    }

    var benchmarkScene = arguments.ContainsKey("enable-imported-reflections")
        ? scene with
        {
            Geometry = scene.Geometry.Select(item => item.Kind == GeometryKind.ImportedMesh
                ? item with { Reflects = true }
                : item).ToList()
        }
        : scene;
    if (arguments.ContainsKey("add-benchmark-reflection-plane"))
    {
        benchmarkScene = await AddBenchmarkReflectionPlaneAsync(benchmarkScene, projectDirectory);
    }
    var importedGeometry = benchmarkScene.Geometry
        .Where(item => item.Kind == GeometryKind.ImportedMesh && item.Reflects)
        .ToArray();
    if (importedGeometry.Length == 0)
    {
        throw new InvalidOperationException(
            "Reflection-monitor benchmarking requires at least one reflective imported mesh; pass --enable-imported-reflections to benchmark a non-reflective fixture without changing its project.");
    }
    var monitorProfile = benchmarkScene.RenderProfiles[0] with
    {
        ReflectionOrder = Math.Max(1, benchmarkScene.RenderProfiles[0].ReflectionOrder)
    };
    var monitorReceiverIndex = arguments.TryGetValue("receiver-index", out var monitorReceiverIndexText)
        ? int.Parse(monitorReceiverIndexText, System.Globalization.CultureInfo.InvariantCulture)
        : 0;
    var enabledMonitorReceivers = benchmarkScene.ReceiverRigs.Where(item => item.Enabled).ToArray();
    if (monitorReceiverIndex < 0 || monitorReceiverIndex >= enabledMonitorReceivers.Length)
    {
        throw new ArgumentOutOfRangeException(
            "receiver-index",
            $"Receiver index {monitorReceiverIndex} does not identify one of the {enabledMonitorReceivers.Length} enabled rigs.");
    }
    var monitorReceiver = enabledMonitorReceivers[monitorReceiverIndex];
    var probeTime = benchmarkScene.Timeline.DurationSeconds * 0.5;

    var start = Stopwatch.GetTimestamp();
    var oneShotRoutes = NativeSceneRenderer.TraceImportedMeshReflectionPaths(
        benchmarkScene,
        monitorReceiver.Id,
        probeTime,
        monitorProfile,
        projectDirectory);
    var oneShotMilliseconds = Stopwatch.GetElapsedTime(start).TotalMilliseconds;

    start = Stopwatch.GetTimestamp();
    using var reflectionMonitor = NativeSceneRenderer.CreateReflectionMonitor(
        benchmarkScene,
        projectDirectory,
        monitorProfile.AtmosphereIntegrationSegments);
    var configurationMilliseconds = Stopwatch.GetElapsedTime(start).TotalMilliseconds;

    start = Stopwatch.GetTimestamp();
    var warmupRoutes = reflectionMonitor.Query(monitorReceiver.Id, probeTime, monitorProfile);
    var warmupMilliseconds = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
    var queryMilliseconds = new double[reflectionQueryCount];
    var routeCounts = new int[reflectionQueryCount];
    for (var index = 0; index < reflectionQueryCount; index++)
    {
        var receiveTime = benchmarkScene.Timeline.DurationSeconds * (index + 1.0) / (reflectionQueryCount + 1.0);
        start = Stopwatch.GetTimestamp();
        var routes = reflectionMonitor.Query(monitorReceiver.Id, receiveTime, monitorProfile);
        queryMilliseconds[index] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        routeCounts[index] = routes.Count;
    }
    var deterministicProbe = reflectionMonitor.Query(monitorReceiver.Id, probeTime, monitorProfile);
    var oneShotHash = ReflectionRouteHash(oneShotRoutes);
    var warmupHash = ReflectionRouteHash(warmupRoutes);
    var deterministicHash = ReflectionRouteHash(deterministicProbe);
    var sortedQueryMilliseconds = queryMilliseconds.Order().ToArray();
    var percentile95Index = (int)Math.Ceiling(sortedQueryMilliseconds.Length * 0.95) - 1;
    var queryAverageMilliseconds = queryMilliseconds.Average();
    var speedup = oneShotMilliseconds / Math.Max(queryAverageMilliseconds, 1.0e-9);
    var deterministic = string.Equals(oneShotHash, warmupHash, StringComparison.Ordinal) &&
        string.Equals(warmupHash, deterministicHash, StringComparison.Ordinal);
    var passed = deterministic &&
        reflectionMonitor.CompletedQueryCount == checked((ulong)reflectionQueryCount + 2UL) &&
        deterministicProbe.Count > 0 &&
        routeCounts.Max() > 0 &&
        oneShotMilliseconds > queryAverageMilliseconds;
    var reflectionReceipt = new
    {
        marker = passed ? "PULSEGEN_REFLECTION_MONITOR_STRESS_OK" : "PULSEGEN_REFLECTION_MONITOR_STRESS_FAILED",
        capturedAtUtc = DateTimeOffset.UtcNow,
        engineVersion = NativeSceneRenderer.EngineVersion,
        project = Path.GetFullPath(projectDirectory),
        scene = benchmarkScene.Name,
        machine = Environment.MachineName,
        operatingSystem = Environment.OSVersion.VersionString,
        runtime = Environment.Version.ToString(),
        processor = Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ?? "unknown",
        importedMeshCount = importedGeometry.Length,
        importedAssetSha256 = importedGeometry.Select(item => item.MeshAssetHash).ToArray(),
        receiver = monitorReceiver.Name,
        monitorProfile.SampleRate,
        monitorProfile.ReflectionOrder,
        monitorProfile.RayCount,
        monitorProfile.AtmosphereIntegrationSegments,
        configurationCount = 1,
        baselineOneShotMilliseconds = oneShotMilliseconds,
        retainedConfigurationMilliseconds = configurationMilliseconds,
        retainedWarmupMilliseconds = warmupMilliseconds,
        retainedQueryCount = reflectionQueryCount,
        retainedCompletedQueryCount = reflectionMonitor.CompletedQueryCount,
        retainedQueryMillisecondsAverage = queryAverageMilliseconds,
        retainedQueryMillisecondsP95 = sortedQueryMilliseconds[percentile95Index],
        retainedQueryMillisecondsMaximum = queryMilliseconds.Max(),
        speedupVersusOneShot = speedup,
        routeCountMinimum = routeCounts.Min(),
        routeCountMaximum = routeCounts.Max(),
        probeRouteCount = deterministicProbe.Count,
        deterministic,
        deterministicRouteSha256 = deterministicHash
    };
    var reflectionReceiptJson = JsonSerializer.Serialize(
        reflectionReceipt,
        new JsonSerializerOptions { WriteIndented = true });
    if (arguments.TryGetValue("reflection-monitor-receipt", out var reflectionReceiptPath))
    {
        WriteTextAtomically(Path.GetFullPath(reflectionReceiptPath), reflectionReceiptJson);
    }
    Console.WriteLine(reflectionReceiptJson);
    if (!passed)
    {
        throw new InvalidOperationException(
            $"The retained reflection monitor was deterministic={deterministic} and averaged {queryAverageMilliseconds:0.###} ms versus {oneShotMilliseconds:0.###} ms for one-shot reconstruction.");
    }
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

static string ReflectionRouteHash(IReadOnlyList<ImportedMeshReflectionPathData> routes) =>
    Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(routes))).ToLowerInvariant();

static void WriteTextAtomically(string destination, string contents)
{
    var directory = Path.GetDirectoryName(destination)
        ?? throw new InvalidOperationException("The reflection-monitor receipt requires a parent directory.");
    Directory.CreateDirectory(directory);
    var temporary = Path.Combine(directory, $".{Path.GetFileName(destination)}.{Guid.NewGuid():N}.tmp");
    try
    {
        File.WriteAllText(temporary, contents);
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

static async Task<SceneDocument> AddBenchmarkReflectionPlaneAsync(SceneDocument scene, string projectDirectory)
{
    const string obj = """
        o PulseGenReflectionMonitorProbe
        v -40 0 -20
        v -40 0 20
        v 40 0 20
        v 40 0 -20
        f 1 2 3
        f 1 3 4
        """;
    var sourcePath = Path.Combine(Path.GetTempPath(), $"pulsegen-reflection-monitor-{Guid.NewGuid():N}.obj");
    try
    {
        await File.WriteAllTextAsync(sourcePath, obj);
        var asset = await ProjectStore.ImportAssetAsync(
            projectDirectory,
            sourcePath,
            AssetKind.Mesh,
            "model/obj",
            "PulseGen deterministic reflection-monitor benchmark plane",
            "CC0-1.0");
        var assets = scene.Assets.ToList();
        if (!assets.Any(item => string.Equals(item.Sha256, asset.Sha256, StringComparison.OrdinalIgnoreCase)))
        {
            assets.Add(asset);
        }
        var geometry = scene.Geometry.ToList();
        geometry.Add(new AcousticGeometry
        {
            Id = Guid.Parse("f3f3f3f3-0000-4000-8000-000000000001"),
            Name = "Reflection monitor probe floor",
            Kind = GeometryKind.ImportedMesh,
            MaterialId = scene.Materials[0].Id,
            MeshAssetHash = asset.Sha256,
            DimensionsMetres = new(80, 0.01, 40),
            Occludes = false,
            Reflects = true
        });
        return scene with { Assets = assets, Geometry = geometry };
    }
    finally
    {
        if (File.Exists(sourcePath))
        {
            File.Delete(sourcePath);
        }
    }
}
