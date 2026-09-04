using System.Text.Json;
using System.Text.Json.Serialization;

namespace PulseGen.Editor;

internal sealed record EditorPreferences
{
    public int SchemaVersion { get; init; } = 1;
    public bool ShowStartGuideOnLaunch { get; init; } = true;
}

internal sealed class EditorPreferencesStore
{
    private const long MaximumPreferencesBytes = 64 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    private readonly string path;

    public EditorPreferencesStore(string path)
    {
        this.path = Path.GetFullPath(path);
    }

    public static EditorPreferencesStore CreateDefault()
    {
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Atlas Universalis",
            "PulseGen");
        return new(Path.Combine(root, "editor-preferences.json"));
    }

    public EditorPreferences Load()
    {
        if (!File.Exists(path))
        {
            return new();
        }

        try
        {
            var info = new FileInfo(path);
            if (info.Length is <= 0 or > MaximumPreferencesBytes)
            {
                throw new InvalidDataException("Editor preferences exceed their bounded size.");
            }
            var preferences = JsonSerializer.Deserialize<EditorPreferences>(File.ReadAllText(path), JsonOptions)
                ?? throw new InvalidDataException("Editor preferences were empty.");
            if (preferences.SchemaVersion != 1)
            {
                throw new InvalidDataException($"Unsupported editor-preferences schema {preferences.SchemaVersion}.");
            }
            return preferences;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            QuarantineInvalidFile();
            return new();
        }
    }

    public void Save(EditorPreferences preferences)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        if (preferences.SchemaVersion != 1)
        {
            throw new ArgumentException("Only editor-preferences schema 1 can be saved.", nameof(preferences));
        }

        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(preferences, JsonOptions));
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

    private void QuarantineInvalidFile()
    {
        try
        {
            var directory = Path.GetDirectoryName(path)!;
            var quarantine = Path.Combine(
                directory,
                $"{Path.GetFileNameWithoutExtension(path)}.invalid-{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}.json");
            File.Move(path, quarantine);
        }
        catch (IOException)
        {
            // A locked or concurrently replaced file remains untouched; defaults are still safe.
        }
        catch (UnauthorizedAccessException)
        {
            // Read-only settings must not prevent the editor from opening.
        }
    }
}

