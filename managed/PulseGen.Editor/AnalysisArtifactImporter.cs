using PulseGen.Contracts;
using PulseGen.ModelHost.Client;
using System.Text.Json;

namespace PulseGen.Editor;

internal sealed record AnalysisImportResult(
    SceneDocument Scene,
    int DeclaredAssetCount,
    int ReusedAssetCount,
    int PartialTrackCount,
    ModelHostChannelMode ChannelMode,
    int MaxPartials,
    double RmsNull,
    double MaxAbsoluteError,
    string SourceFileName,
    string AnalysisReceiptSha256);

internal static class AnalysisArtifactImporter
{
    internal const double MaximumReconstructionError = 1.0e-5;
    private const long MaximumManifestBytes = 16L * 1024 * 1024;
    private const long MaximumTrackBytes = 512L * 1024 * 1024;
    private const long MaximumLayerBytes = 32L * 1024 * 1024 * 1024;

    public static async Task<AnalysisImportResult> ImportAsync(
        SceneDocument scene,
        string projectDirectory,
        string outputDirectory,
        string expectedSourcePath,
        ModelHostChannelMode expectedChannelMode,
        int expectedMaxPartials,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scene);
        if (expectedMaxPartials is < 1 or > 256)
        {
            throw new ArgumentOutOfRangeException(nameof(expectedMaxPartials), "Partial density must be in 1..256.");
        }

        var projectRoot = Path.GetFullPath(projectDirectory);
        var outputRoot = Path.GetFullPath(outputDirectory);
        var sourcePath = Path.GetFullPath(expectedSourcePath);
        var savedScene = await ProjectStore.LoadAsync(projectRoot, cancellationToken);
        if (savedScene.ProjectId != scene.ProjectId)
        {
            throw new InvalidDataException("The selected project directory belongs to a different PulseGen scene.");
        }
        if (!string.Equals(
                ProjectStore.ComputeSceneRevisionSha256(savedScene),
                ProjectStore.ComputeSceneRevisionSha256(scene),
                StringComparison.Ordinal))
        {
            throw new InvalidDataException("The saved project revision changed after this receiver take was prepared. Reopen or save, then render again.");
        }

        var manifestPath = Path.Combine(outputRoot, "analysis.json");
        var manifestFile = RequireBoundedFile(manifestPath, MaximumManifestBytes, "Analysis manifest");
        var validatedManifestHash = await ProjectStore.ComputeSha256Async(manifestFile.FullName, cancellationToken);
        using var manifest = await ReadJsonAsync(manifestFile.FullName, cancellationToken);
        var root = manifest.RootElement;
        RequireInteger(root, "schemaVersion", 1, "analysis manifest");

        var source = RequireObject(root, "source");
        var declaredSourcePath = Path.GetFullPath(RequireString(source, "path"));
        if (!string.Equals(declaredSourcePath, sourcePath, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Analysis provenance does not identify the receiver take selected by the editor.");
        }
        if (!RequireBoolean(source, "untouched"))
        {
            throw new InvalidDataException("Analysis did not preserve the untouched receiver take.");
        }
        var sourceHash = RequireSha256(source, "sha256");
        var actualSourceHash = await ProjectStore.ComputeSha256Async(sourcePath, cancellationToken);
        if (!string.Equals(sourceHash, actualSourceHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The receiver take changed after analysis and was not imported.");
        }

        var sampleRate = RequirePositiveInt32(source, "sampleRate");
        var channels = RequirePositiveInt32(source, "channels");
        var frames = RequirePositiveInt64(source, "frames");
        var durationSeconds = RequireFiniteNumber(source, "durationSeconds");
        if (channels != 2)
        {
            throw new InvalidDataException("This editor workflow accepts only two-channel receiver takes.");
        }
        if (durationSeconds <= 0 || Math.Abs(durationSeconds - ((double)frames / sampleRate)) > (1.0 / sampleRate))
        {
            throw new InvalidDataException("Analysis duration does not agree with its frame and sample-rate metadata.");
        }

        var analysis = RequireObject(root, "analysis");
        if (!string.Equals(RequireString(analysis, "kind"), "multi-resolution-stft-partial-transient-stochastic", StringComparison.Ordinal))
        {
            throw new InvalidDataException("The result is not a supported PulseGen spectral decomposition.");
        }
        var channelMode = ParseChannelMode(RequireString(analysis, "channelMode"));
        if (channelMode != expectedChannelMode)
        {
            throw new InvalidDataException("Analysis channel mode does not match the requested stereo interpretation.");
        }
        var maxPartials = RequirePositiveInt32(analysis, "maxPartialsPerFrame");
        if (maxPartials != expectedMaxPartials)
        {
            throw new InvalidDataException("Analysis partial density does not match the requested setting.");
        }

        var reconstruction = RequireObject(root, "reconstruction");
        if (!RequireBoolean(reconstruction, "timingAndChannelAligned"))
        {
            throw new InvalidDataException("Analysis layers do not retain exact receiver-take timing and channel alignment.");
        }
        var maxAbsoluteError = RequireFiniteNumber(reconstruction, "maxAbsoluteError");
        var rmsNull = RequireFiniteNumber(reconstruction, "rmsNull");
        if (maxAbsoluteError < 0 || rmsNull < 0 ||
            maxAbsoluteError > MaximumReconstructionError || rmsNull > MaximumReconstructionError)
        {
            throw new InvalidDataException($"Analysis reconstruction exceeded the {MaximumReconstructionError:0.0e+0} safety threshold.");
        }

        var provenance = RequireObject(root, "provenance");
        var hostVersion = RequireString(provenance, "hostVersion");
        if (!RequireBoolean(provenance, "localOnly"))
        {
            throw new InvalidDataException("Analysis did not confirm the local-only processing boundary.");
        }

        var layers = RequireObject(root, "layers");
        var layerInputs = new[]
        {
            ReadLayer(layers, "partials", "partials.wav", outputRoot),
            ReadLayer(layers, "transients", "transients.wav", outputRoot),
            ReadLayer(layers, "stochasticResidual", "stochastic-residual.wav", outputRoot)
        };
        foreach (var layer in layerInputs)
        {
            var actualHash = await ProjectStore.ComputeSha256Async(layer.Path, cancellationToken);
            if (!string.Equals(layer.Sha256, actualHash, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"{layer.FileName} failed its recorded SHA-256 integrity check.");
            }
        }

        var partialTracks = RequireObject(root, "partialTracks");
        var trackFileName = RequireExactFileName(partialTracks, "file", "partial-tracks.json");
        var trackCount = RequireNonNegativeInt32(partialTracks, "count");
        var trackPath = Path.Combine(outputRoot, trackFileName);
        var trackFile = RequireBoundedFile(trackPath, MaximumTrackBytes, "Partial-track data");
        var validatedTrackHash = await ProjectStore.ComputeSha256Async(trackFile.FullName, cancellationToken);
        using (var tracks = await ReadJsonAsync(trackFile.FullName, cancellationToken))
        {
            RequireInteger(tracks.RootElement, "schemaVersion", 1, "partial-track data");
            if (!string.Equals(RequireString(tracks.RootElement, "representation"), ToProtocolValue(channelMode), StringComparison.Ordinal))
            {
                throw new InvalidDataException("Partial-track representation does not agree with the analysis channel mode.");
            }
            var trackArray = RequireArray(tracks.RootElement, "tracks");
            if (trackArray.GetArrayLength() != trackCount)
            {
                throw new InvalidDataException("Partial-track count does not agree with the analysis manifest.");
            }
        }

        var provenanceText = $"PulseGen spectral analysis {hostVersion}; mode={ToProtocolValue(channelMode)}; maxPartials={maxPartials}; sourceSha256={sourceHash}; exactResidual=true";
        var candidates = new List<AssetRef>(5);
        foreach (var layer in layerInputs)
        {
            var imported = await ProjectStore.ImportAssetAsync(
                projectRoot,
                layer.Path,
                AssetKind.ModelOutput,
                "audio/wav",
                provenanceText,
                cancellationToken: cancellationToken);
            if (!string.Equals(imported.Sha256, layer.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"{layer.FileName} changed after verification and was not declared by the project.");
            }
            candidates.Add(imported with
            {
                SampleRate = sampleRate,
                Channels = channels,
                DurationSeconds = durationSeconds
            });
        }
        var importedTracks = await ProjectStore.ImportAssetAsync(
            projectRoot,
            trackFile.FullName,
            AssetKind.ModelOutput,
            "application/vnd.pulsegen.partial-tracks+json",
            provenanceText,
            cancellationToken: cancellationToken);
        if (!string.Equals(importedTracks.Sha256, validatedTrackHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Partial-track data changed after verification and was not declared by the project.");
        }
        candidates.Add(importedTracks);
        var importedManifest = await ProjectStore.ImportAssetAsync(
            projectRoot,
            manifestFile.FullName,
            AssetKind.ModelOutput,
            "application/vnd.pulsegen.analysis+json",
            provenanceText,
            cancellationToken: cancellationToken);
        if (!string.Equals(importedManifest.Sha256, validatedManifestHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Analysis receipt changed after verification and was not declared by the project.");
        }
        candidates.Add(importedManifest);

        var mergedAssets = scene.Assets.ToList();
        var declaredCount = 0;
        var reusedCount = 0;
        foreach (var candidate in candidates)
        {
            var existing = mergedAssets.FirstOrDefault(asset =>
                string.Equals(asset.Sha256, candidate.Sha256, StringComparison.OrdinalIgnoreCase));
            if (existing is null)
            {
                mergedAssets.Add(candidate);
                declaredCount++;
                continue;
            }
            if (existing.Kind != candidate.Kind || !string.Equals(existing.MediaType, candidate.MediaType, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("Analysis content collides with an incompatible asset already declared by this project.");
            }
            reusedCount++;
        }

        var updatedScene = scene with
        {
            Assets = mergedAssets,
            UpdatedAtUtc = DateTimeOffset.UtcNow
        };
        SceneValidator.EnsureValid(updatedScene);
        await ProjectStore.SaveAsync(updatedScene, projectRoot, cancellationToken);
        return new(
            updatedScene,
            declaredCount,
            reusedCount,
            trackCount,
            channelMode,
            maxPartials,
            rmsNull,
            maxAbsoluteError,
            Path.GetFileName(sourcePath),
            importedManifest.Sha256);
    }

    private static (string Path, string FileName, string Sha256) ReadLayer(
        JsonElement layers,
        string propertyName,
        string expectedFileName,
        string outputRoot)
    {
        var layer = RequireObject(layers, propertyName);
        var fileName = RequireExactFileName(layer, "file", expectedFileName);
        var path = Path.Combine(outputRoot, fileName);
        _ = RequireBoundedFile(path, MaximumLayerBytes, $"Analysis layer {fileName}");
        return (path, fileName, RequireSha256(layer, "sha256"));
    }

    private static ModelHostChannelMode ParseChannelMode(string value) => value switch
    {
        "linked" => ModelHostChannelMode.LinkedStereo,
        "independent" => ModelHostChannelMode.IndependentChannels,
        "mid-side" => ModelHostChannelMode.MidSide,
        _ => throw new InvalidDataException("Analysis channelMode must be linked, independent, or mid-side.")
    };

    private static string ToProtocolValue(ModelHostChannelMode value) => value switch
    {
        ModelHostChannelMode.LinkedStereo => "linked",
        ModelHostChannelMode.IndependentChannels => "independent",
        ModelHostChannelMode.MidSide => "mid-side",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, "Unsupported channel analysis mode.")
    };

    private static async Task<JsonDocument> ReadJsonAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await JsonDocument.ParseAsync(stream, new JsonDocumentOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = 128
        }, cancellationToken);
    }

    private static FileInfo RequireBoundedFile(string path, long maximumBytes, string description)
    {
        var file = new FileInfo(path);
        if (!file.Exists || file.Length <= 0 || file.Length > maximumBytes)
        {
            throw new InvalidDataException($"{description} is missing, empty, or exceeds its size limit.");
        }
        return file;
    }

    private static JsonElement RequireObject(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException($"Analysis result requires an object named '{name}'.");
        }
        return value;
    }

    private static JsonElement RequireArray(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException($"Analysis result requires an array named '{name}'.");
        }
        return value;
    }

    private static string RequireString(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
        {
            throw new InvalidDataException($"Analysis result requires a non-empty string named '{name}'.");
        }
        return value.GetString()!;
    }

    private static string RequireExactFileName(JsonElement parent, string name, string expected)
    {
        var value = RequireString(parent, name);
        if (!string.Equals(value, expected, StringComparison.Ordinal) ||
            !string.Equals(Path.GetFileName(value), value, StringComparison.Ordinal) ||
            Path.IsPathRooted(value))
        {
            throw new InvalidDataException($"Analysis result expected the local file '{expected}'.");
        }
        return value;
    }

    private static string RequireSha256(JsonElement parent, string name)
    {
        var value = RequireString(parent, name);
        if (value.Length != 64 || !value.All(Uri.IsHexDigit))
        {
            throw new InvalidDataException($"Analysis result requires a valid SHA-256 value named '{name}'.");
        }
        return value.ToLowerInvariant();
    }

    private static bool RequireBoolean(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value) || value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            throw new InvalidDataException($"Analysis result requires a Boolean named '{name}'.");
        }
        return value.GetBoolean();
    }

    private static int RequirePositiveInt32(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value) || !value.TryGetInt32(out var number) || number <= 0)
        {
            throw new InvalidDataException($"Analysis result requires a positive integer named '{name}'.");
        }
        return number;
    }

    private static int RequireNonNegativeInt32(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value) || !value.TryGetInt32(out var number) || number < 0)
        {
            throw new InvalidDataException($"Analysis result requires a non-negative integer named '{name}'.");
        }
        return number;
    }

    private static long RequirePositiveInt64(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value) || !value.TryGetInt64(out var number) || number <= 0)
        {
            throw new InvalidDataException($"Analysis result requires a positive integer named '{name}'.");
        }
        return number;
    }

    private static double RequireFiniteNumber(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value) || !value.TryGetDouble(out var number) || !double.IsFinite(number))
        {
            throw new InvalidDataException($"Analysis result requires a finite number named '{name}'.");
        }
        return number;
    }

    private static void RequireInteger(JsonElement parent, string name, int expected, string description)
    {
        if (!parent.TryGetProperty(name, out var value) || !value.TryGetInt32(out var number) || number != expected)
        {
            throw new InvalidDataException($"Unsupported {description} schema version.");
        }
    }
}
