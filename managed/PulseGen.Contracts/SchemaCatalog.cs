using System.Reflection;

namespace PulseGen.Contracts;

public static class SchemaCatalog
{
    public static string ReadSceneSchema() => Read("scene-v1.schema.json");

    public static string ReadModelPackSchema() => Read("model-pack-v1.schema.json");

    private static string Read(string fileName)
    {
        var assembly = typeof(SchemaCatalog).Assembly;
        var resourceName = assembly.GetManifestResourceNames()
            .SingleOrDefault(name => name.EndsWith(fileName, StringComparison.Ordinal))
            ?? throw new InvalidOperationException($"Embedded contract schema '{fileName}' was not found.");
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded contract schema '{fileName}' could not be opened.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
