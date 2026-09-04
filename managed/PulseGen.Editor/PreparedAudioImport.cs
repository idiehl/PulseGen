using PulseGen.Contracts;
using PulseGen.Engine.Client;

namespace PulseGen.Editor;

/// <summary>A private temporary copy; the project receives no asset until the user accepts its mapping.</summary>
internal sealed class PreparedAudioImport : IDisposable
{
    private readonly string directory;
    public string FilePath { get; }
    public AssetRef Asset { get; }

    private PreparedAudioImport(string directory, string filePath, AssetRef asset)
    {
        this.directory = directory;
        FilePath = filePath;
        Asset = asset;
    }

    public static async Task<PreparedAudioImport> CreateAsync(string sourcePath, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var mediaType = Path.GetExtension(sourcePath).ToLowerInvariant() switch
        {
            ".wav" or ".wave" => "audio/wav", ".aif" or ".aiff" => "audio/aiff",
            ".flac" => "audio/flac", ".mp3" => "audio/mpeg", ".ogg" => "audio/ogg", ".opus" => "audio/opus",
            _ => throw new NotSupportedException("Choose WAV, AIFF, FLAC, MP3, Ogg Vorbis, or Opus.")
        };
        var directory = Directory.CreateTempSubdirectory("PulseGen-sample-").FullName;
        var staged = Path.Combine(directory, "source.audio");
        try
        {
            await using (var input = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous))
            {
                if (input.Length is <= 0 or > SampleAudioReader.MaximumEncodedBytes)
                    throw new InvalidDataException("Choose an audio file between 1 byte and 256 MiB. Active decoded recordings share a 64 MiB budget.");
                await using var output = new FileStream(staged, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous);
                await input.CopyToAsync(output, token);
            }
            var decoded = await Task.Run(() => SampleAudioReader.Read(staged, token), token);
            var asset = new AssetRef
            {
                Sha256 = await ProjectStore.ComputeSha256Async(staged, token), Kind = AssetKind.Audio,
                FileName = Path.GetFileName(sourcePath), MediaType = mediaType, SizeBytes = new FileInfo(staged).Length,
                Channels = decoded.Channels, SampleRate = decoded.SampleRate, DurationSeconds = decoded.DurationSeconds,
                Provenance = $"Local original recording; {decoded.DecoderVersion}; imported without normalization or channel conversion"
            };
            return new(directory, staged, asset);
        }
        catch
        {
            Cleanup(directory, staged);
            throw;
        }
    }

    public void Dispose() => Cleanup(directory, FilePath);

    private static void Cleanup(string directory, string filePath)
    {
        try
        {
            File.Delete(filePath);
            if (Directory.Exists(directory)) Directory.Delete(directory);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Trace.TraceWarning($"Unable to remove temporary audio import '{directory}': {exception.Message}");
        }
    }
}
