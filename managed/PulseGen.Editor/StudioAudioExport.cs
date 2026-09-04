using PulseGen.Contracts;
using System.Buffers.Binary;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PulseGen.Editor;

internal enum StudioExportContainer
{
    Wave,
    BroadcastWave,
    Rf64BroadcastWave,
    Aiff,
    Flac,
    Mp3,
    OggVorbis,
    Opus
}

internal enum StudioSampleEncoding
{
    Pcm16,
    Pcm24,
    Pcm32,
    Float32,
    LossyHigh,
    LossyCompact
}

internal sealed record StudioContainerChoice(
    StudioExportContainer Container,
    string Name,
    string Extension,
    string Description)
{
    public override string ToString() => Name;
}

internal sealed record StudioEncodingChoice(
    StudioSampleEncoding Encoding,
    string Name)
{
    public override string ToString() => Name;
}

internal sealed record StudioDitherChoice(DitherMode Dither, string Name)
{
    public override string ToString() => Name;
}

internal static class StudioExportChoices
{
    public static IReadOnlyList<StudioContainerChoice> Containers { get; } =
    [
        new(StudioExportContainer.Wave, "WAV", ".wav", "Standard WAVE; PCM or 32-bit float."),
        new(StudioExportContainer.BroadcastWave, "BWF", ".wav", "Broadcast WAVE with embedded provenance and loudness fields."),
        new(StudioExportContainer.Rf64BroadcastWave, "RF64 / BWF", ".wav", "Large-file broadcast WAVE using 64-bit size fields."),
        new(StudioExportContainer.Aiff, "AIFF", ".aiff", "Big-endian uncompressed PCM for studio interchange."),
        new(StudioExportContainer.Flac, "FLAC", ".flac", "Lossless compression with explicit 16-bit or 24-bit PCM precision."),
        new(StudioExportContainer.Mp3, "MP3", ".mp3", "Widely compatible lossy delivery using deterministic LAME variable-bitrate encoding."),
        new(StudioExportContainer.OggVorbis, "Ogg Vorbis", ".ogg", "Open lossy delivery format with a canonical, repeatable Ogg stream identity."),
        new(StudioExportContainer.Opus, "Opus", ".opus", "Efficient open delivery codec; unsupported source rates are explicitly converted to an Opus rate.")
    ];

    public static IReadOnlyList<StudioEncodingChoice> Encodings { get; } =
    [
        new(StudioSampleEncoding.Pcm16, "16-bit PCM"),
        new(StudioSampleEncoding.Pcm24, "24-bit PCM"),
        new(StudioSampleEncoding.Pcm32, "32-bit PCM"),
        new(StudioSampleEncoding.Float32, "32-bit float")
    ];

    public static IReadOnlyList<StudioEncodingChoice> LosslessEncodings { get; } =
    [
        new(StudioSampleEncoding.Pcm16, "16-bit PCM"),
        new(StudioSampleEncoding.Pcm24, "24-bit PCM")
    ];

    public static IReadOnlyList<StudioEncodingChoice> LossyEncodings { get; } =
    [
        new(StudioSampleEncoding.LossyHigh, "High-quality VBR"),
        new(StudioSampleEncoding.LossyCompact, "Compact VBR")
    ];

    public static IReadOnlyList<StudioDitherChoice> Dithers { get; } =
    [
        new(DitherMode.Tpdf, "TPDF"),
        new(DitherMode.None, "None")
    ];

    public static IReadOnlyList<StudioEncodingChoice> EncodingsFor(StudioExportContainer container) => container switch
    {
        StudioExportContainer.Wave => Encodings,
        StudioExportContainer.Flac => LosslessEncodings,
        StudioExportContainer.Mp3 or StudioExportContainer.OggVorbis or StudioExportContainer.Opus => LossyEncodings,
        _ => Encodings.Where(choice => choice.Encoding != StudioSampleEncoding.Float32).ToArray()
    };

    public static bool IsLossy(StudioExportContainer container) =>
        container is StudioExportContainer.Mp3 or StudioExportContainer.OggVorbis or StudioExportContainer.Opus;

    public static bool UsesCodecAdapter(StudioExportContainer container) =>
        container is StudioExportContainer.Flac or StudioExportContainer.Mp3 or StudioExportContainer.OggVorbis or StudioExportContainer.Opus;
}

internal sealed record StudioExportRequest
{
    public required string DestinationPath { get; init; }
    public required StudioExportContainer Container { get; init; }
    public required StudioSampleEncoding Encoding { get; init; }
    public required DitherMode Dither { get; init; }
    public required ulong DitherSeed { get; init; }
    public required Guid ProjectId { get; init; }
    public required string ProjectName { get; init; }
    public required string SceneRevisionSha256 { get; init; }
    public required IReadOnlyList<Guid> TakeIds { get; init; }
    public required DateTimeOffset CreatedAtUtc { get; init; }
    public string EngineVersion { get; init; } =
        typeof(StudioMasterExporter).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";
    public ulong TimeReferenceSamples { get; init; }
}

internal sealed record StudioExportResult(
    string OutputPath,
    string ReceiptPath,
    string Sha256,
    long SizeBytes,
    StudioAudioMetrics Metrics,
    int SampleRate,
    int Frames,
    int? BitsPerSample,
    int MeasurementSourceSampleRate,
    int MeasurementSourceFrames,
    bool Lossy,
    string EncoderName,
    string EncoderVersion,
    string EncoderSettings,
    string? SampleRateConversion,
    IReadOnlyList<Guid> TakeIds,
    long QuantizerSaturationCount);

internal sealed record StudioExportImportResult(
    SceneDocument Scene,
    AssetRef AudioAsset,
    AssetRef ReceiptAsset);

internal sealed record StudioExportReceipt
{
    public int ReceiptVersion { get; init; } = 2;
    public required Guid ProjectId { get; init; }
    public required string ProjectName { get; init; }
    public required string SceneRevisionSha256 { get; init; }
    public required IReadOnlyList<Guid> TakeIds { get; init; }
    public required string OutputFileName { get; init; }
    public required string OutputSha256 { get; init; }
    public required long OutputSizeBytes { get; init; }
    public required StudioExportContainer Container { get; init; }
    public required StudioSampleEncoding Encoding { get; init; }
    public int? BitsPerSample { get; init; }
    public required int SampleRate { get; init; }
    public int Channels { get; init; } = 2;
    public required int Frames { get; init; }
    public required double DurationSeconds { get; init; }
    public required int MeasurementSourceSampleRate { get; init; }
    public required int MeasurementSourceFrames { get; init; }
    public required bool Lossy { get; init; }
    public required string EncoderName { get; init; }
    public required string EncoderVersion { get; init; }
    public required string EncoderSettings { get; init; }
    public string? SampleRateConversion { get; init; }
    public required DitherMode Dither { get; init; }
    public required ulong DitherSeed { get; init; }
    public bool NormalizationApplied { get; init; }
    public string MeasurementStage { get; init; } = "post-mix/pre-encoding";
    public required StudioAudioMetrics Metrics { get; init; }
    public required long QuantizerSaturationCount { get; init; }
    public required DateTimeOffset CreatedAtUtc { get; init; }
    public required string EngineVersion { get; init; }
    public IReadOnlyList<string> MeasurementStandards { get; init; } =
    ["ITU-R BS.1770-5", "EBU R 128", "EBU Tech 3341"];
}

internal static class StudioMasterExporter
{
    private const uint RiffSizeSentinel = uint.MaxValue;
    private static readonly JsonSerializerOptions ReceiptOptions = CreateReceiptOptions();

    public static StudioExportResult Export(
        StudioExportRequest request,
        FloatWaveBuffer buffer,
        CancellationToken cancellationToken = default)
    {
        Validate(request, buffer);
        cancellationToken.ThrowIfCancellationRequested();
        var metrics = StudioAudioAnalyzer.Analyze(buffer);
        var output = Path.GetFullPath(request.DestinationPath);
        var directory = Path.GetDirectoryName(output) ?? throw new InvalidOperationException("The export destination needs a parent directory.");
        Directory.CreateDirectory(directory);
        var temporary = output + $".{Guid.NewGuid():N}.tmp";
        CodecWriteSummary writeSummary;
        try
        {
            if (StudioExportChoices.UsesCodecAdapter(request.Container))
            {
                writeSummary = SndFileCodecAdapter.Write(temporary, request, buffer, cancellationToken);
            }
            else
            {
                long quantizerSaturationCount;
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, FileOptions.WriteThrough))
                {
                    quantizerSaturationCount = request.Container switch
                    {
                        StudioExportContainer.Wave => WriteWave(stream, request, buffer, metrics, broadcast: false, rf64: false, cancellationToken),
                        StudioExportContainer.BroadcastWave => WriteWave(stream, request, buffer, metrics, broadcast: true, rf64: false, cancellationToken),
                        StudioExportContainer.Rf64BroadcastWave => WriteWave(stream, request, buffer, metrics, broadcast: true, rf64: true, cancellationToken),
                        StudioExportContainer.Aiff => WriteAiff(stream, request, buffer, cancellationToken),
                        _ => throw new ArgumentOutOfRangeException(nameof(request), "Unknown studio container.")
                    };
                    stream.Flush(flushToDisk: true);
                }
                writeSummary = new(
                    buffer.SampleRate,
                    buffer.Frames,
                    BitsPerSample(request.Encoding),
                    quantizerSaturationCount,
                    false,
                    "PulseGen direct writer",
                    request.EngineVersion,
                    DirectWriterSettings(request),
                    null);
            }
            File.Move(temporary, output, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }

        var file = new FileInfo(output);
        var hash = ComputeSha256(output);
        var receipt = new StudioExportReceipt
        {
            ProjectId = request.ProjectId,
            ProjectName = request.ProjectName,
            SceneRevisionSha256 = request.SceneRevisionSha256,
            TakeIds = request.TakeIds,
            OutputFileName = file.Name,
            OutputSha256 = hash,
            OutputSizeBytes = file.Length,
            Container = request.Container,
            Encoding = request.Encoding,
            BitsPerSample = writeSummary.BitsPerSample,
            SampleRate = writeSummary.SampleRate,
            Frames = writeSummary.Frames,
            DurationSeconds = (double)writeSummary.Frames / writeSummary.SampleRate,
            MeasurementSourceSampleRate = buffer.SampleRate,
            MeasurementSourceFrames = buffer.Frames,
            Lossy = writeSummary.Lossy,
            EncoderName = writeSummary.EncoderName,
            EncoderVersion = writeSummary.EncoderVersion,
            EncoderSettings = writeSummary.EncoderSettings,
            SampleRateConversion = writeSummary.SampleRateConversion,
            Dither = request.Dither,
            DitherSeed = request.DitherSeed,
            Metrics = metrics,
            QuantizerSaturationCount = writeSummary.QuantizerSaturationCount,
            CreatedAtUtc = request.CreatedAtUtc,
            EngineVersion = request.EngineVersion
        };
        var receiptPath = output + ".pulse-receipt.json";
        WriteReceiptAtomic(receiptPath, receipt);
        return new(
            output,
            receiptPath,
            hash,
            file.Length,
            metrics,
            writeSummary.SampleRate,
            writeSummary.Frames,
            writeSummary.BitsPerSample,
            buffer.SampleRate,
            buffer.Frames,
            writeSummary.Lossy,
            writeSummary.EncoderName,
            writeSummary.EncoderVersion,
            writeSummary.EncoderSettings,
            writeSummary.SampleRateConversion,
            request.TakeIds,
            writeSummary.QuantizerSaturationCount);
    }

    public static string MediaType(StudioExportContainer container) => container switch
    {
        StudioExportContainer.Aiff => "audio/aiff",
        StudioExportContainer.Flac => "audio/flac",
        StudioExportContainer.Mp3 => "audio/mpeg",
        StudioExportContainer.OggVorbis => "audio/ogg",
        StudioExportContainer.Opus => "audio/opus",
        _ => "audio/wav"
    };

    public static string Extension(StudioExportContainer container) => container switch
    {
        StudioExportContainer.Aiff => ".aiff",
        StudioExportContainer.Flac => ".flac",
        StudioExportContainer.Mp3 => ".mp3",
        StudioExportContainer.OggVorbis => ".ogg",
        StudioExportContainer.Opus => ".opus",
        _ => ".wav"
    };

    private static void Validate(StudioExportRequest request, FloatWaveBuffer buffer)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(buffer);
        if (buffer.Channels != 2 || buffer.SampleRate is < 8000 or > 192000 || buffer.Frames <= 0 ||
            buffer.InterleavedSamples.Any(sample => !float.IsFinite(sample)))
        {
            throw new ArgumentException("Studio export requires finite, non-empty stereo audio at 8–192 kHz.", nameof(buffer));
        }
        if (request.ProjectId == Guid.Empty || string.IsNullOrWhiteSpace(request.ProjectName) ||
            request.SceneRevisionSha256.Length != 64 || !request.SceneRevisionSha256.All(Uri.IsHexDigit) ||
            request.TakeIds.Count == 0 || request.TakeIds.Any(id => id == Guid.Empty) ||
            string.IsNullOrWhiteSpace(request.EngineVersion))
        {
            throw new ArgumentException("Studio export provenance is incomplete.", nameof(request));
        }
        var extension = Path.GetExtension(request.DestinationPath);
        var extensionMatches = request.Container == StudioExportContainer.Aiff
            ? extension.Equals(".aif", StringComparison.OrdinalIgnoreCase) || extension.Equals(".aiff", StringComparison.OrdinalIgnoreCase)
            : extension.Equals(Extension(request.Container), StringComparison.OrdinalIgnoreCase);
        if (!extensionMatches)
        {
            throw new ArgumentException($"The selected container requires a {Extension(request.Container)} file name.", nameof(request));
        }
        if (!StudioExportChoices.EncodingsFor(request.Container).Any(choice => choice.Encoding == request.Encoding))
        {
            throw new ArgumentException($"{request.Encoding} is not available in {request.Container}.", nameof(request));
        }
        if (request.Encoding == StudioSampleEncoding.Float32)
        {
            if (request.Container != StudioExportContainer.Wave)
            {
                throw new ArgumentException("32-bit float is available only in standard WAV; BWF, RF64/BWF, and AIFF use integer PCM in this milestone.", nameof(request));
            }
            if (request.Dither != DitherMode.None)
            {
                throw new ArgumentException("Dither applies only when reducing audio to integer PCM.", nameof(request));
            }
        }
        else if (StudioExportChoices.IsLossy(request.Container))
        {
            if (request.Dither != DitherMode.None)
            {
                throw new ArgumentException("Lossy encoders accept the measured floating-point master directly; PCM dither does not apply.", nameof(request));
            }
        }
        else if (request.Dither is not (DitherMode.None or DitherMode.Tpdf))
        {
            throw new ArgumentException("The selected integer dither mode is unsupported.", nameof(request));
        }
        if (request.Encoding != StudioSampleEncoding.Float32 && buffer.InterleavedSamples.Any(sample => Math.Abs((double)sample) > 1))
        {
            throw new InvalidOperationException("The mix exceeds 0 dBFS. Choose 32-bit float WAV or reduce the arranged take gain; PulseGen will not silently normalize or clip it.");
        }
    }

    private static string DirectWriterSettings(StudioExportRequest request) =>
        request.Encoding == StudioSampleEncoding.Float32
            ? "32-bit IEEE float; no normalization"
            : $"{BitsPerSample(request.Encoding)}-bit PCM; dither={request.Dither}; no normalization";

    private static long WriteWave(
        Stream stream,
        StudioExportRequest request,
        FloatWaveBuffer buffer,
        StudioAudioMetrics metrics,
        bool broadcast,
        bool rf64,
        CancellationToken cancellationToken)
    {
        var bytesPerSample = BitsPerSample(request.Encoding) / 8;
        var dataSize = checked((long)buffer.InterleavedSamples.Length * bytesPerSample);
        var bext = broadcast ? BuildBroadcastExtension(request, buffer, metrics) : [];
        var bextChunkSize = broadcast ? ChunkTotalSize(bext.LongLength) : 0;
        var fmtChunkSize = 24L;
        var factChunkSize = request.Encoding == StudioSampleEncoding.Float32 ? 12L : 0;
        var ds64ChunkSize = rf64 ? 36L : 0;
        var dataChunkSize = checked(8 + dataSize + (dataSize & 1));
        var fileSize = checked(12 + ds64ChunkSize + bextChunkSize + fmtChunkSize + factChunkSize + dataChunkSize);
        if (!rf64 && fileSize - 8 > uint.MaxValue)
        {
            throw new InvalidOperationException("This WAVE file exceeds the 4 GiB RIFF limit. Choose RF64 / BWF.");
        }

        WriteAscii(stream, rf64 ? "RF64" : "RIFF");
        WriteUInt32LittleEndian(stream, rf64 ? RiffSizeSentinel : checked((uint)(fileSize - 8)));
        WriteAscii(stream, "WAVE");
        if (rf64)
        {
            WriteAscii(stream, "ds64");
            WriteUInt32LittleEndian(stream, 28);
            WriteUInt64LittleEndian(stream, checked((ulong)(fileSize - 8)));
            WriteUInt64LittleEndian(stream, checked((ulong)dataSize));
            WriteUInt64LittleEndian(stream, checked((ulong)buffer.Frames));
            WriteUInt32LittleEndian(stream, 0);
        }
        if (broadcast)
        {
            WriteChunk(stream, "bext", bext, littleEndian: true);
        }

        WriteAscii(stream, "fmt ");
        WriteUInt32LittleEndian(stream, 16);
        WriteUInt16LittleEndian(stream, request.Encoding == StudioSampleEncoding.Float32 ? (ushort)3 : (ushort)1);
        WriteUInt16LittleEndian(stream, 2);
        WriteUInt32LittleEndian(stream, checked((uint)buffer.SampleRate));
        WriteUInt32LittleEndian(stream, checked((uint)(buffer.SampleRate * 2 * bytesPerSample)));
        WriteUInt16LittleEndian(stream, checked((ushort)(2 * bytesPerSample)));
        WriteUInt16LittleEndian(stream, checked((ushort)(bytesPerSample * 8)));
        if (request.Encoding == StudioSampleEncoding.Float32)
        {
            WriteAscii(stream, "fact");
            WriteUInt32LittleEndian(stream, 4);
            WriteUInt32LittleEndian(stream, checked((uint)buffer.Frames));
        }
        WriteAscii(stream, "data");
        WriteUInt32LittleEndian(stream, rf64 ? RiffSizeSentinel : checked((uint)dataSize));
        var saturated = WriteSamples(stream, request, buffer, littleEndian: true, cancellationToken);
        if ((dataSize & 1) != 0)
        {
            stream.WriteByte(0);
        }
        return saturated;
    }

    private static long WriteAiff(
        Stream stream,
        StudioExportRequest request,
        FloatWaveBuffer buffer,
        CancellationToken cancellationToken)
    {
        var bytesPerSample = BitsPerSample(request.Encoding) / 8;
        var dataSize = checked((long)buffer.InterleavedSamples.Length * bytesPerSample);
        var soundPayloadSize = checked(8 + dataSize);
        var soundChunkSize = checked(8 + soundPayloadSize + (soundPayloadSize & 1));
        var fileSize = checked(12 + 26 + soundChunkSize);
        if (fileSize - 8 > uint.MaxValue)
        {
            throw new InvalidOperationException("This AIFF exceeds its 4 GiB container limit. Choose RF64 / BWF.");
        }

        WriteAscii(stream, "FORM");
        WriteUInt32BigEndian(stream, checked((uint)(fileSize - 8)));
        WriteAscii(stream, "AIFF");
        WriteAscii(stream, "COMM");
        WriteUInt32BigEndian(stream, 18);
        WriteUInt16BigEndian(stream, 2);
        WriteUInt32BigEndian(stream, checked((uint)buffer.Frames));
        WriteUInt16BigEndian(stream, checked((ushort)(bytesPerSample * 8)));
        WriteExtendedSampleRate(stream, buffer.SampleRate);
        WriteAscii(stream, "SSND");
        WriteUInt32BigEndian(stream, checked((uint)soundPayloadSize));
        WriteUInt32BigEndian(stream, 0);
        WriteUInt32BigEndian(stream, 0);
        var saturated = WriteSamples(stream, request, buffer, littleEndian: false, cancellationToken);
        if ((soundPayloadSize & 1) != 0)
        {
            stream.WriteByte(0);
        }
        return saturated;
    }

    private static long WriteSamples(
        Stream stream,
        StudioExportRequest request,
        FloatWaveBuffer buffer,
        bool littleEndian,
        CancellationToken cancellationToken)
    {
        var bytesPerSample = BitsPerSample(request.Encoding) / 8;
        var staging = new byte[64 * 1024 - ((64 * 1024) % bytesPerSample)];
        var cursor = 0;
        var random = new DeterministicDither(request.DitherSeed);
        var saturationCount = 0L;
        for (var index = 0; index < buffer.InterleavedSamples.Length; index++)
        {
            if ((index & 0xFFF) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }
            var target = staging.AsSpan(cursor, bytesPerSample);
            if (request.Encoding == StudioSampleEncoding.Float32)
            {
                if (littleEndian)
                {
                    BinaryPrimitives.WriteSingleLittleEndian(target, buffer.InterleavedSamples[index]);
                }
                else
                {
                    BinaryPrimitives.WriteSingleBigEndian(target, buffer.InterleavedSamples[index]);
                }
            }
            else
            {
                var quantized = Quantize(
                    buffer.InterleavedSamples[index],
                    BitsPerSample(request.Encoding),
                    request.Dither == DitherMode.Tpdf ? random.NextTpdf() : 0,
                    out var saturated);
                if (saturated)
                {
                    saturationCount++;
                }
                WriteIntegerSample(target, quantized, littleEndian);
            }
            cursor += bytesPerSample;
            if (cursor == staging.Length)
            {
                stream.Write(staging);
                cursor = 0;
            }
        }
        if (cursor > 0)
        {
            stream.Write(staging, 0, cursor);
        }
        return saturationCount;
    }

    private static long Quantize(double sample, int bits, double dither, out bool saturated)
    {
        var scale = Math.Pow(2, bits - 1);
        var minimum = -scale;
        var maximum = scale - 1;
        var rounded = Math.Round((sample * scale) + dither, MidpointRounding.AwayFromZero);
        saturated = rounded < minimum || rounded > maximum;
        return checked((long)Math.Clamp(rounded, minimum, maximum));
    }

    private static void WriteIntegerSample(Span<byte> destination, long value, bool littleEndian)
    {
        switch (destination.Length)
        {
            case 2:
                if (littleEndian)
                {
                    BinaryPrimitives.WriteInt16LittleEndian(destination, checked((short)value));
                }
                else
                {
                    BinaryPrimitives.WriteInt16BigEndian(destination, checked((short)value));
                }
                break;
            case 3:
                if (littleEndian)
                {
                    destination[0] = (byte)value;
                    destination[1] = (byte)(value >> 8);
                    destination[2] = (byte)(value >> 16);
                }
                else
                {
                    destination[0] = (byte)(value >> 16);
                    destination[1] = (byte)(value >> 8);
                    destination[2] = (byte)value;
                }
                break;
            case 4:
                if (littleEndian)
                {
                    BinaryPrimitives.WriteInt32LittleEndian(destination, checked((int)value));
                }
                else
                {
                    BinaryPrimitives.WriteInt32BigEndian(destination, checked((int)value));
                }
                break;
            default:
                throw new InvalidOperationException("Unsupported PCM word size.");
        }
    }

    private static byte[] BuildBroadcastExtension(StudioExportRequest request, FloatWaveBuffer buffer, StudioAudioMetrics metrics)
    {
        var codingHistory = Encoding.ASCII.GetBytes($"A=PCM,F={buffer.SampleRate},W={BitsPerSample(request.Encoding)},M=stereo,T=PulseGen\r\n");
        var payload = new byte[checked(602 + codingHistory.Length)];
        WriteFixedAscii(payload.AsSpan(0, 256), $"PulseGen studio master: {request.ProjectName}; {request.TakeIds.Count} receiver takes; normalization none");
        WriteFixedAscii(payload.AsSpan(256, 32), "PulseGen");
        WriteFixedAscii(payload.AsSpan(288, 32), request.ProjectId.ToString("N"));
        WriteFixedAscii(payload.AsSpan(320, 10), request.CreatedAtUtc.UtcDateTime.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture));
        WriteFixedAscii(payload.AsSpan(330, 8), request.CreatedAtUtc.UtcDateTime.ToString("HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture));
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(338, 4), (uint)request.TimeReferenceSamples);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(342, 4), (uint)(request.TimeReferenceSamples >> 32));
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(346, 2), 2);
        WriteBextMetric(payload.AsSpan(412, 2), metrics.IntegratedLoudnessLufs);
        WriteBextMetric(payload.AsSpan(414, 2), metrics.LoudnessRangeLu);
        WriteBextMetric(payload.AsSpan(416, 2), metrics.TruePeakDbTp);
        WriteBextMetric(payload.AsSpan(418, 2), metrics.MaximumMomentaryLoudnessLufs);
        WriteBextMetric(payload.AsSpan(420, 2), metrics.MaximumShortTermLoudnessLufs);
        codingHistory.CopyTo(payload, 602);
        return payload;
    }

    private static void WriteBextMetric(Span<byte> destination, double? value)
    {
        var encoded = value is null || !double.IsFinite(value.Value)
            ? short.MaxValue
            : checked((short)Math.Clamp(
                Math.Round(value.Value * 100, MidpointRounding.AwayFromZero),
                short.MinValue,
                short.MaxValue - 1));
        BinaryPrimitives.WriteInt16LittleEndian(destination, encoded);
    }

    private static void WriteFixedAscii(Span<byte> destination, string value)
    {
        destination.Clear();
        var encoded = Encoding.ASCII.GetBytes(value);
        encoded.AsSpan(0, Math.Min(encoded.Length, destination.Length)).CopyTo(destination);
    }

    private static void WriteExtendedSampleRate(Stream stream, int sampleRate)
    {
        var exponent = 31 - int.LeadingZeroCount(sampleRate);
        var biasedExponent = checked((ushort)(exponent + 16383));
        var mantissa = checked((ulong)sampleRate << (63 - exponent));
        WriteUInt16BigEndian(stream, biasedExponent);
        WriteUInt64BigEndian(stream, mantissa);
    }

    private static void WriteChunk(Stream stream, string id, byte[] payload, bool littleEndian)
    {
        WriteAscii(stream, id);
        if (littleEndian)
        {
            WriteUInt32LittleEndian(stream, checked((uint)payload.Length));
        }
        else
        {
            WriteUInt32BigEndian(stream, checked((uint)payload.Length));
        }
        stream.Write(payload);
        if ((payload.Length & 1) != 0)
        {
            stream.WriteByte(0);
        }
    }

    private static long ChunkTotalSize(long payloadSize) => checked(8 + payloadSize + (payloadSize & 1));

    private static int BitsPerSample(StudioSampleEncoding encoding) => encoding switch
    {
        StudioSampleEncoding.Pcm16 => 16,
        StudioSampleEncoding.Pcm24 => 24,
        StudioSampleEncoding.Pcm32 or StudioSampleEncoding.Float32 => 32,
        _ => throw new ArgumentOutOfRangeException(nameof(encoding))
    };

    private static string ComputeSha256(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.SequentialScan);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    private static void WriteReceiptAtomic(string path, StudioExportReceipt receipt)
    {
        var temporary = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(receipt, ReceiptOptions);
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
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

    private static JsonSerializerOptions CreateReceiptOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    private static void WriteAscii(Stream stream, string value)
    {
        if (value.Length != 4 || value.Any(character => character > 0x7F))
        {
            throw new ArgumentException("Container identifiers must contain exactly four ASCII characters.", nameof(value));
        }
        Span<byte> bytes = stackalloc byte[4];
        Encoding.ASCII.GetBytes(value, bytes);
        stream.Write(bytes);
    }

    private static void WriteUInt16LittleEndian(Stream stream, ushort value)
    {
        Span<byte> bytes = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(bytes, value);
        stream.Write(bytes);
    }

    private static void WriteUInt16BigEndian(Stream stream, ushort value)
    {
        Span<byte> bytes = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(bytes, value);
        stream.Write(bytes);
    }

    private static void WriteUInt32LittleEndian(Stream stream, uint value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        stream.Write(bytes);
    }

    private static void WriteUInt32BigEndian(Stream stream, uint value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        stream.Write(bytes);
    }

    private static void WriteUInt64LittleEndian(Stream stream, ulong value)
    {
        Span<byte> bytes = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(bytes, value);
        stream.Write(bytes);
    }

    private static void WriteUInt64BigEndian(Stream stream, ulong value)
    {
        Span<byte> bytes = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(bytes, value);
        stream.Write(bytes);
    }

    private sealed class DeterministicDither(ulong seed)
    {
        private ulong state = seed == 0 ? 0x9E3779B97F4A7C15UL : seed;

        public double NextTpdf() => NextUnit() + NextUnit() - 1;

        private double NextUnit()
        {
            state ^= state >> 12;
            state ^= state << 25;
            state ^= state >> 27;
            var value = state * 0x2545F4914F6CDD1DUL;
            return (value >> 11) * (1.0 / (1UL << 53));
        }
    }
}

internal static class StudioExportProjectImporter
{
    public static async Task<StudioExportImportResult> ImportAsync(
        SceneDocument scene,
        string projectDirectory,
        StudioExportRequest request,
        StudioExportResult exported,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentException.ThrowIfNullOrWhiteSpace(projectDirectory);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(exported);
        var saved = await ProjectStore.LoadAsync(projectDirectory, cancellationToken);
        var revision = ProjectStore.ComputeSceneRevisionSha256(scene);
        if (saved.ProjectId != scene.ProjectId ||
            !string.Equals(ProjectStore.ComputeSceneRevisionSha256(saved), revision, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Save the current take arrangement before importing its studio master.");
        }
        if (request.ProjectId != scene.ProjectId ||
            !string.Equals(request.SceneRevisionSha256, revision, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The studio export does not describe the current saved scene revision.");
        }
        var expectedOutputPath = Path.GetFullPath(request.DestinationPath);
        var output = new FileInfo(exported.OutputPath);
        var receipt = new FileInfo(exported.ReceiptPath);
        if (!string.Equals(output.FullName, expectedOutputPath, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(receipt.FullName, expectedOutputPath + ".pulse-receipt.json", StringComparison.OrdinalIgnoreCase) ||
            !exported.TakeIds.SequenceEqual(request.TakeIds))
        {
            throw new InvalidDataException("The studio export result does not match its original request.");
        }
        if (!output.Exists || !receipt.Exists || output.Length != exported.SizeBytes ||
            !string.Equals(await ProjectStore.ComputeSha256Async(output.FullName, cancellationToken), exported.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The studio master changed before project import and was not admitted.");
        }

        using (var document = JsonDocument.Parse(await File.ReadAllTextAsync(receipt.FullName, cancellationToken)))
        {
            ValidateReceipt(document.RootElement, scene, revision, request, exported, output);
        }

        var receiptAsset = await ProjectStore.ImportAssetAsync(
            projectDirectory,
            receipt.FullName,
            AssetKind.Render,
            "application/vnd.pulsegen.studio-export-receipt+json",
            $"PulseGen studio export receipt; sceneRevisionSha256={revision}; outputSha256={exported.Sha256}",
            cancellationToken: cancellationToken);
        var audioAsset = await ProjectStore.ImportAssetAsync(
            projectDirectory,
            output.FullName,
            AssetKind.Render,
            StudioMasterExporter.MediaType(request.Container),
            $"PulseGen studio master; receiptSha256={receiptAsset.Sha256}; takes={string.Join(',', exported.TakeIds.Select(id => id.ToString("D")))}; normalization=none",
            cancellationToken: cancellationToken);
        audioAsset = audioAsset with
        {
            SampleRate = exported.SampleRate,
            Channels = 2,
            DurationSeconds = (double)exported.Frames / exported.SampleRate
        };
        var assets = scene.Assets.ToList();
        receiptAsset = AddCompatible(assets, receiptAsset);
        audioAsset = AddCompatible(assets, audioAsset);
        var updated = scene with { Assets = assets, UpdatedAtUtc = DateTimeOffset.UtcNow };
        SceneValidator.EnsureValid(updated);
        await ProjectStore.SaveAsync(updated, projectDirectory, cancellationToken);
        return new(updated, audioAsset, receiptAsset);
    }

    private static void ValidateReceipt(
        JsonElement root,
        SceneDocument scene,
        string revision,
        StudioExportRequest request,
        StudioExportResult exported,
        FileInfo output)
    {
        var receiptTakeIds = root.GetProperty("takeIds").EnumerateArray().Select(value => value.GetGuid()).ToArray();
        var standards = root.GetProperty("measurementStandards").EnumerateArray().Select(value => value.GetString()).ToArray();
        var metrics = root.GetProperty("metrics");
        var bitsElement = root.GetProperty("bitsPerSample");
        var bitsValid = exported.BitsPerSample is { } bits
            ? bitsElement.ValueKind == JsonValueKind.Number && bitsElement.GetInt32() == bits
            : bitsElement.ValueKind == JsonValueKind.Null;
        var conversionElement = root.GetProperty("sampleRateConversion");
        var conversionValid = exported.SampleRateConversion is null
            ? conversionElement.ValueKind == JsonValueKind.Null
            : string.Equals(conversionElement.GetString(), exported.SampleRateConversion, StringComparison.Ordinal);
        var valid = root.GetProperty("receiptVersion").GetInt32() == 2 &&
            root.GetProperty("projectId").GetGuid() == scene.ProjectId &&
            string.Equals(root.GetProperty("projectName").GetString(), request.ProjectName, StringComparison.Ordinal) &&
            string.Equals(root.GetProperty("sceneRevisionSha256").GetString(), revision, StringComparison.OrdinalIgnoreCase) &&
            receiptTakeIds.SequenceEqual(request.TakeIds) &&
            string.Equals(root.GetProperty("outputFileName").GetString(), output.Name, StringComparison.Ordinal) &&
            string.Equals(root.GetProperty("outputSha256").GetString(), exported.Sha256, StringComparison.OrdinalIgnoreCase) &&
            root.GetProperty("outputSizeBytes").GetInt64() == output.Length &&
            string.Equals(root.GetProperty("container").GetString(), request.Container.ToString(), StringComparison.Ordinal) &&
            string.Equals(root.GetProperty("encoding").GetString(), request.Encoding.ToString(), StringComparison.Ordinal) &&
            bitsValid &&
            root.GetProperty("sampleRate").GetInt32() == exported.SampleRate &&
            root.GetProperty("channels").GetInt32() == 2 &&
            root.GetProperty("frames").GetInt32() == exported.Frames &&
            SameDouble(root.GetProperty("durationSeconds"), (double)exported.Frames / exported.SampleRate) &&
            root.GetProperty("measurementSourceSampleRate").GetInt32() == exported.MeasurementSourceSampleRate &&
            root.GetProperty("measurementSourceFrames").GetInt32() == exported.MeasurementSourceFrames &&
            root.GetProperty("lossy").GetBoolean() == exported.Lossy &&
            string.Equals(root.GetProperty("encoderName").GetString(), exported.EncoderName, StringComparison.Ordinal) &&
            string.Equals(root.GetProperty("encoderVersion").GetString(), exported.EncoderVersion, StringComparison.Ordinal) &&
            string.Equals(root.GetProperty("encoderSettings").GetString(), exported.EncoderSettings, StringComparison.Ordinal) &&
            conversionValid &&
            string.Equals(root.GetProperty("dither").GetString(), request.Dither.ToString(), StringComparison.Ordinal) &&
            root.GetProperty("ditherSeed").GetUInt64() == request.DitherSeed &&
            !root.GetProperty("normalizationApplied").GetBoolean() &&
            string.Equals(root.GetProperty("measurementStage").GetString(), "post-mix/pre-encoding", StringComparison.Ordinal) &&
            root.GetProperty("quantizerSaturationCount").GetInt64() == exported.QuantizerSaturationCount &&
            root.GetProperty("createdAtUtc").GetDateTimeOffset() == request.CreatedAtUtc &&
            string.Equals(root.GetProperty("engineVersion").GetString(), request.EngineVersion, StringComparison.Ordinal) &&
            standards.SequenceEqual(["ITU-R BS.1770-5", "EBU R 128", "EBU Tech 3341"]) &&
            SameDouble(metrics.GetProperty("samplePeakLinear"), exported.Metrics.SamplePeakLinear) &&
            SameNullableDouble(metrics.GetProperty("samplePeakDbFs"), exported.Metrics.SamplePeakDbFs) &&
            SameDouble(metrics.GetProperty("truePeakLinear"), exported.Metrics.TruePeakLinear) &&
            SameNullableDouble(metrics.GetProperty("truePeakDbTp"), exported.Metrics.TruePeakDbTp) &&
            SameNullableDouble(metrics.GetProperty("integratedLoudnessLufs"), exported.Metrics.IntegratedLoudnessLufs) &&
            SameNullableDouble(metrics.GetProperty("loudnessRangeLu"), exported.Metrics.LoudnessRangeLu) &&
            SameNullableDouble(metrics.GetProperty("maximumMomentaryLoudnessLufs"), exported.Metrics.MaximumMomentaryLoudnessLufs) &&
            SameNullableDouble(metrics.GetProperty("maximumShortTermLoudnessLufs"), exported.Metrics.MaximumShortTermLoudnessLufs);
        if (!valid)
        {
            throw new InvalidDataException("The studio export receipt failed its provenance, output, measurement, or normalization checks.");
        }
    }

    private static bool SameNullableDouble(JsonElement actual, double? expected) =>
        expected is null ? actual.ValueKind == JsonValueKind.Null : SameDouble(actual, expected.Value);

    private static bool SameDouble(JsonElement actual, double expected) =>
        actual.ValueKind == JsonValueKind.Number &&
        BitConverter.DoubleToInt64Bits(actual.GetDouble()) == BitConverter.DoubleToInt64Bits(expected);

    private static AssetRef AddCompatible(List<AssetRef> assets, AssetRef imported)
    {
        var index = assets.FindIndex(asset => string.Equals(asset.Sha256, imported.Sha256, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
        {
            assets.Add(imported);
            return imported;
        }
        var existing = assets[index];
        var reusableAudio = existing.Kind is AssetKind.Audio or AssetKind.Render &&
            imported.Kind is AssetKind.Audio or AssetKind.Render &&
            existing.MediaType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) &&
            imported.MediaType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase);
        var durationTolerance = imported.SampleRate is > 0 ? 0.5 / imported.SampleRate.Value : 0;
        if ((existing.Kind != imported.Kind && !reusableAudio) ||
            !string.Equals(existing.MediaType, imported.MediaType, StringComparison.OrdinalIgnoreCase) ||
            existing.SizeBytes != imported.SizeBytes ||
            (existing.SampleRate is { } rate && imported.SampleRate is { } importedRate && rate != importedRate) ||
            (existing.Channels is { } channels && imported.Channels is { } importedChannels && channels != importedChannels) ||
            (existing.DurationSeconds is { } duration && imported.DurationSeconds is { } importedDuration &&
                (!double.IsFinite(duration) || !double.IsFinite(importedDuration) || Math.Abs(duration - importedDuration) > durationTolerance)))
        {
            throw new InvalidDataException("The studio export collides with an existing asset that has incompatible metadata.");
        }
        var compatible = existing with
        {
            SampleRate = existing.SampleRate ?? imported.SampleRate,
            Channels = existing.Channels ?? imported.Channels,
            DurationSeconds = existing.DurationSeconds ?? imported.DurationSeconds,
            Provenance = existing.Provenance ?? imported.Provenance,
            License = existing.License ?? imported.License
        };
        assets[index] = compatible;
        return compatible;
    }
}
