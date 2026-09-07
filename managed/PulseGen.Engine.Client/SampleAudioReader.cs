using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using PulseGen.Contracts;

namespace PulseGen.Engine.Client;

public sealed record DecodedSampleAudio(int SampleRate, int Channels, float[] Samples)
{
    public int Frames => Samples.Length / Channels;
    public double DurationSeconds => Frames / (double)SampleRate;
    public string Container { get; init; } = "unknown";
    public string DecoderVersion { get; init; } = "unknown";
    public string DecoderBinarySha256 { get; init; } = "unknown";
    public int? BitsPerSample { get; init; }
    public string? Title { get; init; }
    public string? Comment { get; init; }
}

/// <summary>Bounded decoding of immutable source audio. PCM conversion does not normalize peaks.</summary>
public static unsafe class SampleAudioReader
{
    public const int MaximumDecodedSamples = 16_777_216;
    public const long MaximumEncodedBytes = 256L * 1024 * 1024;
    public const string ExpectedBinarySha256 = "4e3bd2de8e1485110eaebef8e1239471f73d608773831c323bf528e05645655e";
    private static readonly Lazy<CodecApi> Codec = new(LoadCodec);

    public static DecodedSampleAudio Read(string path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Keep a read-only handle for the full decode, preventing replacement or writes on Windows.
        using var source = OpenSource(path);
        return ReadLocked(source, cancellationToken);
    }

    public static DecodedSampleAudio ReadVerified(
        string projectDirectory, AssetRef asset, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(asset);
        cancellationToken.ThrowIfCancellationRequested();
        if (asset.Kind is not (AssetKind.Audio or AssetKind.Render) ||
            asset.MediaType?.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) != true || asset.MediaType.Length <= 6)
            throw new InvalidDataException("Sample playback requires an audio asset.");
        using var source = OpenSource(ProjectStore.ResolveAssetPath(projectDirectory, asset.Sha256));
        if (asset.SizeBytes != source.Length)
            throw new InvalidDataException("The sample asset size no longer matches the project manifest.");
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var chunk = new byte[64 * 1024];
        int count;
        while ((count = source.Read(chunk)) != 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            hash.AppendData(chunk, 0, count);
        }
        if (!string.Equals(Convert.ToHexStringLower(hash.GetHashAndReset()), asset.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The sample asset failed SHA-256 verification.");
        source.Position = 0;
        var decoded = ReadLocked(source, cancellationToken);
        if (asset.Channels != decoded.Channels || asset.SampleRate != decoded.SampleRate ||
            asset.DurationSeconds is not double duration || !double.IsFinite(duration) ||
            Math.Abs(duration - decoded.DurationSeconds) > 0.5 / decoded.SampleRate)
            throw new InvalidDataException("The sample asset's decoded dimensions no longer match the project manifest.");
        return decoded;
    }

    private static FileStream OpenSource(string path)
    {
        var source = new FileStream(Path.GetFullPath(path), FileMode.Open, FileAccess.Read, FileShare.Read);
        if (source.Length is <= 0 or > MaximumEncodedBytes)
        {
            source.Dispose();
            throw new InvalidDataException("Sample files must be nonempty and at most 256 MiB encoded.");
        }
        return source;
    }

    private static DecodedSampleAudio ReadLocked(FileStream source, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateContainerLength(source, cancellationToken);
        var api = Codec.Value;
        var info = new FileInfoNative();
        var handle = api.Open(source.Name, 0x10, &info);
        if (handle == 0)
            throw new InvalidDataException($"Cannot decode this sample: {api.LastError(handle)}");
        try
        {
            var container = (info.Format & 0x0fff0000) switch
            {
                0x010000 or 0x130000 or 0x220000 => "WAV",
                0x020000 => "AIFF",
                0x170000 => "FLAC",
                0x200000 => (info.Format & 0xffff) == 0x64 ? "Opus" : "Ogg Vorbis",
                0x230000 => "MP3",
                _ => throw new InvalidDataException("Supported sample files are WAV, AIFF, FLAC, MP3, Ogg Vorbis, and Opus.")
            };
            if (container == "MP3")
            {
                // libsndfile 1.2.2 can report an estimated MP3 length and also cap
                // reads at it. Use a complete bounded decode, not that estimate.
                source.Position = 0;
                var encoded = new byte[checked((int)source.Length)];
                source.ReadExactly(encoded);
                var scan = Mp3FrameScanner.Validate(encoded, cancellationToken);
                var decoded = NativeSceneRenderer.DecodeMp3(encoded, scan.AudioOffset, scan.AudioByteCount, cancellationToken);
                if (decoded.SampleRate != scan.SampleRate || decoded.Channels != scan.Channels ||
                    (scan.ExpectedPcmFrames is { } expectedFrames && decoded.Frames != expectedFrames))
                    throw new InvalidDataException("MP3 decoded audio does not match its complete frame scan and gapless metadata.");
                return decoded with
                {
                    Title = Marshal.PtrToStringUTF8(api.GetString(handle, 1)),
                    Comment = Marshal.PtrToStringUTF8(api.GetString(handle, 5))
                };
            }
            if (info.Channels is < 1 or > 2 || info.SampleRate is < 8000 or > 192000 ||
                info.Frames <= 0 || info.Frames > MaximumDecodedSamples / info.Channels)
                throw new InvalidDataException("Samples require one or two channels at 8–192 kHz, within the 64 MiB decoded limit.");
            var samples = new float[checked((int)info.Frames * info.Channels)];
            var cursor = 0;
            fixed (float* data = samples)
            {
                while (cursor < info.Frames)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var requested = Math.Min(16 * 1024, checked((int)info.Frames - cursor));
                    var read = api.Read(handle, data + cursor * info.Channels, requested);
                    if (read <= 0 || read > requested)
                        throw new InvalidDataException("The sample ended before its declared frame count; truncated sources cannot be imported.");
                    for (var index = cursor * info.Channels; index < (cursor + read) * info.Channels; index++)
                    {
                        if (!float.IsFinite(samples[index]))
                            throw new InvalidDataException("The source contains non-finite audio samples.");
                        if (Math.Abs(samples[index]) > 64f)
                            throw new InvalidDataException("The source exceeds the engine's ±64 sample amplitude limit (about 36 dB above full scale). Reduce its level explicitly before importing; no normalization was applied.");
                    }
                    cursor += checked((int)read);
                }
                float* tail = stackalloc float[2];
                if (api.Read(handle, tail, 1) != 0 || api.Error(handle) != 0)
                    throw new InvalidDataException("The sample's audio data does not match its declared duration.");
            }
            cancellationToken.ThrowIfCancellationRequested();
            return new(info.SampleRate, info.Channels, samples)
            {
                Container = container,
                DecoderVersion = Marshal.PtrToStringUTF8(api.Version()) ?? "unknown",
                DecoderBinarySha256 = ExpectedBinarySha256,
                BitsPerSample = (info.Format & 0xffff) switch { 1 or 5 => 8, 2 => 16, 3 => 24, 4 or 6 => 32, 7 => 64, _ => null },
                Title = Marshal.PtrToStringUTF8(api.GetString(handle, 1)),
                Comment = Marshal.PtrToStringUTF8(api.GetString(handle, 5))
            };
        }
        finally { _ = api.Close(handle); }
    }

    private static void ValidateContainerLength(FileStream source, CancellationToken cancellationToken)
    {
        if (source.Length < 12) throw new InvalidDataException("The sample header is truncated.");
        Span<byte> header = stackalloc byte[12];
        source.ReadExactly(header);
        var riff = header[..4].SequenceEqual("RIFF"u8);
        var bigEndian = header[..4].SequenceEqual("RIFX"u8) || header[..4].SequenceEqual("FORM"u8);
        if (riff || bigEndian)
        {
            var declared = bigEndian ? BinaryPrimitives.ReadUInt32BigEndian(header[4..]) : BinaryPrimitives.ReadUInt32LittleEndian(header[4..]);
            if (declared < 4 || declared + 8L > source.Length)
                throw new InvalidDataException("The audio container is truncated.");
            var end = declared + 8L;
            Span<byte> chunk = stackalloc byte[8];
            while (source.Position + 8 <= end)
            {
                cancellationToken.ThrowIfCancellationRequested();
                source.ReadExactly(chunk);
                var size = bigEndian ? BinaryPrimitives.ReadUInt32BigEndian(chunk[4..]) : BinaryPrimitives.ReadUInt32LittleEndian(chunk[4..]);
                if (size > end - source.Position) throw new InvalidDataException("An audio container chunk is truncated.");
                source.Position += size + (size & 1);
            }
        }
        else if (header[..4].SequenceEqual("OggS"u8))
        {
            source.Position = 0;
            var activeStreams = new HashSet<uint>();
            Span<byte> page = stackalloc byte[27];
            Span<byte> lacing = stackalloc byte[255];
            while (source.Position < source.Length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (source.Length - source.Position < page.Length) throw new InvalidDataException("The Ogg page header is truncated.");
                source.ReadExactly(page);
                if (!page[..4].SequenceEqual("OggS"u8) || page[4] != 0) throw new InvalidDataException("Invalid Ogg page header.");
                var serial = BinaryPrimitives.ReadUInt32LittleEndian(page[14..]);
                activeStreams.Add(serial);
                source.ReadExactly(lacing[..page[26]]);
                var bodySize = 0;
                foreach (var length in lacing[..page[26]]) bodySize += length;
                if (bodySize > source.Length - source.Position) throw new InvalidDataException("The Ogg audio page is truncated.");
                source.Position += bodySize;
                if ((page[5] & 4) != 0) activeStreams.Remove(serial);
            }
            if (activeStreams.Count != 0) throw new InvalidDataException("The Ogg audio stream has no complete ending.");
        }
        source.Position = 0;
    }

    private static CodecApi LoadCodec()
    {
        var overridePath = Environment.GetEnvironmentVariable("PULSEGEN_SNDFILE_PATH");
        var path = !string.IsNullOrWhiteSpace(overridePath) ? Path.GetFullPath(overridePath) : Path.Combine(AppContext.BaseDirectory, "sndfile.dll");
        if (!File.Exists(path)) throw new DllNotFoundException("The pinned sample decoder is missing. Run scripts/bootstrap-codecs.ps1 and rebuild PulseGen.");
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (!string.Equals(Convert.ToHexStringLower(SHA256.HashData(stream)), ExpectedBinarySha256, StringComparison.Ordinal))
            throw new InvalidDataException("Refusing to load a sample decoder that does not match the pinned SHA-256.");
        return new CodecApi(NativeLibrary.Load(path));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileInfoNative { public long Frames; public int SampleRate, Channels, Format, Sections, Seekable; }
    [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
    private delegate nint OpenDelegate([MarshalAs(UnmanagedType.LPWStr)] string path, int mode, FileInfoNative* info);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate long ReadDelegate(nint handle, float* samples, long frames);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int StatusDelegate(nint handle);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate nint ErrorStringDelegate(nint handle);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate nint GetStringDelegate(nint handle, int kind);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate nint VersionDelegate();

    private sealed class CodecApi(nint library)
    {
        public readonly OpenDelegate Open = Bind<OpenDelegate>(library, "sf_wchar_open");
        public readonly ReadDelegate Read = Bind<ReadDelegate>(library, "sf_readf_float");
        public readonly StatusDelegate Close = Bind<StatusDelegate>(library, "sf_close");
        public readonly StatusDelegate Error = Bind<StatusDelegate>(library, "sf_error");
        public readonly ErrorStringDelegate ErrorString = Bind<ErrorStringDelegate>(library, "sf_strerror");
        public readonly GetStringDelegate GetString = Bind<GetStringDelegate>(library, "sf_get_string");
        public readonly VersionDelegate Version = Bind<VersionDelegate>(library, "sf_version_string");
        public string LastError(nint handle) => Marshal.PtrToStringUTF8(ErrorString(handle)) ?? "unknown decoder error";
        private static T Bind<T>(nint library, string symbol) where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(library, symbol));
    }
}
