using PulseGen.Contracts;
using System.Text.Json;

namespace PulseGen.Editor;

internal sealed record ReceiverTakeSession(
    CaptureTake Take,
    ReceiverRig Receiver,
    AssetRef SourceAsset,
    AssetRef LeftAsset,
    AssetRef RightAsset,
    AssetRef ReceiptAsset,
    string SourcePath,
    string LeftPath,
    string RightPath,
    string ReceiptPath,
    IReadOnlyList<float> LeftEnvelope,
    IReadOnlyList<float> RightEnvelope)
{
    public int SampleRate => LeftAsset.SampleRate ?? 0;
    public double DurationSeconds => Math.Min(LeftAsset.DurationSeconds ?? 0, RightAsset.DurationSeconds ?? 0);
    public double EffectiveTrimEndSeconds => Take.TrimEndSeconds ?? DurationSeconds;
    public double ArrangedEndSeconds => Take.TimelineOffsetSeconds + Math.Max(0, EffectiveTrimEndSeconds - Take.TrimStartSeconds);
    public string DisplayName => Take.Name;
    public override string ToString() => DisplayName;
}

internal sealed record CaptureImportResult(SceneDocument Scene, int ImportedTakeCount, int AddedAssetCount, int ReusedAssetCount);

internal sealed record ReceiverMixResult(
    FloatWaveBuffer Buffer,
    double Peak,
    double Rms,
    IReadOnlyList<Guid> TakeIds);

internal enum ReceiverAuditionMode
{
    Stereo,
    LeftCentered,
    RightCentered
}

internal static class CaptureTakeImporter
{
    private const int ReceiptVersion = 8;
    private const string ReceiptMediaType = "application/vnd.pulsegen.render-receipt+json";

    public static async Task<CaptureImportResult> ImportAsync(
        SceneDocument scene,
        string projectDirectory,
        string receiptPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scene);
        SceneValidator.EnsureValid(scene);
        _ = ProjectStore.ResolveManifestPath(projectDirectory);
        var receiptFile = new FileInfo(Path.GetFullPath(receiptPath));
        if (!receiptFile.Exists || receiptFile.Length is <= 0 or > 16 * 1024 * 1024)
        {
            throw new InvalidDataException("The render receipt is missing, empty, or larger than the bounded 16 MiB receipt limit.");
        }

        var verified = await ReadAndVerifyReceiptAsync(scene, receiptFile.FullName, cancellationToken);
        var receiptAsset = await ProjectStore.ImportAssetAsync(
            projectDirectory,
            receiptFile.FullName,
            AssetKind.Render,
            ReceiptMediaType,
            $"scene-revision={verified.SceneRevisionSha256}; profile={verified.RenderProfileId:D}",
            cancellationToken: cancellationToken);

        var assets = scene.Assets.ToList();
        var addedAssets = 0;
        var reusedAssets = 0;
        receiptAsset = MergeAsset(assets, receiptAsset, ref addedAssets, ref reusedAssets);
        var takes = scene.CaptureTakes.ToList();
        var importedAt = DateTimeOffset.UtcNow;
        var temporaryRoot = Path.Combine(Path.GetFullPath(projectDirectory), "renders", $"capture-import-{Guid.NewGuid():N}.tmp");
        Directory.CreateDirectory(temporaryRoot);
        try
        {
            foreach (var rendered in verified.Takes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var stereo = FloatWaveFile.Read(rendered.Path);
                if (stereo.Channels != 2 || stereo.SampleRate != verified.SampleRate || (ulong)stereo.Frames != verified.FramesRendered)
                {
                    throw new InvalidDataException($"Rendered take '{rendered.FileName}' does not match the receipt's stereo format or frame count.");
                }

                var sourceAsset = await ProjectStore.ImportAssetAsync(
                    projectDirectory,
                    rendered.Path,
                    AssetKind.Render,
                    "audio/wav",
                    $"render-receipt={receiptAsset.Sha256}; receiver={rendered.ReceiverRigId:D}; channel=stereo-source",
                    cancellationToken: cancellationToken);
                sourceAsset = sourceAsset with
                {
                    SampleRate = stereo.SampleRate,
                    Channels = 2,
                    DurationSeconds = stereo.DurationSeconds
                };
                sourceAsset = MergeAsset(assets, sourceAsset, ref addedAssets, ref reusedAssets);

                var leftSamples = new float[stereo.Frames];
                var rightSamples = new float[stereo.Frames];
                for (var frame = 0; frame < stereo.Frames; frame++)
                {
                    leftSamples[frame] = stereo.InterleavedSamples[frame * 2];
                    rightSamples[frame] = stereo.InterleavedSamples[(frame * 2) + 1];
                }

                var safeName = SafeName(rendered.ReceiverName);
                var leftPath = Path.Combine(temporaryRoot, $"{safeName}-{rendered.ReceiverRigId:N}-L.wav");
                var rightPath = Path.Combine(temporaryRoot, $"{safeName}-{rendered.ReceiverRigId:N}-R.wav");
                FloatWaveFile.WriteAtomic(leftPath, new FloatWaveBuffer(stereo.SampleRate, 1, leftSamples));
                FloatWaveFile.WriteAtomic(rightPath, new FloatWaveBuffer(stereo.SampleRate, 1, rightSamples));

                var leftAsset = await ImportChannelAsync(projectDirectory, leftPath, receiptAsset.Sha256, rendered.ReceiverRigId, "left", stereo, cancellationToken);
                var rightAsset = await ImportChannelAsync(projectDirectory, rightPath, receiptAsset.Sha256, rendered.ReceiverRigId, "right", stereo, cancellationToken);
                leftAsset = MergeAsset(assets, leftAsset, ref addedAssets, ref reusedAssets);
                rightAsset = MergeAsset(assets, rightAsset, ref addedAssets, ref reusedAssets);

                var receiverTakeNumber = takes.Count(take => take.ReceiverRigId == rendered.ReceiverRigId) + 1;
                takes.Add(new CaptureTake
                {
                    Id = Guid.NewGuid(),
                    ReceiverRigId = rendered.ReceiverRigId,
                    Name = $"{rendered.ReceiverName} take {receiverTakeNumber}",
                    SourceAssetHash = sourceAsset.Sha256,
                    LeftAssetHash = leftAsset.Sha256,
                    RightAssetHash = rightAsset.Sha256,
                    ChannelsLinked = true,
                    IncludedInMaster = true,
                    RenderedAtUtc = importedAt,
                    RenderReceiptHash = receiptAsset.Sha256
                });
            }

            var updated = scene with
            {
                Assets = assets,
                CaptureTakes = takes,
                UpdatedAtUtc = importedAt
            };
            SceneValidator.EnsureValid(updated);
            await ProjectStore.SaveAsync(updated, projectDirectory, cancellationToken);
            return new(updated, verified.Takes.Count, addedAssets, reusedAssets);
        }
        finally
        {
            if (Directory.Exists(temporaryRoot))
            {
                Directory.Delete(temporaryRoot, recursive: true);
            }
        }
    }

    private static async Task<AssetRef> ImportChannelAsync(
        string projectDirectory,
        string path,
        string receiptHash,
        Guid receiverRigId,
        string channel,
        FloatWaveBuffer source,
        CancellationToken cancellationToken)
    {
        var asset = await ProjectStore.ImportAssetAsync(
            projectDirectory,
            path,
            AssetKind.Render,
            "audio/wav",
            $"render-receipt={receiptHash}; receiver={receiverRigId:D}; channel={channel}",
            cancellationToken: cancellationToken);
        return asset with
        {
            SampleRate = source.SampleRate,
            Channels = 1,
            DurationSeconds = source.DurationSeconds
        };
    }

    private static AssetRef MergeAsset(List<AssetRef> assets, AssetRef candidate, ref int added, ref int reused)
    {
        var existing = assets.FirstOrDefault(asset => string.Equals(asset.Sha256, candidate.Sha256, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            if (existing.Kind != candidate.Kind || !string.Equals(existing.MediaType, candidate.MediaType, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("A rendered artifact collides with an existing asset that has incompatible metadata.");
            }
            reused++;
            return existing;
        }
        assets.Add(candidate);
        added++;
        return candidate;
    }

    private static async Task<VerifiedReceipt> ReadAndVerifyReceiptAsync(
        SceneDocument scene,
        string receiptPath,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(receiptPath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var root = document.RootElement;
        if (root.GetProperty("receiptVersion").GetInt32() != ReceiptVersion ||
            root.GetProperty("projectId").GetGuid() != scene.ProjectId)
        {
            throw new InvalidDataException("The render receipt version or project identity does not match this project.");
        }
        var sceneRevision = root.GetProperty("sceneRevisionSha256").GetString() ?? string.Empty;
        if (!string.Equals(sceneRevision, ProjectStore.ComputeSceneRevisionSha256(scene), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The rendered takes belong to a different saved scene revision.");
        }
        var sampleRate = root.GetProperty("sampleRate").GetInt32();
        var framesRendered = root.GetProperty("framesRendered").GetUInt64();
        var profileId = root.GetProperty("renderProfileId").GetGuid();
        var profile = scene.RenderProfiles.SingleOrDefault(candidate => candidate.Id == profileId);
        var expectedFrames = sampleRate is < 8000 or > 192000
            ? 0UL
            : checked((ulong)Math.Round(scene.Timeline.DurationSeconds * sampleRate, MidpointRounding.AwayFromZero));
        if (profile is null || profile.SampleRate != sampleRate || framesRendered != expectedFrames || framesRendered is 0 or > int.MaxValue)
        {
            throw new InvalidDataException("The render receipt does not match a saved render profile, sample rate, or scene duration.");
        }

        var receiptDirectory = Path.GetDirectoryName(receiptPath)!;
        var enabledReceiverIds = scene.ReceiverRigs.Where(receiver => receiver.Enabled).Select(receiver => receiver.Id).ToHashSet();
        var renderedFileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var takes = new List<VerifiedRenderedTake>();
        foreach (var item in root.GetProperty("takes").EnumerateArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var receiverId = item.GetProperty("receiverRigId").GetGuid();
            var receiverName = item.GetProperty("receiverName").GetString() ?? string.Empty;
            var fileName = item.GetProperty("fileName").GetString() ?? string.Empty;
            var expectedHash = item.GetProperty("sha256").GetString() ?? string.Empty;
            var expectedSize = item.GetProperty("sizeBytes").GetInt64();
            var receiver = scene.ReceiverRigs.SingleOrDefault(candidate => candidate.Id == receiverId);
            if (receiver is null || !enabledReceiverIds.Remove(receiverId) ||
                !string.Equals(receiver.Name, receiverName, StringComparison.Ordinal) ||
                string.IsNullOrWhiteSpace(fileName) || !renderedFileNames.Add(fileName) ||
                !string.Equals(fileName, Path.GetFileName(fileName), StringComparison.Ordinal) ||
                expectedHash.Length != 64 || !expectedHash.All(Uri.IsHexDigit))
            {
                throw new InvalidDataException("The render receipt contains an invalid, duplicate, or unknown receiver take identity.");
            }
            var path = Path.Combine(receiptDirectory, fileName);
            var file = new FileInfo(path);
            if (!file.Exists || file.Length != expectedSize ||
                !string.Equals(await ProjectStore.ComputeSha256Async(path, cancellationToken), expectedHash, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"Rendered take '{fileName}' failed its size or SHA-256 check.");
            }
            takes.Add(new(receiverId, receiverName, fileName, path));
        }
        if (takes.Count == 0 || enabledReceiverIds.Count != 0 || root.GetProperty("filesWritten").GetUInt32() != takes.Count)
        {
            throw new InvalidDataException("The render receipt does not contain exactly one take for every enabled receiver rig.");
        }
        return new(sceneRevision, profileId, sampleRate, framesRendered, takes);
    }

    private static string SafeName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        var result = new string(value.Select(character => invalid.Contains(character) ? '_' : character).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(result) ? "Receiver" : result;
    }

    private sealed record VerifiedRenderedTake(Guid ReceiverRigId, string ReceiverName, string FileName, string Path);
    private sealed record VerifiedReceipt(string SceneRevisionSha256, Guid RenderProfileId, int SampleRate, ulong FramesRendered, IReadOnlyList<VerifiedRenderedTake> Takes);
}

internal static class ReceiverTakeCatalog
{
    public static async Task<IReadOnlyList<ReceiverTakeSession>> LoadAsync(
        SceneDocument scene,
        string projectDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scene);
        SceneValidator.EnsureValid(scene);
        var assets = scene.Assets.ToDictionary(asset => asset.Sha256, StringComparer.OrdinalIgnoreCase);
        var receivers = scene.ReceiverRigs.ToDictionary(receiver => receiver.Id);
        var verifiedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var sessions = new List<ReceiverTakeSession>(scene.CaptureTakes.Count);
        foreach (var take in scene.CaptureTakes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var receiver = receivers[take.ReceiverRigId];
            var source = assets[take.SourceAssetHash!];
            var left = assets[take.LeftAssetHash!];
            var right = assets[take.RightAssetHash!];
            var receipt = assets[take.RenderReceiptHash!];
            var sourcePath = ProjectStore.ResolveAssetPath(projectDirectory, source.Sha256);
            var leftPath = ProjectStore.ResolveAssetPath(projectDirectory, left.Sha256);
            var rightPath = ProjectStore.ResolveAssetPath(projectDirectory, right.Sha256);
            var receiptPath = ProjectStore.ResolveAssetPath(projectDirectory, receipt.Sha256);
            foreach (var (asset, path) in new[] { (source, sourcePath), (left, leftPath), (right, rightPath), (receipt, receiptPath) })
            {
                if (!verifiedPaths.Add(path))
                {
                    continue;
                }
                var file = new FileInfo(path);
                if (!file.Exists || file.Length != asset.SizeBytes ||
                    !string.Equals(await ProjectStore.ComputeSha256Async(path, cancellationToken), asset.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException($"Receiver take asset '{asset.FileName}' failed its immutable size or SHA-256 check.");
                }
            }
            var leftEnvelope = BuildEnvelope(leftPath, left, 192);
            var rightEnvelope = BuildEnvelope(rightPath, right, 192);
            sessions.Add(new(take, receiver, source, left, right, receipt, sourcePath, leftPath, rightPath, receiptPath, leftEnvelope, rightEnvelope));
        }
        return sessions
            .OrderByDescending(session => session.Take.RenderedAtUtc ?? DateTimeOffset.MinValue)
            .ThenBy(session => session.Take.Name, StringComparer.Ordinal)
            .ToArray();
    }

    private static IReadOnlyList<float> BuildEnvelope(string path, AssetRef asset, int bins)
    {
        var audio = FloatWaveFile.Read(path);
        if (audio.Channels != 1 || audio.SampleRate != asset.SampleRate ||
            Math.Abs(audio.DurationSeconds - (asset.DurationSeconds ?? -1)) > 0.5 / audio.SampleRate)
        {
            throw new InvalidDataException($"Receiver channel '{asset.FileName}' does not match its declared mono audio metadata.");
        }
        var result = new float[Math.Min(bins, audio.Frames)];
        for (var bin = 0; bin < result.Length; bin++)
        {
            var start = (long)bin * audio.Frames / result.Length;
            var end = Math.Max(start + 1, (long)(bin + 1) * audio.Frames / result.Length);
            var peak = 0f;
            for (var frame = start; frame < end; frame++)
            {
                peak = Math.Max(peak, Math.Abs(audio.InterleavedSamples[checked((int)frame)]));
            }
            result[bin] = peak;
        }
        return result;
    }
}

internal static class ReceiverTakeMixer
{
    public static async Task<ReceiverMixResult> RenderTakeAsync(
        ReceiverTakeSession session,
        ReceiverAuditionMode mode,
        bool includeTimelineOffset = false,
        CancellationToken cancellationToken = default)
    {
        var channels = await LoadChannelsAsync(session, cancellationToken);
        var (startFrame, endFrame) = ResolveTrimFrames(session, channels.Left.Frames);
        var offsetFrames = includeTimelineOffset
            ? checked((int)Math.Round(session.Take.TimelineOffsetSeconds * session.SampleRate, MidpointRounding.AwayFromZero))
            : 0;
        var frames = checked(offsetFrames + endFrame - startFrame);
        var output = new float[checked(frames * 2)];
        for (var sourceFrame = startFrame; sourceFrame < endFrame; sourceFrame++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var targetFrame = offsetFrames + sourceFrame - startFrame;
            var left = session.Take.LeftMuted ? 0f : channels.Left.InterleavedSamples[sourceFrame] * (float)session.Take.LeftGainLinear;
            var right = session.Take.RightMuted ? 0f : channels.Right.InterleavedSamples[sourceFrame] * (float)session.Take.RightGainLinear;
            switch (mode)
            {
                case ReceiverAuditionMode.LeftCentered:
                    output[targetFrame * 2] = left;
                    output[(targetFrame * 2) + 1] = left;
                    break;
                case ReceiverAuditionMode.RightCentered:
                    output[targetFrame * 2] = right;
                    output[(targetFrame * 2) + 1] = right;
                    break;
                default:
                    output[targetFrame * 2] = left;
                    output[(targetFrame * 2) + 1] = right;
                    break;
            }
        }
        return Measure(new FloatWaveBuffer(session.SampleRate, 2, output), [session.Take.Id]);
    }

    public static async Task<FloatWaveBuffer> RenderChannelAsync(
        ReceiverTakeSession session,
        bool leftChannel,
        bool includeTimelineOffset,
        CancellationToken cancellationToken = default)
    {
        var channels = await LoadChannelsAsync(session, cancellationToken);
        var (startFrame, endFrame) = ResolveTrimFrames(session, channels.Left.Frames);
        var offsetFrames = includeTimelineOffset
            ? checked((int)Math.Round(session.Take.TimelineOffsetSeconds * session.SampleRate, MidpointRounding.AwayFromZero))
            : 0;
        var output = new float[checked(offsetFrames + endFrame - startFrame)];
        var source = leftChannel ? channels.Left.InterleavedSamples : channels.Right.InterleavedSamples;
        var muted = leftChannel ? session.Take.LeftMuted : session.Take.RightMuted;
        var gain = (float)(leftChannel ? session.Take.LeftGainLinear : session.Take.RightGainLinear);
        if (!muted)
        {
            for (var sourceFrame = startFrame; sourceFrame < endFrame; sourceFrame++)
            {
                output[offsetFrames + sourceFrame - startFrame] = source[sourceFrame] * gain;
            }
        }
        return new(session.SampleRate, 1, output);
    }

    public static async Task<ReceiverMixResult> BuildMasterAsync(
        IReadOnlyList<ReceiverTakeSession> sessions,
        CancellationToken cancellationToken = default)
    {
        var included = sessions.Where(session => session.Take.IncludedInMaster).ToArray();
        if (included.Length == 0)
        {
            throw new InvalidOperationException("Select at least one receiver take for the stereo master.");
        }
        var sampleRate = included[0].SampleRate;
        if (sampleRate <= 0 || included.Any(session => session.SampleRate != sampleRate))
        {
            throw new InvalidOperationException("Selected receiver takes use different sample rates. Keep one sample rate per master for this milestone.");
        }

        var prepared = new List<(ReceiverTakeSession Session, FloatWaveBuffer Left, FloatWaveBuffer Right, int Start, int End, int Offset)>();
        var totalFrames = 0;
        foreach (var session in included)
        {
            var channels = await LoadChannelsAsync(session, cancellationToken);
            var (start, end) = ResolveTrimFrames(session, channels.Left.Frames);
            var offset = checked((int)Math.Round(session.Take.TimelineOffsetSeconds * sampleRate, MidpointRounding.AwayFromZero));
            totalFrames = Math.Max(totalFrames, checked(offset + end - start));
            prepared.Add((session, channels.Left, channels.Right, start, end, offset));
        }
        if (totalFrames <= 0 || totalFrames > int.MaxValue / 2)
        {
            throw new InvalidOperationException("The arranged master is empty or too large for the bounded in-memory renderer.");
        }

        var output = new float[checked(totalFrames * 2)];
        foreach (var item in prepared)
        {
            var leftGain = item.Session.Take.LeftMuted ? 0 : item.Session.Take.LeftGainLinear;
            var rightGain = item.Session.Take.RightMuted ? 0 : item.Session.Take.RightGainLinear;
            for (var sourceFrame = item.Start; sourceFrame < item.End; sourceFrame++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var targetFrame = item.Offset + sourceFrame - item.Start;
                var target = targetFrame * 2;
                output[target] = checked((float)((double)output[target] + item.Left.InterleavedSamples[sourceFrame] * leftGain));
                output[target + 1] = checked((float)((double)output[target + 1] + item.Right.InterleavedSamples[sourceFrame] * rightGain));
            }
        }
        return Measure(new FloatWaveBuffer(sampleRate, 2, output), included.Select(session => session.Take.Id).ToArray());
    }

    private static async Task<(FloatWaveBuffer Left, FloatWaveBuffer Right)> LoadChannelsAsync(
        ReceiverTakeSession session,
        CancellationToken cancellationToken)
    {
        foreach (var (asset, path) in new[] { (session.LeftAsset, session.LeftPath), (session.RightAsset, session.RightPath) })
        {
            if (!string.Equals(await ProjectStore.ComputeSha256Async(path, cancellationToken), asset.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"Receiver channel '{asset.FileName}' changed after it was loaded.");
            }
        }
        var left = FloatWaveFile.Read(session.LeftPath);
        var right = FloatWaveFile.Read(session.RightPath);
        if (left.Channels != 1 || right.Channels != 1 || left.SampleRate != right.SampleRate || left.Frames != right.Frames || left.SampleRate != session.SampleRate)
        {
            throw new InvalidDataException("Receiver take channels no longer have matching mono format, sample rate, and duration.");
        }
        return (left, right);
    }

    private static (int Start, int End) ResolveTrimFrames(ReceiverTakeSession session, int frames)
    {
        var start = Math.Clamp(checked((int)Math.Round(session.Take.TrimStartSeconds * session.SampleRate, MidpointRounding.AwayFromZero)), 0, frames);
        var endSeconds = session.Take.TrimEndSeconds ?? session.DurationSeconds;
        var end = Math.Clamp(checked((int)Math.Round(endSeconds * session.SampleRate, MidpointRounding.AwayFromZero)), 0, frames);
        if (end <= start)
        {
            throw new InvalidOperationException($"Receiver take '{session.Take.Name}' has an empty trim range.");
        }
        return (start, end);
    }

    private static ReceiverMixResult Measure(FloatWaveBuffer buffer, IReadOnlyList<Guid> takeIds)
    {
        var peak = 0d;
        var sumSquares = 0d;
        foreach (var sample in buffer.InterleavedSamples)
        {
            peak = Math.Max(peak, Math.Abs(sample));
            sumSquares += (double)sample * sample;
        }
        return new(buffer, peak, Math.Sqrt(sumSquares / buffer.InterleavedSamples.Length), takeIds);
    }
}
