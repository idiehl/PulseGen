using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.IO.Compression;

namespace PulseGen.Contracts;

public static class ProjectStore
{
    private const int MaximumArchiveEntries = 100_000;
    private const long MaximumExpandedArchiveBytes = 32L * 1024 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        AllowTrailingCommas = false,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter() }
    };

    public static async Task<SceneDocument> LoadAsync(string projectDirectory, CancellationToken cancellationToken = default)
    {
        var manifestPath = ResolveManifestPath(projectDirectory);
        await using var stream = new FileStream(manifestPath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var scene = await JsonSerializer.DeserializeAsync<SceneDocument>(stream, JsonOptions, cancellationToken)
            ?? throw new InvalidDataException("Project manifest did not contain a scene document.");
        SceneValidator.EnsureValid(scene);
        return scene;
    }

    public static async Task<string> SaveAsync(SceneDocument scene, string projectDirectory, CancellationToken cancellationToken = default)
    {
        SceneValidator.EnsureValid(scene);
        var fullDirectory = Path.GetFullPath(projectDirectory);
        if (!fullDirectory.EndsWith(".pulseproj", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Working project directories must end in .pulseproj.", nameof(projectDirectory));
        }

        Directory.CreateDirectory(fullDirectory);
        Directory.CreateDirectory(Path.Combine(fullDirectory, "assets", "sha256"));
        Directory.CreateDirectory(Path.Combine(fullDirectory, "analysis"));
        Directory.CreateDirectory(Path.Combine(fullDirectory, "renders"));

        var manifestPath = Path.Combine(fullDirectory, "project.json");
        var temporaryPath = Path.Combine(fullDirectory, $"project.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, scene, JsonOptions, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }

            File.Move(temporaryPath, manifestPath, overwrite: true);
            return await ComputeSha256Async(manifestPath, cancellationToken);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    public static async Task<string> ComputeSha256Async(string filePath, CancellationToken cancellationToken = default)
    {
        await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexStringLower(hash);
    }

    public static string ComputeSceneRevisionSha256(SceneDocument scene)
    {
        SceneValidator.EnsureValid(scene);
        var manifest = JsonSerializer.SerializeToUtf8Bytes(scene, JsonOptions);
        return Convert.ToHexStringLower(SHA256.HashData(manifest));
    }

    public static async Task<AssetRef> ImportAssetAsync(
        string projectDirectory,
        string sourcePath,
        AssetKind kind,
        string mediaType,
        string? provenance = null,
        string? license = null,
        CancellationToken cancellationToken = default)
    {
        _ = ResolveManifestPath(projectDirectory);
        var source = Path.GetFullPath(sourcePath);
        var hash = await ComputeSha256Async(source, cancellationToken);
        var assetRoot = Path.Combine(Path.GetFullPath(projectDirectory), "assets", "sha256", hash[..2]);
        Directory.CreateDirectory(assetRoot);
        var destination = Path.Combine(assetRoot, hash);
        if (File.Exists(destination))
        {
            var existingHash = await ComputeSha256Async(destination, cancellationToken);
            if (!string.Equals(hash, existingHash, StringComparison.Ordinal))
            {
                throw new InvalidDataException("Existing content-addressed asset failed SHA-256 verification and was not overwritten.");
            }
        }
        else
        {
            var temporary = destination + $".{Guid.NewGuid():N}.tmp";
            try
            {
                await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
                await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough))
                {
                    await input.CopyToAsync(output, cancellationToken);
                    await output.FlushAsync(cancellationToken);
                }

                var copiedHash = await ComputeSha256Async(temporary, cancellationToken);
                if (!string.Equals(hash, copiedHash, StringComparison.Ordinal))
                {
                    throw new InvalidDataException("Asset hash changed while copying into the content store.");
                }
                File.Move(temporary, destination);
            }
            finally
            {
                if (File.Exists(temporary))
                {
                    File.Delete(temporary);
                }
            }
        }

        var file = new FileInfo(source);
        return new AssetRef
        {
            Sha256 = hash,
            Kind = kind,
            FileName = file.Name,
            MediaType = mediaType,
            SizeBytes = file.Length,
            Provenance = provenance,
            License = license
        };
    }

    public static string ResolveAssetPath(string projectDirectory, string sha256)
    {
        if (sha256.Length != 64 || !sha256.All(Uri.IsHexDigit))
        {
            throw new ArgumentException("Asset identity must be a 64-character SHA-256 value.", nameof(sha256));
        }
        _ = ResolveManifestPath(projectDirectory);
        return Path.Combine(Path.GetFullPath(projectDirectory), "assets", "sha256", sha256[..2].ToLowerInvariant(), sha256.ToLowerInvariant());
    }

    public static async Task<string> PackAsync(
        string projectDirectory,
        string archivePath,
        CancellationToken cancellationToken = default)
    {
        var manifestPath = ResolveManifestPath(projectDirectory);
        _ = await LoadAsync(projectDirectory, cancellationToken);
        var projectRoot = Path.GetDirectoryName(manifestPath)!;
        var fullArchivePath = Path.GetFullPath(archivePath);
        if (!fullArchivePath.EndsWith(".pulsepack", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Portable project archives must end in .pulsepack.", nameof(archivePath));
        }

        Directory.CreateDirectory(Path.GetDirectoryName(fullArchivePath)!);
        var temporaryPath = fullArchivePath + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var output = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough))
            using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
            {
                foreach (var file in Directory.EnumerateFiles(projectRoot, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (Path.GetExtension(file).Equals(".tmp", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var relativePath = Path.GetRelativePath(projectRoot, file).Replace(Path.DirectorySeparatorChar, '/');
                    var entry = archive.CreateEntry(relativePath, CompressionLevel.Optimal);
                    entry.LastWriteTime = new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
                    await using var entryStream = entry.Open();
                    await using var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                    await input.CopyToAsync(entryStream, cancellationToken);
                }
            }

            File.Move(temporaryPath, fullArchivePath, overwrite: true);
            return await ComputeSha256Async(fullArchivePath, cancellationToken);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    public static async Task<SceneDocument> UnpackAsync(
        string archivePath,
        string projectDirectory,
        CancellationToken cancellationToken = default)
    {
        var fullArchivePath = Path.GetFullPath(archivePath);
        var destination = Path.GetFullPath(projectDirectory);
        if (!fullArchivePath.EndsWith(".pulsepack", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Portable project archives must end in .pulsepack.", nameof(archivePath));
        }
        if (!destination.EndsWith(".pulseproj", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Working project directories must end in .pulseproj.", nameof(projectDirectory));
        }
        if (Directory.Exists(destination) || File.Exists(destination))
        {
            throw new IOException("The project import destination already exists.");
        }

        var parent = Path.GetDirectoryName(destination)!;
        Directory.CreateDirectory(parent);
        var staging = Path.Combine(parent, $".{Path.GetFileNameWithoutExtension(destination)}.import-{Guid.NewGuid():N}.pulseproj");
        Directory.CreateDirectory(staging);
        try
        {
            using var archive = ZipFile.OpenRead(fullArchivePath);
            if (archive.Entries.Count > MaximumArchiveEntries)
            {
                throw new InvalidDataException("PulsePack contains too many entries.");
            }

            var stagingPrefix = Path.GetFullPath(staging) + Path.DirectorySeparatorChar;
            long expandedBytes = 0;
            var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in archive.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                expandedBytes = checked(expandedBytes + entry.Length);
                if (expandedBytes > MaximumExpandedArchiveBytes)
                {
                    throw new InvalidDataException("PulsePack expanded size exceeds the safety limit.");
                }

                var target = Path.GetFullPath(Path.Combine(staging, entry.FullName.Replace('/', Path.DirectorySeparatorChar)));
                if (!target.StartsWith(stagingPrefix, StringComparison.OrdinalIgnoreCase) || !targets.Add(target))
                {
                    throw new InvalidDataException("PulsePack contains an unsafe or duplicate path.");
                }

                if (string.IsNullOrEmpty(entry.Name))
                {
                    Directory.CreateDirectory(target);
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                await using var input = entry.Open();
                await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough);
                await input.CopyToAsync(output, cancellationToken);
            }

            var scene = await LoadAsync(staging, cancellationToken);
            Directory.Move(staging, destination);
            return scene;
        }
        finally
        {
            if (Directory.Exists(staging))
            {
                Directory.Delete(staging, recursive: true);
            }
        }
    }

    public static string ResolveManifestPath(string projectDirectory)
    {
        var fullDirectory = Path.GetFullPath(projectDirectory);
        if (!fullDirectory.EndsWith(".pulseproj", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Working project directories must end in .pulseproj.", nameof(projectDirectory));
        }

        var manifestPath = Path.Combine(fullDirectory, "project.json");
        if (!File.Exists(manifestPath))
        {
            throw new FileNotFoundException("PulseGen project manifest was not found.", manifestPath);
        }

        return manifestPath;
    }
}
