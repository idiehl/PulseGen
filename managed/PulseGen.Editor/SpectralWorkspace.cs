using PulseGen.Contracts;
using PulseGen.ModelHost.Client;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PulseGen.Editor;

internal sealed record SpectralAssetFile(AssetRef Asset, string Path);

internal sealed record SpectralPartialPoint(
    double TimeSeconds,
    double FrequencyHz,
    double Amplitude,
    double PhaseRadians);

internal sealed record SpectralPartialTrack(
    string Id,
    int Channel,
    IReadOnlyList<SpectralPartialPoint> Points)
{
    public double PeakAmplitude => Points.Count == 0 ? 0 : Points.Max(point => point.Amplitude);
    public double MinimumFrequencyHz => Points.Count == 0 ? 0 : Points.Min(point => point.FrequencyHz);
    public double MaximumFrequencyHz => Points.Count == 0 ? 0 : Points.Max(point => point.FrequencyHz);
    public override string ToString() => $"{Id} · ch {Channel + 1} · {MinimumFrequencyHz:0}–{MaximumFrequencyHz:0} Hz";
}

internal sealed record SpectralAnalysisSession(
    string AnalysisReceiptSha256,
    string SourceSha256,
    string SourceFileName,
    int SampleRate,
    int Channels,
    long Frames,
    double DurationSeconds,
    ModelHostChannelMode ChannelMode,
    int MaxPartials,
    double RmsNull,
    SpectralAssetFile Partials,
    SpectralAssetFile Transients,
    SpectralAssetFile StochasticResidual,
    SpectralAssetFile PartialTracks,
    SpectralAssetFile AnalysisReceipt,
    IReadOnlyList<SpectralPartialTrack> Tracks)
{
    public string DisplayName => $"{Path.GetFileNameWithoutExtension(SourceFileName)} · {ChannelModeLabel} · {Tracks.Count:N0} tones";
    public string ChannelModeLabel => ChannelMode switch
    {
        ModelHostChannelMode.LinkedStereo => "linked stereo",
        ModelHostChannelMode.IndependentChannels => "separate L/R",
        ModelHostChannelMode.MidSide => "center/width",
        _ => "unknown"
    };
    public override string ToString() => DisplayName;
}

internal sealed record SpectralLayerSettings
{
    public bool Enabled { get; init; } = true;
    public double GainLinear { get; init; } = 1;
}

internal sealed record SpectralTrackEdit
{
    public required string TrackId { get; init; }
    public bool Enabled { get; init; } = true;
    public double GainLinear { get; init; } = 1;
    public double PitchSemitones { get; init; }
}

internal sealed record SpectralEditDocument
{
    public int SchemaVersion { get; init; } = 1;
    public Guid EditId { get; init; } = Guid.NewGuid();
    public required string AnalysisReceiptSha256 { get; init; }
    public required string SourceSha256 { get; init; }
    public string? ParentEditSha256 { get; init; }
    public DateTimeOffset CreatedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public SpectralLayerSettings Partials { get; init; } = new();
    public SpectralLayerSettings Transients { get; init; } = new();
    public SpectralLayerSettings StochasticResidual { get; init; } = new();
    public List<SpectralTrackEdit> TrackEdits { get; init; } = [];

    public static SpectralEditDocument CreateDefault(SpectralAnalysisSession session) => new()
    {
        AnalysisReceiptSha256 = session.AnalysisReceiptSha256,
        SourceSha256 = session.SourceSha256
    };
}

internal sealed record SpectralEditState(SpectralEditDocument Document, string? AssetSha256);

internal sealed record SpectralEditSaveResult(SceneDocument Scene, SpectralEditState State, AssetRef Asset);

internal sealed record SpectralRenderResult(
    FloatWaveBuffer Buffer,
    double Peak,
    double RmsLeft,
    double RmsRight);

internal static class SpectralAnalysisCatalog
{
    private const string AnalysisMediaType = "application/vnd.pulsegen.analysis+json";
    private const string TracksMediaType = "application/vnd.pulsegen.partial-tracks+json";
    private const int MaximumTracks = 100_000;
    private const int MaximumTrackPoints = 5_000_000;

    public static async Task<IReadOnlyList<SpectralAnalysisSession>> LoadAsync(
        SceneDocument scene,
        string projectDirectory,
        CancellationToken cancellationToken = default)
    {
        var sessions = new List<SpectralAnalysisSession>();
        foreach (var receiptAsset in scene.Assets.Where(asset =>
                     asset.Kind == AssetKind.ModelOutput &&
                     string.Equals(asset.MediaType, AnalysisMediaType, StringComparison.OrdinalIgnoreCase)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            sessions.Add(await LoadSessionAsync(scene, projectDirectory, receiptAsset, cancellationToken));
        }
        return sessions.OrderBy(session => session.SourceFileName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(session => session.AnalysisReceiptSha256, StringComparer.Ordinal)
            .ToArray();
    }

    private static async Task<SpectralAnalysisSession> LoadSessionAsync(
        SceneDocument scene,
        string projectDirectory,
        AssetRef receiptAsset,
        CancellationToken cancellationToken)
    {
        var receiptFile = await ResolveVerifiedAsync(projectDirectory, receiptAsset, cancellationToken);
        using var receipt = await ReadJsonAsync(receiptFile.Path, cancellationToken);
        var root = receipt.RootElement;
        RequireSchema(root, "analysis receipt");
        var source = RequireObject(root, "source");
        var sourceSha = RequireSha256(source, "sha256");
        var sourceFileName = Path.GetFileName(RequireString(source, "path"));
        var sampleRate = RequirePositiveInt(source, "sampleRate");
        var channels = RequirePositiveInt(source, "channels");
        var frames = RequirePositiveLong(source, "frames");
        var duration = RequireFinite(source, "durationSeconds");
        if (channels != 2 || duration <= 0 || Math.Abs(duration - ((double)frames / sampleRate)) > 1.0 / sampleRate)
        {
            throw new InvalidDataException("Spectral analysis source metadata is not aligned two-channel audio.");
        }

        var analysis = RequireObject(root, "analysis");
        var channelMode = ParseChannelMode(RequireString(analysis, "channelMode"));
        var maxPartials = RequirePositiveInt(analysis, "maxPartialsPerFrame");
        var reconstruction = RequireObject(root, "reconstruction");
        var rmsNull = RequireFinite(reconstruction, "rmsNull");
        if (rmsNull < 0 || rmsNull > AnalysisArtifactImporter.MaximumReconstructionError ||
            !reconstruction.GetProperty("timingAndChannelAligned").GetBoolean())
        {
            throw new InvalidDataException("Spectral analysis no longer satisfies its reconstruction guarantee.");
        }

        var layers = RequireObject(root, "layers");
        var partials = await ResolveLayerAsync(scene, projectDirectory, layers, "partials", cancellationToken);
        var transients = await ResolveLayerAsync(scene, projectDirectory, layers, "transients", cancellationToken);
        var stochastic = await ResolveLayerAsync(scene, projectDirectory, layers, "stochasticResidual", cancellationToken);

        var partialTracksInfo = RequireObject(root, "partialTracks");
        var declaredTrackCount = RequireNonNegativeInt(partialTracksInfo, "count");
        var trackCandidates = scene.Assets.Where(asset =>
                asset.Kind == AssetKind.ModelOutput &&
                string.Equals(asset.MediaType, TracksMediaType, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(asset.Provenance, receiptAsset.Provenance, StringComparison.Ordinal))
            .ToArray();
        if (trackCandidates.Length != 1)
        {
            throw new InvalidDataException("Spectral analysis must resolve exactly one provenance-matched partial-track asset.");
        }
        var trackFile = await ResolveVerifiedAsync(projectDirectory, trackCandidates[0], cancellationToken);
        var tracks = await ReadTracksAsync(trackFile.Path, channelMode, sampleRate, duration, cancellationToken);
        if (tracks.Count != declaredTrackCount)
        {
            throw new InvalidDataException("Partial-track count no longer agrees with the analysis receipt.");
        }

        return new(
            receiptAsset.Sha256,
            sourceSha,
            sourceFileName,
            sampleRate,
            channels,
            frames,
            duration,
            channelMode,
            maxPartials,
            rmsNull,
            partials,
            transients,
            stochastic,
            trackFile,
            receiptFile,
            tracks);
    }

    internal static async Task<SpectralAssetFile> ResolveVerifiedAsync(
        string projectDirectory,
        AssetRef asset,
        CancellationToken cancellationToken)
    {
        var path = ProjectStore.ResolveAssetPath(projectDirectory, asset.Sha256);
        var file = new FileInfo(path);
        if (!file.Exists || file.Length <= 0 || file.Length != asset.SizeBytes)
        {
            throw new InvalidDataException($"Project asset {asset.FileName} is missing or has the wrong size.");
        }
        var hash = await ProjectStore.ComputeSha256Async(path, cancellationToken);
        if (!string.Equals(hash, asset.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"Project asset {asset.FileName} failed SHA-256 verification.");
        }
        return new(asset, path);
    }

    private static async Task<SpectralAssetFile> ResolveLayerAsync(
        SceneDocument scene,
        string projectDirectory,
        JsonElement layers,
        string name,
        CancellationToken cancellationToken)
    {
        var hash = RequireSha256(RequireObject(layers, name), "sha256");
        var asset = scene.Assets.SingleOrDefault(candidate =>
            string.Equals(candidate.Sha256, hash, StringComparison.OrdinalIgnoreCase) &&
            candidate.Kind == AssetKind.ModelOutput &&
            string.Equals(candidate.MediaType, "audio/wav", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException($"Spectral layer '{name}' is not declared as a project audio asset.");
        return await ResolveVerifiedAsync(projectDirectory, asset, cancellationToken);
    }

    private static async Task<IReadOnlyList<SpectralPartialTrack>> ReadTracksAsync(
        string path,
        ModelHostChannelMode expectedMode,
        int sampleRate,
        double duration,
        CancellationToken cancellationToken)
    {
        using var document = await ReadJsonAsync(path, cancellationToken);
        var root = document.RootElement;
        RequireSchema(root, "partial tracks");
        if (ParseChannelMode(RequireString(root, "representation")) != expectedMode)
        {
            throw new InvalidDataException("Partial-track representation does not match its analysis receipt.");
        }
        var array = root.GetProperty("tracks");
        if (array.ValueKind != JsonValueKind.Array || array.GetArrayLength() > MaximumTracks)
        {
            throw new InvalidDataException("Partial-track collection is missing or exceeds the editor limit.");
        }

        var result = new List<SpectralPartialTrack>(array.GetArrayLength());
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var pointCount = 0;
        foreach (var item in array.EnumerateArray())
        {
            var id = RequireString(item, "id");
            var channel = RequireNonNegativeInt(item, "channel");
            var pointsElement = item.GetProperty("points");
            if (!ids.Add(id) || id.Length > 128 || channel > 1 || pointsElement.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidDataException("Partial track identity, channel, or point collection is invalid.");
            }
            pointCount = checked(pointCount + pointsElement.GetArrayLength());
            if (pointCount > MaximumTrackPoints)
            {
                throw new InvalidDataException("Partial-track point count exceeds the editor limit.");
            }

            var points = new List<SpectralPartialPoint>(pointsElement.GetArrayLength());
            double previousTime = -1;
            foreach (var point in pointsElement.EnumerateArray())
            {
                var time = RequireFinite(point, "timeSeconds");
                var frequency = RequireFinite(point, "frequencyHz");
                var amplitude = RequireFinite(point, "amplitude");
                var phase = RequireFinite(point, "phaseRadians");
                if (time < previousTime || time < 0 || time > duration + 1 || frequency <= 0 ||
                    frequency > sampleRate / 2.0 || amplitude < 0 || amplitude > 100)
                {
                    throw new InvalidDataException($"Partial track '{id}' contains an out-of-range point.");
                }
                previousTime = time;
                points.Add(new(time, frequency, amplitude, phase));
            }
            if (points.Count >= 2)
            {
                result.Add(new(id, channel, points));
            }
        }
        return result;
    }

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

    private static void RequireSchema(JsonElement root, string description)
    {
        if (!root.TryGetProperty("schemaVersion", out var value) || !value.TryGetInt32(out var version) || version != 1)
        {
            throw new InvalidDataException($"Unsupported {description} schema version.");
        }
    }

    private static JsonElement RequireObject(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Object
            ? value
            : throw new InvalidDataException($"Spectral data requires an object named '{name}'.");

    private static string RequireString(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()!
            : throw new InvalidDataException($"Spectral data requires a string named '{name}'.");

    private static string RequireSha256(JsonElement parent, string name)
    {
        var value = RequireString(parent, name);
        return value.Length == 64 && value.All(Uri.IsHexDigit)
            ? value.ToLowerInvariant()
            : throw new InvalidDataException($"Spectral data requires a valid SHA-256 named '{name}'.");
    }

    private static int RequirePositiveInt(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) && value.TryGetInt32(out var number) && number > 0
            ? number
            : throw new InvalidDataException($"Spectral data requires a positive integer named '{name}'.");

    private static int RequireNonNegativeInt(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) && value.TryGetInt32(out var number) && number >= 0
            ? number
            : throw new InvalidDataException($"Spectral data requires a non-negative integer named '{name}'.");

    private static long RequirePositiveLong(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) && value.TryGetInt64(out var number) && number > 0
            ? number
            : throw new InvalidDataException($"Spectral data requires a positive integer named '{name}'.");

    private static double RequireFinite(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) && value.TryGetDouble(out var number) && double.IsFinite(number)
            ? number
            : throw new InvalidDataException($"Spectral data requires a finite number named '{name}'.");

    internal static ModelHostChannelMode ParseChannelMode(string value) => value switch
    {
        "linked" => ModelHostChannelMode.LinkedStereo,
        "independent" => ModelHostChannelMode.IndependentChannels,
        "mid-side" => ModelHostChannelMode.MidSide,
        _ => throw new InvalidDataException("Unsupported spectral stereo representation.")
    };
}

internal static class SpectralEditStore
{
    internal const string MediaType = "application/vnd.pulsegen.spectral-edit+json";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter() }
    };

    public static async Task<SpectralEditState> LoadLatestAsync(
        SceneDocument scene,
        string projectDirectory,
        SpectralAnalysisSession session,
        CancellationToken cancellationToken = default)
    {
        var matches = new List<(SpectralEditDocument Document, string Sha256)>();
        foreach (var asset in scene.Assets.Where(asset =>
                     asset.Kind == AssetKind.ModelOutput &&
                     string.Equals(asset.MediaType, MediaType, StringComparison.OrdinalIgnoreCase) &&
                     asset.Provenance?.Contains(session.AnalysisReceiptSha256, StringComparison.OrdinalIgnoreCase) == true))
        {
            var file = await SpectralAnalysisCatalog.ResolveVerifiedAsync(projectDirectory, asset, cancellationToken);
            await using var stream = new FileStream(file.Path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var document = await JsonSerializer.DeserializeAsync<SpectralEditDocument>(stream, JsonOptions, cancellationToken)
                ?? throw new InvalidDataException("Spectral edit asset is empty.");
            Validate(document, session);
            matches.Add((document, asset.Sha256));
        }

        return matches.OrderByDescending(match => match.Document.UpdatedAtUtc)
            .ThenByDescending(match => match.Sha256, StringComparer.Ordinal)
            .Select(match => new SpectralEditState(match.Document, match.Sha256))
            .FirstOrDefault() ?? new(SpectralEditDocument.CreateDefault(session), null);
    }

    public static async Task<SpectralEditSaveResult> SaveAsync(
        SceneDocument scene,
        string projectDirectory,
        SpectralAnalysisSession session,
        SpectralEditState current,
        SpectralEditDocument pending,
        CancellationToken cancellationToken = default)
    {
        Validate(pending, session);
        var now = DateTimeOffset.UtcNow;
        var snapshot = pending with
        {
            EditId = Guid.NewGuid(),
            ParentEditSha256 = current.AssetSha256,
            CreatedAtUtc = pending.CreatedAtUtc == default ? now : pending.CreatedAtUtc,
            UpdatedAtUtc = now,
            TrackEdits = pending.TrackEdits.OrderBy(edit => edit.TrackId, StringComparer.Ordinal).ToList()
        };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(snapshot, JsonOptions);
        var temporaryRoot = Path.Combine(Path.GetTempPath(), $"pulsegen-spectral-edit-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporaryRoot);
        var source = Path.Combine(temporaryRoot, "spectral-edit.json");
        try
        {
            await File.WriteAllBytesAsync(source, bytes, cancellationToken);
            var imported = await ProjectStore.ImportAssetAsync(
                projectDirectory,
                source,
                AssetKind.ModelOutput,
                MediaType,
                $"PulseGen spectral edit; analysisReceiptSha256={session.AnalysisReceiptSha256}; sourceSha256={session.SourceSha256}",
                cancellationToken: cancellationToken);
            var assets = scene.Assets.ToList();
            var existing = assets.FirstOrDefault(asset => string.Equals(asset.Sha256, imported.Sha256, StringComparison.OrdinalIgnoreCase));
            if (existing is null)
            {
                assets.Add(imported);
            }
            else if (existing.Kind != imported.Kind || !string.Equals(existing.MediaType, imported.MediaType, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("Spectral edit collides with an incompatible project asset.");
            }
            var updated = scene with { Assets = assets, UpdatedAtUtc = now };
            await ProjectStore.SaveAsync(updated, projectDirectory, cancellationToken);
            return new(updated, new(snapshot, imported.Sha256), imported);
        }
        finally
        {
            if (Directory.Exists(temporaryRoot))
            {
                Directory.Delete(temporaryRoot, recursive: true);
            }
        }
    }

    internal static void Validate(SpectralEditDocument document, SpectralAnalysisSession session)
    {
        if (document.SchemaVersion != 1 || document.EditId == Guid.Empty ||
            !string.Equals(document.AnalysisReceiptSha256, session.AnalysisReceiptSha256, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(document.SourceSha256, session.SourceSha256, StringComparison.OrdinalIgnoreCase) ||
            (document.ParentEditSha256 is not null &&
             (document.ParentEditSha256.Length != 64 || !document.ParentEditSha256.All(Uri.IsHexDigit))))
        {
            throw new InvalidDataException("Spectral edit identity does not match the selected analysis.");
        }
        ValidateLayer(document.Partials, "partials");
        ValidateLayer(document.Transients, "transients");
        ValidateLayer(document.StochasticResidual, "stochastic residual");
        var trackIds = session.Tracks.Select(track => track.Id).ToHashSet(StringComparer.Ordinal);
        var editedIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var edit in document.TrackEdits)
        {
            if (!trackIds.Contains(edit.TrackId) || !editedIds.Add(edit.TrackId) ||
                !double.IsFinite(edit.GainLinear) || edit.GainLinear is < 0 or > 4 ||
                !double.IsFinite(edit.PitchSemitones) || edit.PitchSemitones is < -24 or > 24)
            {
                throw new InvalidDataException($"Spectral edit for track '{edit.TrackId}' is invalid.");
            }
        }
    }

    private static void ValidateLayer(SpectralLayerSettings layer, string name)
    {
        if (!double.IsFinite(layer.GainLinear) || layer.GainLinear is < 0 or > 4)
        {
            throw new InvalidDataException($"Spectral {name} gain must be in 0..4.");
        }
    }
}

internal static class SpectralResynthesisService
{
    public static async Task<SpectralRenderResult> RenderAsync(
        SpectralAnalysisSession session,
        SpectralEditDocument edit,
        CancellationToken cancellationToken = default)
    {
        SpectralEditStore.Validate(edit, session);
        await VerifySessionAudioAsync(session, cancellationToken);
        return await Task.Run(() => RenderCore(session, edit, cancellationToken), cancellationToken);
    }

    private static async Task VerifySessionAudioAsync(SpectralAnalysisSession session, CancellationToken cancellationToken)
    {
        foreach (var file in new[] { session.Partials, session.Transients, session.StochasticResidual })
        {
            var hash = await ProjectStore.ComputeSha256Async(file.Path, cancellationToken);
            if (!string.Equals(hash, file.Asset.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"Spectral layer {file.Asset.FileName} changed after project import.");
            }
        }
    }

    private static SpectralRenderResult RenderCore(
        SpectralAnalysisSession session,
        SpectralEditDocument edit,
        CancellationToken cancellationToken)
    {
        var partials = FloatWaveFile.Read(session.Partials.Path);
        var transients = FloatWaveFile.Read(session.Transients.Path);
        var stochastic = FloatWaveFile.Read(session.StochasticResidual.Path);
        EnsureAligned(session, partials, transients, stochastic);
        var output = new float[partials.InterleavedSamples.Length];
        AddLayer(output, partials.InterleavedSamples, edit.Partials, cancellationToken);
        AddLayer(output, transients.InterleavedSamples, edit.Transients, cancellationToken);
        AddLayer(output, stochastic.InterleavedSamples, edit.StochasticResidual, cancellationToken);

        if (edit.Partials.Enabled && edit.Partials.GainLinear > 0)
        {
            var tracksById = session.Tracks.ToDictionary(track => track.Id, StringComparer.Ordinal);
            foreach (var trackEdit in edit.TrackEdits)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (trackEdit.Enabled && Math.Abs(trackEdit.GainLinear - 1) < 1.0e-12 && Math.Abs(trackEdit.PitchSemitones) < 1.0e-12)
                {
                    continue;
                }
                AddTrackDelta(
                    output,
                    partials.SampleRate,
                    tracksById[trackEdit.TrackId],
                    trackEdit,
                    session.ChannelMode,
                    edit.Partials.GainLinear,
                    cancellationToken);
            }
        }

        double peak = 0;
        double sumLeft = 0;
        double sumRight = 0;
        for (var index = 0; index < output.Length; index += 2)
        {
            output[index] = Math.Clamp(output[index], -4, 4);
            output[index + 1] = Math.Clamp(output[index + 1], -4, 4);
            peak = Math.Max(peak, Math.Max(Math.Abs(output[index]), Math.Abs(output[index + 1])));
            sumLeft += output[index] * output[index];
            sumRight += output[index + 1] * output[index + 1];
        }
        var frames = output.Length / 2;
        return new(
            new FloatWaveBuffer(partials.SampleRate, 2, output),
            peak,
            Math.Sqrt(sumLeft / frames),
            Math.Sqrt(sumRight / frames));
    }

    private static void EnsureAligned(
        SpectralAnalysisSession session,
        FloatWaveBuffer partials,
        FloatWaveBuffer transients,
        FloatWaveBuffer stochastic)
    {
        if (partials.SampleRate != session.SampleRate || partials.Channels != 2 || partials.Frames != session.Frames ||
            transients.SampleRate != partials.SampleRate || transients.Channels != 2 || transients.Frames != partials.Frames ||
            stochastic.SampleRate != partials.SampleRate || stochastic.Channels != 2 || stochastic.Frames != partials.Frames)
        {
            throw new InvalidDataException("Spectral layers no longer share the source sample rate, duration, and stereo alignment.");
        }
    }

    private static void AddLayer(
        float[] output,
        float[] layer,
        SpectralLayerSettings settings,
        CancellationToken cancellationToken)
    {
        if (!settings.Enabled || settings.GainLinear == 0)
        {
            return;
        }
        for (var index = 0; index < output.Length; index++)
        {
            if ((index & 0x1FFFF) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }
            output[index] += (float)(layer[index] * settings.GainLinear);
        }
    }

    private static void AddTrackDelta(
        float[] output,
        int sampleRate,
        SpectralPartialTrack track,
        SpectralTrackEdit edit,
        ModelHostChannelMode channelMode,
        double partialLayerGain,
        CancellationToken cancellationToken)
    {
        var pitchScale = Math.Pow(2, edit.PitchSemitones / 12.0);
        var modifiedGain = edit.Enabled ? edit.GainLinear : 0;
        var firstTime = track.Points[0].TimeSeconds;
        var lastTime = track.Points[^1].TimeSeconds;
        var frameCapacity = output.Length / 2;
        for (var segment = 0; segment < track.Points.Count - 1; segment++)
        {
            var first = track.Points[segment];
            var second = track.Points[segment + 1];
            var startFrame = Math.Clamp((int)Math.Ceiling(first.TimeSeconds * sampleRate), 0, frameCapacity);
            var endFrame = Math.Clamp((int)Math.Ceiling(second.TimeSeconds * sampleRate), startFrame, frameCapacity);
            var segmentDuration = second.TimeSeconds - first.TimeSeconds;
            if (segmentDuration <= 0 || startFrame == endFrame)
            {
                continue;
            }
            var expectedPhaseAdvance = 2 * Math.PI * 0.5 * (first.FrequencyHz + second.FrequencyHz) * segmentDuration;
            var rawPhaseAdvance = second.PhaseRadians - first.PhaseRadians;
            var turns = Math.Round((expectedPhaseAdvance - rawPhaseAdvance) / (2 * Math.PI));
            var phaseAdvance = rawPhaseAdvance + (turns * 2 * Math.PI);
            for (var frame = startFrame; frame < endFrame; frame++)
            {
                if ((frame & 0xFFFF) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }
                var time = (double)frame / sampleRate;
                var amount = Math.Clamp((time - first.TimeSeconds) / segmentDuration, 0, 1);
                var amplitude = first.Amplitude + ((second.Amplitude - first.Amplitude) * amount);
                var edgeGain = Math.Clamp(Math.Min(time - firstTime, lastTime - time) / 0.005, 0, 1);
                var original = amplitude * edgeGain * Math.Cos(first.PhaseRadians + (phaseAdvance * amount));
                var modified = amplitude * edgeGain * modifiedGain * Math.Cos(first.PhaseRadians + (phaseAdvance * pitchScale * amount));
                var delta = (modified - original) * partialLayerGain;
                AddRepresentedSample(output, frame, track.Channel, delta, channelMode);
            }
        }
    }

    private static void AddRepresentedSample(
        float[] output,
        int frame,
        int channel,
        double sample,
        ModelHostChannelMode mode)
    {
        var index = frame * 2;
        if (mode == ModelHostChannelMode.MidSide)
        {
            const double scale = 0.7071067811865475244;
            if (channel == 0)
            {
                output[index] += (float)(sample * scale);
                output[index + 1] += (float)(sample * scale);
            }
            else
            {
                output[index] += (float)(sample * scale);
                output[index + 1] -= (float)(sample * scale);
            }
        }
        else
        {
            output[index + channel] += (float)sample;
        }
    }
}
