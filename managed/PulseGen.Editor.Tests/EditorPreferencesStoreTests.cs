using Xunit;

namespace PulseGen.Editor.Tests;

public sealed class EditorPreferencesStoreTests
{
    [Fact]
    public void MissingPreferencesShowTheStartGuide()
    {
        var root = TemporaryDirectory();
        try
        {
            var preferences = new EditorPreferencesStore(Path.Combine(root, "editor-preferences.json")).Load();

            Assert.True(preferences.ShowStartGuideOnLaunch);
            Assert.Equal(1, preferences.SchemaVersion);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void PreferenceRoundTripIsAtomic()
    {
        var root = TemporaryDirectory();
        try
        {
            var path = Path.Combine(root, "nested", "editor-preferences.json");
            var store = new EditorPreferencesStore(path);
            store.Save(new EditorPreferences { ShowStartGuideOnLaunch = false });

            Assert.False(store.Load().ShowStartGuideOnLaunch);
            Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!, "*.tmp"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void InvalidPreferencesAreQuarantinedAndDefaultsRemainUsable()
    {
        var root = TemporaryDirectory();
        try
        {
            var path = Path.Combine(root, "editor-preferences.json");
            File.WriteAllText(path, "{");
            var preferences = new EditorPreferencesStore(path).Load();

            Assert.True(preferences.ShowStartGuideOnLaunch);
            Assert.False(File.Exists(path));
            Assert.Single(Directory.GetFiles(root, "editor-preferences.invalid-*.json"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string TemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"pulsegen-preferences-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }
}

