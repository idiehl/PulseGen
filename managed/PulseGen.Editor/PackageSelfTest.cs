using System.Reflection;
using System.Text.Json;
using PulseGen.Contracts;
using PulseGen.Engine.Client;
using PulseGen.ModelHost.Client;

namespace PulseGen.Editor;

internal sealed record PackageSelfTestOptions(string ReceiptPath, string OutputDirectory)
{
    public static PackageSelfTestOptions? TryParse(IReadOnlyList<string> arguments)
    {
        if (!arguments.Contains("--package-self-test", StringComparer.Ordinal))
        {
            return null;
        }

        static string? Value(IReadOnlyList<string> values, string name)
        {
            var index = values.IndexOf(name);
            if (index < 0)
            {
                return null;
            }
            if (index + 1 >= values.Count || values[index + 1].StartsWith("--", StringComparison.Ordinal))
            {
                throw new ArgumentException($"{name} requires a value.", name);
            }
            return values[index + 1];
        }

        var receipt = Value(arguments, "--receipt")
            ?? throw new ArgumentException("--package-self-test requires --receipt.", "--receipt");
        if (!receipt.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The package self-test receipt must be a .json file.", "--receipt");
        }

        var fullReceipt = Path.GetFullPath(receipt);
        var output = Value(arguments, "--output")
            ?? Path.Combine(Path.GetDirectoryName(fullReceipt)!, "render");
        return new(fullReceipt, Path.GetFullPath(output));
    }
}

internal static class PackageSelfTest
{
    private const string SuccessMarker = "PULSEGEN_PACKAGE_SELF_TEST_OK";
    private const string FailureMarker = "PULSEGEN_PACKAGE_SELF_TEST_FAILED";

    public static async Task<int> RunAsync(PackageSelfTestOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var checks = new List<SelfTestCheck>();
        SceneDocument? flyby = null;

        await CheckAsync("package-manifest", () =>
        {
            var path = Path.Combine(AppContext.BaseDirectory, "package-manifest.json");
            using var manifest = JsonDocument.Parse(File.ReadAllText(path));
            var root = manifest.RootElement;
            if (root.GetProperty("schemaVersion").GetInt32() != 1 ||
                root.GetProperty("product").GetString() != "PulseGen" ||
                root.GetProperty("runtimeIdentifier").GetString() != "win-x64")
            {
                throw new InvalidDataException("The package manifest identity is invalid.");
            }
            return Task.FromResult($"{root.GetProperty("version").GetString()} · {root.GetProperty("channel").GetString()}");
        });

        await CheckAsync("native-engine", () =>
        {
            var engineVersion = NativeSceneRenderer.EngineVersion;
            var productVersion = ProductVersion();
            if (!string.Equals(engineVersion, productVersion, StringComparison.Ordinal))
            {
                throw new InvalidDataException($"Native engine {engineVersion} does not match packaged editor {productVersion}.");
            }
            return Task.FromResult(engineVersion);
        });

        await CheckAsync("codec-runtime", () =>
        {
            var path = Path.Combine(AppContext.BaseDirectory, "sndfile.dll");
            if (!File.Exists(path) || !SndFileCodecAdapter.VerifyRuntimeFile(path))
            {
                throw new InvalidDataException("The packaged codec runtime failed its pinned SHA-256 check.");
            }
            return Task.FromResult(SndFileCodecAdapter.RuntimeVersion);
        });

        await CheckAsync("model-host", async () =>
        {
            await using var client = await ModelHostClient.StartAsync();
            var packs = await client.GetPackInventoryAsync();
            if (packs.ValueKind != JsonValueKind.Array || packs.GetArrayLength() < 2)
            {
                throw new InvalidDataException("The local analysis host did not return its pack inventory.");
            }
            return $"local-only Python host · {packs.GetArrayLength()} optional pack adapters";
        });

        await CheckAsync("sample-projects", async () =>
        {
            var samples = Path.Combine(AppContext.BaseDirectory, "Samples");
            flyby = await ProjectStore.LoadAsync(Path.Combine(samples, "PhysicsFlyby.pulseproj"));
            var echo = await ProjectStore.LoadAsync(Path.Combine(samples, "SurfaceEcho.pulseproj"));
            if (flyby.ReceiverRigs.Count < 2 || echo.Geometry.Count == 0)
            {
                throw new InvalidDataException("The packaged starter projects are incomplete.");
            }
            return $"{flyby.Name}; {echo.Name}";
        });

        await CheckAsync("offline-render", () =>
        {
            if (flyby is null)
            {
                throw new InvalidOperationException("The flyby starter project was not available for rendering.");
            }
            Directory.CreateDirectory(options.OutputDirectory);
            var shortScene = flyby with
            {
                Name = "Package Smoke Flyby",
                Timeline = flyby.Timeline with { DurationSeconds = 0.25 }
            };
            var summary = NativeSceneRenderer.Render(shortScene, options.OutputDirectory);
            var takes = Directory.GetFiles(options.OutputDirectory, "*.wav", SearchOption.TopDirectoryOnly);
            if (summary.FilesWritten != 2 || summary.FramesRendered != 12_000 || takes.Length != 2 ||
                takes.Any(path => new FileInfo(path).Length <= 44))
            {
                throw new InvalidDataException("The packaged engine did not produce two valid quarter-second stereo takes.");
            }
            return Task.FromResult($"{summary.FilesWritten} stereo takes · {summary.FramesRendered} frames · 48 kHz float");
        });

        await CheckAsync("sample-source-render", async () =>
        {
            if (flyby is null) throw new InvalidOperationException("The flyby starter is required.");
            var projectDirectory = Path.Combine(options.OutputDirectory, "sample-source.pulseproj");
            await ProjectStore.SaveAsync(flyby, projectDirectory);
            var sourceDirectory = Path.Combine(options.OutputDirectory, "sample-source-input");
            Directory.CreateDirectory(sourceDirectory);
            var sourcePath = Path.Combine(sourceDirectory, "source-left-only.wav");
            var samples = new float[2400];
            for (var frame = 0; frame < samples.Length / 2; frame++) samples[frame * 2] = 0.25f;
            FloatWaveFile.WriteAtomic(sourcePath, new FloatWaveBuffer(48000, 2, samples));
            var importedAsset = await ProjectStore.ImportAssetAsync(projectDirectory, sourcePath, AssetKind.Audio, "audio/wav");
            var decoded = SampleAudioReader.Read(sourcePath);
            var asset = importedAsset with { Channels = decoded.Channels, SampleRate = decoded.SampleRate, DurationSeconds = decoded.DurationSeconds };
            var imported = SampleSourceAuthoring.Add(flyby, asset, new(SourceChannelMapping.NonSpatialStereoBed, true));
            var sampleScene = imported.Scene with { Timeline = imported.Scene.Timeline with { DurationSeconds = decoded.DurationSeconds } };
            await ProjectStore.SaveAsync(sampleScene, projectDirectory);
            var loaded = await ProjectStore.LoadAsync(projectDirectory);
            var output = Path.Combine(projectDirectory, "renders");
            var summary = NativeSceneRenderer.Render(loaded, output, projectDirectory: projectDirectory);
            var takes = Directory.GetFiles(output, "*.wav");
            if (summary.FilesWritten != 2 || summary.FramesRendered != 1200 || takes.Length != 2)
                throw new InvalidDataException("Sample-source render did not produce the expected isolated takes.");
            foreach (var take in takes)
            {
                var audio = FloatWaveFile.Read(take);
                for (var frame = 32; frame < 1168; frame++)
                    if (Math.Abs(audio.InterleavedSamples[frame * 2] - 0.25f) > 1e-5 || audio.InterleavedSamples[frame * 2 + 1] != 0)
                        throw new InvalidDataException("Stereo background changed channel order, gain, or isolation.");
            }
            var captured = await CaptureTakeImporter.ImportAsync(loaded, projectDirectory, summary.RenderReceiptPath);
            if (captured.ImportedTakeCount != 2)
                throw new InvalidDataException("Sample-source receipt could not be imported into editable receiver takes.");
            return "Copied recording reopened, rendered, and imported into both editable receiver takes; left-only channel preserved";
        });

        await CheckAsync("mp3-exact-length-import", async () =>
        {
            var inputDirectory = Path.Combine(options.OutputDirectory, "sample-source-input");
            Directory.CreateDirectory(inputDirectory);
            var path = Path.Combine(inputDirectory, "padded-mono.mp3");
            using (var stream = File.Create(path))
            {
                // Authored silent MPEG-1 Layer III packets with variable byte padding.
                // This reproduces the estimated-length bug without bundling user media.
                for (var packet = 0; packet < 137; packet++)
                {
                    var padded = packet % 3 != 0;
                    var bytes = new byte[835 + (padded ? 1 : 0)];
                    bytes[0] = 0xff; bytes[1] = 0xfb; bytes[2] = (byte)(0xd0 | (padded ? 2 : 0)); bytes[3] = 0xc0;
                    stream.Write(bytes);
                }
            }
            using var prepared = await PreparedAudioImport.CreateAsync(path, CancellationToken.None);
            var audio = SampleAudioReader.Read(prepared.FilePath);
            if (audio.Frames != 137 * 1152 || audio.SampleRate != 44100 || audio.Channels != 1 ||
                !audio.DecoderVersion.Contains("dr_mp3", StringComparison.Ordinal) || audio.DecoderBinarySha256.Length != 64)
                throw new InvalidDataException("The packaged MP3 importer did not preserve the exact decoded frame count.");
            return "137 complete padded mono MP3 packets imported as exactly 157824 frames at 44.1 kHz";
        });

        var passed = checks.All(check => check.Passed);
        var receipt = new
        {
            schemaVersion = 1,
            marker = passed ? SuccessMarker : FailureMarker,
            product = "PulseGen",
            version = ProductVersion(),
            runtimeIdentifier = "win-x64",
            createdAtUtc = DateTimeOffset.UtcNow,
            packageRoot = AppContext.BaseDirectory,
            outputDirectory = options.OutputDirectory,
            checks
        };

        await WriteReceiptAtomicAsync(options.ReceiptPath, JsonSerializer.Serialize(receipt, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true
        }));
        return passed ? 0 : 1;

        async Task CheckAsync(string name, Func<Task<string>> action)
        {
            try
            {
                checks.Add(new(name, true, await action(), null));
            }
            catch (Exception exception)
            {
                checks.Add(new(name, false, null, $"{exception.GetType().Name}: {exception.Message}"));
            }
        }
    }

    private static string ProductVersion()
    {
        var version = typeof(PackageSelfTest).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;
        return string.IsNullOrWhiteSpace(version)
            ? "unknown"
            : version.Split('+', 2)[0];
    }

    private static async Task WriteReceiptAtomicAsync(string path, string json)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllTextAsync(temporary, json);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private sealed record SelfTestCheck(string Name, bool Passed, string? Detail, string? Error);
}
