using System.Buffers.Binary;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using PulseGen.Contracts;
using PulseGen.Engine.Client;

namespace PulseGen.Editor;

internal sealed record CodecWriteSummary(
    int SampleRate,
    int Frames,
    int? BitsPerSample,
    long QuantizerSaturationCount,
    bool Lossy,
    string EncoderName,
    string EncoderVersion,
    string EncoderSettings,
    string? SampleRateConversion);

internal sealed record CodecReadResult(
    FloatWaveBuffer Buffer,
    StudioExportContainer Container,
    int? BitsPerSample,
    string? Title,
    string? Software,
    string? Comment);

internal static unsafe class SndFileCodecAdapter
{
    internal const string Version = "1.2.2";
    internal const string ExpectedBinarySha256 = SampleAudioReader.ExpectedBinarySha256;

    private const string LibraryName = "sndfile";
    private const int WriteMode = 0x20;
    private const int ReadMode = 0x10;
    private const int FormatFlac = 0x170000;
    private const int FormatOgg = 0x200000;
    private const int FormatMpeg = 0x230000;
    private const int FormatPcm16 = 0x0002;
    private const int FormatPcm24 = 0x0003;
    private const int FormatVorbis = 0x0060;
    private const int FormatOpus = 0x0064;
    private const int FormatMpegLayer3 = 0x0082;
    private const int FormatTypeMask = 0x0FFF0000;
    private const int FormatSubtypeMask = 0x0000FFFF;
    private const int SetVbrEncodingQuality = 0x1300;
    private const int SetCompressionLevel = 0x1301;
    private const int SetOriginalSampleRate = 0x1500;
    private const int StringTitle = 0x01;
    private const int StringSoftware = 0x03;
    private const int StringComment = 0x05;
    private const int StringDate = 0x06;
    private const int ChunkFrames = 16 * 1024;
    private const int MaximumDecodedSamples = 268_435_456;
    private static readonly object ResolverGate = new();
    private static IntPtr loadedLibrary;

    static SndFileCodecAdapter()
    {
        NativeLibrary.SetDllImportResolver(typeof(SndFileCodecAdapter).Assembly, ResolveLibrary);
    }

    public static string RuntimeVersion => SndFileRuntimeLoader.Current.Version;

    public static CodecWriteSummary Write(
        string path,
        StudioExportRequest request,
        FloatWaveBuffer source,
        CancellationToken cancellationToken)
    {
        if (!IsCodecContainer(request.Container))
        {
            throw new ArgumentException("The libsndfile adapter accepts compressed delivery containers only.", nameof(request));
        }
        cancellationToken.ThrowIfCancellationRequested();
        var targetRate = SelectCompatibleSampleRate(request.Container, source.SampleRate);
        var converted = OfflineSampleRateConverter.Convert(source, targetRate, cancellationToken);
        var format = FormatFor(request.Container, request.Encoding);
        var info = new SndFileInfo
        {
            SampleRate = converted.SampleRate,
            Channels = converted.Channels,
            Format = format
        };
        if (Native.sf_format_check(&info) == 0)
        {
            throw new NotSupportedException($"{request.Container} cannot encode stereo audio at {converted.SampleRate:N0} Hz with {request.Encoding}.");
        }

        var handle = Native.sf_wchar_open(Path.GetFullPath(path), WriteMode, &info);
        if (handle == IntPtr.Zero)
        {
            throw new IOException($"libsndfile could not create the compressed master: {LastError(IntPtr.Zero)}");
        }

        var closed = false;
        var saturationCount = 0L;
        try
        {
            ConfigureEncoder(handle, request, source.SampleRate);
            SetRequiredString(handle, StringTitle, request.ProjectName);
            SetRequiredString(handle, StringSoftware, $"PulseGen {request.EngineVersion}; {RuntimeVersion}");
            SetRequiredString(handle, StringComment, $"sceneRevisionSha256={request.SceneRevisionSha256}; takes={string.Join(',', request.TakeIds.Select(id => id.ToString("D")))}; normalization=none");
            SetRequiredString(handle, StringDate, request.CreatedAtUtc.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture));

            saturationCount = request.Container == StudioExportContainer.Flac
                ? WriteLosslessPcm(handle, converted, request, cancellationToken)
                : WriteFloat(handle, converted, cancellationToken);
            Native.sf_write_sync(handle);
            var closeResult = Native.sf_close(handle);
            closed = true;
            if (closeResult != 0)
            {
                throw new IOException($"libsndfile could not finalize the compressed master: {LastError(IntPtr.Zero)}");
            }
        }
        finally
        {
            if (!closed)
            {
                _ = Native.sf_close(handle);
            }
        }

        if (request.Container is StudioExportContainer.OggVorbis or StudioExportContainer.Opus)
        {
            OggStreamCanonicalizer.Canonicalize(path, DeterministicStreamSerial(request.DitherSeed));
        }

        var settings = $"{SettingsFor(request.Container, request.Encoding)}; runtimeSha256={SndFileRuntimeLoader.Current.BinarySha256}; userReplacement={SndFileRuntimeLoader.Current.IsUserReplacement}";
        return new(
            converted.SampleRate,
            converted.Frames,
            request.Container == StudioExportContainer.Flac ? BitsPerSample(request.Encoding) : null,
            saturationCount,
            request.Container != StudioExportContainer.Flac,
            "libsndfile",
            RuntimeVersion,
            settings,
            converted.SampleRate == source.SampleRate
                ? null
                : $"windowed-sinc-32-v1 {source.SampleRate} Hz -> {converted.SampleRate} Hz");
    }

    public static CodecReadResult Read(string path, CancellationToken cancellationToken = default)
    {
        var info = new SndFileInfo();
        var handle = Native.sf_wchar_open(Path.GetFullPath(path), ReadMode, &info);
        if (handle == IntPtr.Zero)
        {
            throw new InvalidDataException($"libsndfile could not open the audio file: {LastError(IntPtr.Zero)}");
        }
        try
        {
            if (info.Channels is < 1 or > 2 || info.SampleRate is < 8000 or > 192000 ||
                info.Frames <= 0 || info.Frames > MaximumDecodedSamples / info.Channels)
            {
                throw new InvalidDataException("The compressed audio dimensions exceed PulseGen's bounded reader limits.");
            }
            var samples = new float[checked((int)info.Frames * info.Channels)];
            var cursorFrames = 0;
            fixed (float* samplesPointer = samples)
            {
                while (cursorFrames < info.Frames)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var requested = Math.Min(ChunkFrames, checked((int)info.Frames - cursorFrames));
                    var read = Native.sf_readf_float(handle, samplesPointer + (cursorFrames * info.Channels), requested);
                    if (read <= 0)
                    {
                        break;
                    }
                    cursorFrames = checked(cursorFrames + (int)read);
                }
            }
            if (cursorFrames != info.Frames)
            {
                Array.Resize(ref samples, checked(cursorFrames * info.Channels));
            }
            if (samples.Length == 0 || samples.Any(sample => !float.IsFinite(sample)))
            {
                throw new InvalidDataException("The codec returned empty or non-finite audio.");
            }

            var container = ContainerFor(info.Format);
            var subtype = info.Format & FormatSubtypeMask;
            var bits = subtype switch
            {
                FormatPcm16 => 16,
                FormatPcm24 => 24,
                _ => (int?)null
            };
            return new(
                new FloatWaveBuffer(info.SampleRate, info.Channels, samples),
                container,
                bits,
                GetString(handle, StringTitle),
                GetString(handle, StringSoftware),
                GetString(handle, StringComment));
        }
        finally
        {
            _ = Native.sf_close(handle);
        }
    }

    internal static bool VerifyRuntimeFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.SequentialScan);
        return string.Equals(Convert.ToHexStringLower(SHA256.HashData(stream)), ExpectedBinarySha256, StringComparison.Ordinal);
    }

    private static bool IsCodecContainer(StudioExportContainer container) =>
        container is StudioExportContainer.Flac or StudioExportContainer.Mp3 or StudioExportContainer.OggVorbis or StudioExportContainer.Opus;

    private static int FormatFor(StudioExportContainer container, StudioSampleEncoding encoding) => container switch
    {
        StudioExportContainer.Flac => FormatFlac | (encoding == StudioSampleEncoding.Pcm16 ? FormatPcm16 : FormatPcm24),
        StudioExportContainer.Mp3 => FormatMpeg | FormatMpegLayer3,
        StudioExportContainer.OggVorbis => FormatOgg | FormatVorbis,
        StudioExportContainer.Opus => FormatOgg | FormatOpus,
        _ => throw new ArgumentOutOfRangeException(nameof(container))
    };

    private static StudioExportContainer ContainerFor(int format) => ((format & FormatTypeMask), (format & FormatSubtypeMask)) switch
    {
        (FormatFlac, _) => StudioExportContainer.Flac,
        (FormatMpeg, FormatMpegLayer3) => StudioExportContainer.Mp3,
        (FormatOgg, FormatVorbis) => StudioExportContainer.OggVorbis,
        (FormatOgg, FormatOpus) => StudioExportContainer.Opus,
        _ => throw new InvalidDataException($"Unsupported compressed audio format 0x{format:X8}.")
    };

    private static int SelectCompatibleSampleRate(StudioExportContainer container, int sourceRate)
    {
        if (container == StudioExportContainer.Mp3)
        {
            ReadOnlySpan<int> supported = [8000, 11025, 12000, 16000, 22050, 24000, 32000, 44100, 48000];
            return supported.Contains(sourceRate) ? sourceRate : sourceRate == 88200 ? 44100 : 48000;
        }
        if (container == StudioExportContainer.Opus)
        {
            ReadOnlySpan<int> supported = [8000, 12000, 16000, 24000, 48000];
            if (supported.Contains(sourceRate))
            {
                return sourceRate;
            }
            return sourceRate < 10000 ? 8000 : sourceRate < 14000 ? 12000 : sourceRate < 20000 ? 16000 : sourceRate < 36000 ? 24000 : 48000;
        }
        return sourceRate;
    }

    private static void ConfigureEncoder(IntPtr handle, StudioExportRequest request, int sourceSampleRate)
    {
        switch (request.Container)
        {
            case StudioExportContainer.Flac:
                {
                    var compression = 0.75;
                    RequireCommand(handle, SetCompressionLevel, &compression, sizeof(double), "FLAC compression level");
                    break;
                }
            case StudioExportContainer.Mp3:
                {
                    var compression = request.Encoding == StudioSampleEncoding.LossyHigh ? 0.20 : 0.45;
                    RequireCommand(handle, SetCompressionLevel, &compression, sizeof(double), "MP3 quality");
                    break;
                }
            case StudioExportContainer.OggVorbis:
                {
                    var quality = request.Encoding == StudioSampleEncoding.LossyHigh ? 0.75 : 0.45;
                    RequireCommand(handle, SetVbrEncodingQuality, &quality, sizeof(double), "Vorbis VBR quality");
                    break;
                }
            case StudioExportContainer.Opus:
                {
                    var compression = request.Encoding == StudioSampleEncoding.LossyHigh ? 0.45 : 0.68;
                    RequireCommand(handle, SetCompressionLevel, &compression, sizeof(double), "Opus target bitrate");
                    var originalRate = sourceSampleRate;
                    RequireCommand(handle, SetOriginalSampleRate, &originalRate, sizeof(int), "Opus original sample-rate metadata");
                    break;
                }
            default:
                throw new ArgumentOutOfRangeException(nameof(request));
        }
    }

    private static string SettingsFor(StudioExportContainer container, StudioSampleEncoding encoding) => container switch
    {
        StudioExportContainer.Flac => $"{BitsPerSample(encoding)}-bit PCM; compression level 0.75; lossless",
        StudioExportContainer.Mp3 => encoding == StudioSampleEncoding.LossyHigh ? "VBR quality 2" : "VBR quality 4.5",
        StudioExportContainer.OggVorbis => encoding == StudioSampleEncoding.LossyHigh ? "VBR quality 0.75" : "VBR quality 0.45",
        StudioExportContainer.Opus => encoding == StudioSampleEncoding.LossyHigh ? "target about 287 kbps stereo; complexity 10" : "target about 172 kbps stereo; complexity 10",
        _ => throw new ArgumentOutOfRangeException(nameof(container))
    };

    private static long WriteLosslessPcm(IntPtr handle, FloatWaveBuffer buffer, StudioExportRequest request, CancellationToken cancellationToken)
    {
        var bits = BitsPerSample(request.Encoding);
        var random = new DeterministicDither(request.DitherSeed);
        var saturationCount = 0L;
        for (var frameOffset = 0; frameOffset < buffer.Frames; frameOffset += ChunkFrames)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var frames = Math.Min(ChunkFrames, buffer.Frames - frameOffset);
            var sampleCount = checked(frames * buffer.Channels);
            var sourceOffset = checked(frameOffset * buffer.Channels);
            if (bits == 16)
            {
                var staging = new short[sampleCount];
                for (var index = 0; index < staging.Length; index++)
                {
                    var value = Quantize(buffer.InterleavedSamples[sourceOffset + index], bits, request.Dither == DitherMode.Tpdf ? random.NextTpdf() : 0, out var saturated);
                    staging[index] = checked((short)value);
                    saturationCount += saturated ? 1 : 0;
                }
                fixed (short* pointer = staging)
                {
                    RequireFrames(Native.sf_writef_short(handle, pointer, frames), frames, handle);
                }
            }
            else
            {
                var staging = new int[sampleCount];
                for (var index = 0; index < staging.Length; index++)
                {
                    var value = Quantize(buffer.InterleavedSamples[sourceOffset + index], bits, request.Dither == DitherMode.Tpdf ? random.NextTpdf() : 0, out var saturated);
                    staging[index] = checked((int)value << 8);
                    saturationCount += saturated ? 1 : 0;
                }
                fixed (int* pointer = staging)
                {
                    RequireFrames(Native.sf_writef_int(handle, pointer, frames), frames, handle);
                }
            }
        }
        return saturationCount;
    }

    private static long WriteFloat(IntPtr handle, FloatWaveBuffer buffer, CancellationToken cancellationToken)
    {
        fixed (float* samples = buffer.InterleavedSamples)
        {
            for (var frameOffset = 0; frameOffset < buffer.Frames; frameOffset += ChunkFrames)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var frames = Math.Min(ChunkFrames, buffer.Frames - frameOffset);
                RequireFrames(Native.sf_writef_float(handle, samples + (frameOffset * buffer.Channels), frames), frames, handle);
            }
        }
        return 0;
    }

    private static void RequireFrames(long actual, int expected, IntPtr handle)
    {
        if (actual != expected)
        {
            throw new IOException($"The codec wrote {actual:N0} of {expected:N0} requested frames: {LastError(handle)}");
        }
    }

    private static void RequireCommand(IntPtr handle, int command, void* data, int size, string name)
    {
        if (Native.sf_command(handle, command, data, size) == 0)
        {
            throw new NotSupportedException($"The selected codec runtime rejected the requested {name}.");
        }
    }

    private static void SetRequiredString(IntPtr handle, int kind, string value)
    {
        if (Native.sf_set_string(handle, kind, value) != 0)
        {
            throw new IOException($"The selected codec could not store required PulseGen provenance metadata: {LastError(handle)}");
        }
    }

    private static string? GetString(IntPtr handle, int kind)
    {
        var value = Native.sf_get_string(handle, kind);
        return value == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(value);
    }

    private static string LastError(IntPtr handle)
    {
        var value = Native.sf_strerror(handle);
        return value == IntPtr.Zero ? "unknown codec error" : Marshal.PtrToStringUTF8(value) ?? "unknown codec error";
    }

    private static int BitsPerSample(StudioSampleEncoding encoding) => encoding switch
    {
        StudioSampleEncoding.Pcm16 => 16,
        StudioSampleEncoding.Pcm24 => 24,
        _ => throw new ArgumentException("FLAC export supports 16-bit or 24-bit PCM.", nameof(encoding))
    };

    private static long Quantize(double sample, int bits, double dither, out bool saturated)
    {
        var scale = Math.Pow(2, bits - 1);
        var rounded = Math.Round((sample * scale) + dither, MidpointRounding.AwayFromZero);
        var minimum = -scale;
        var maximum = scale - 1;
        saturated = rounded < minimum || rounded > maximum;
        return checked((long)Math.Clamp(rounded, minimum, maximum));
    }

    private static uint DeterministicStreamSerial(ulong seed)
    {
        var value = unchecked((uint)(seed ^ (seed >> 32) ^ 0x50554C53UL));
        return value == 0 ? 0x50474F47u : value;
    }

    private static IntPtr ResolveLibrary(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (!string.Equals(libraryName, LibraryName, StringComparison.Ordinal))
        {
            return IntPtr.Zero;
        }
        lock (ResolverGate)
        {
            if (loadedLibrary != IntPtr.Zero)
            {
                return loadedLibrary;
            }
            loadedLibrary = SndFileRuntimeLoader.Current.Handle;
            return loadedLibrary;
        }
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

    [StructLayout(LayoutKind.Sequential)]
    private struct SndFileInfo
    {
        public long Frames;
        public int SampleRate;
        public int Channels;
        public int Format;
        public int Sections;
        public int Seekable;
    }

    private static class Native
    {
        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        internal static extern IntPtr sf_version_string();

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true, CharSet = CharSet.Unicode)]
        internal static extern IntPtr sf_wchar_open([MarshalAs(UnmanagedType.LPWStr)] string path, int mode, SndFileInfo* info);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        internal static extern int sf_format_check(SndFileInfo* info);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        internal static extern long sf_writef_short(IntPtr handle, short* samples, long frames);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        internal static extern long sf_writef_int(IntPtr handle, int* samples, long frames);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        internal static extern long sf_writef_float(IntPtr handle, float* samples, long frames);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        internal static extern long sf_readf_float(IntPtr handle, float* samples, long frames);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        internal static extern int sf_command(IntPtr handle, int command, void* data, int size);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        internal static extern int sf_set_string(IntPtr handle, int kind, [MarshalAs(UnmanagedType.LPUTF8Str)] string value);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        internal static extern IntPtr sf_get_string(IntPtr handle, int kind);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        internal static extern IntPtr sf_strerror(IntPtr handle);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        internal static extern void sf_write_sync(IntPtr handle);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        internal static extern int sf_close(IntPtr handle);
    }
}

internal static class OfflineSampleRateConverter
{
    private const int Radius = 16;
    private const int PhaseCount = 4096;

    public static FloatWaveBuffer Convert(FloatWaveBuffer source, int targetSampleRate, CancellationToken cancellationToken)
    {
        if (source.SampleRate == targetSampleRate)
        {
            return source;
        }
        if (targetSampleRate is < 8000 or > 192000)
        {
            throw new ArgumentOutOfRangeException(nameof(targetSampleRate));
        }

        var targetFrames = checked((int)Math.Round(
            source.Frames * (double)targetSampleRate / source.SampleRate,
            MidpointRounding.AwayFromZero));
        var output = new float[checked(targetFrames * source.Channels)];
        var cutoff = Math.Min(1d, targetSampleRate / (double)source.SampleRate) * 0.96;
        var phases = BuildPhases(cutoff);
        for (var targetFrame = 0; targetFrame < targetFrames; targetFrame++)
        {
            if ((targetFrame & 0xFFF) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }
            var sourcePosition = targetFrame * (double)source.SampleRate / targetSampleRate;
            var centre = (int)Math.Floor(sourcePosition);
            var fraction = sourcePosition - centre;
            var phaseIndex = (int)Math.Round(fraction * PhaseCount, MidpointRounding.AwayFromZero);
            if (phaseIndex == PhaseCount)
            {
                phaseIndex = 0;
                centre++;
            }
            var weights = phases[phaseIndex];
            var first = centre - Radius + 1;
            var normalization = 0d;
            for (var tap = 0; tap < weights.Length; tap++)
            {
                var frame = first + tap;
                if (frame >= 0 && frame < source.Frames)
                {
                    normalization += weights[tap];
                }
            }
            for (var channel = 0; channel < source.Channels; channel++)
            {
                var sum = 0d;
                for (var tap = 0; tap < weights.Length; tap++)
                {
                    var frame = first + tap;
                    if (frame >= 0 && frame < source.Frames)
                    {
                        sum += source.InterleavedSamples[(frame * source.Channels) + channel] * weights[tap];
                    }
                }
                output[(targetFrame * source.Channels) + channel] = (float)(sum / normalization);
            }
        }
        return new FloatWaveBuffer(targetSampleRate, source.Channels, output);
    }

    private static double[][] BuildPhases(double cutoff)
    {
        var result = new double[PhaseCount][];
        for (var phase = 0; phase < PhaseCount; phase++)
        {
            var fraction = phase / (double)PhaseCount;
            var weights = new double[Radius * 2];
            for (var tap = 0; tap < weights.Length; tap++)
            {
                var distance = (tap - Radius + 1) - fraction;
                var window = Math.Abs(distance) >= Radius ? 0 : 0.5 * (1 + Math.Cos(Math.PI * distance / Radius));
                var argument = Math.PI * cutoff * distance;
                var sinc = Math.Abs(argument) < 1e-12 ? 1 : Math.Sin(argument) / argument;
                weights[tap] = cutoff * sinc * window;
            }
            result[phase] = weights;
        }
        return result;
    }
}

internal static class OggStreamCanonicalizer
{
    private const uint Polynomial = 0x04C11DB7;
    private static readonly uint[] CrcTable = BuildCrcTable();

    public static void Canonicalize(string path, uint serialNumber)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None, 128 * 1024, FileOptions.WriteThrough);
        var cursor = 0L;
        var pages = 0;
        Span<byte> fixedHeader = stackalloc byte[27];
        while (cursor < stream.Length)
        {
            stream.Position = cursor;
            stream.ReadExactly(fixedHeader);
            if (!fixedHeader[..4].SequenceEqual("OggS"u8) || fixedHeader[4] != 0)
            {
                throw new InvalidDataException("The codec produced an invalid Ogg page while canonicalizing its stream identity.");
            }
            var segmentCount = fixedHeader[26];
            var lacing = new byte[segmentCount];
            stream.ReadExactly(lacing);
            var bodyLength = lacing.Sum(value => (int)value);
            var pageLength = checked(27 + segmentCount + bodyLength);
            if (cursor > stream.Length - pageLength)
            {
                throw new InvalidDataException("The codec produced a truncated Ogg page.");
            }
            var page = new byte[pageLength];
            fixedHeader.CopyTo(page);
            lacing.CopyTo(page, 27);
            stream.ReadExactly(page.AsSpan(27 + segmentCount, bodyLength));
            BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(14, 4), serialNumber);
            page.AsSpan(22, 4).Clear();
            BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(22, 4), ComputeCrc(page));
            stream.Position = cursor;
            stream.Write(page);
            cursor = checked(cursor + pageLength);
            pages++;
        }
        if (pages == 0 || cursor != stream.Length)
        {
            throw new InvalidDataException("The codec produced an empty or malformed Ogg stream.");
        }
        stream.Flush(flushToDisk: true);
    }

    private static uint ComputeCrc(ReadOnlySpan<byte> data)
    {
        var crc = 0u;
        foreach (var value in data)
        {
            crc = (crc << 8) ^ CrcTable[((crc >> 24) & 0xFF) ^ value];
        }
        return crc;
    }

    private static uint[] BuildCrcTable()
    {
        var result = new uint[256];
        for (var index = 0; index < result.Length; index++)
        {
            var value = (uint)index << 24;
            for (var bit = 0; bit < 8; bit++)
            {
                value = (value & 0x80000000) != 0 ? (value << 1) ^ Polynomial : value << 1;
            }
            result[index] = value;
        }
        return result;
    }
}
