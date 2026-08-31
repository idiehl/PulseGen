using PulseGen.Contracts;
using System.Globalization;
using System.Text;

namespace PulseGen.Editor;

internal sealed record ViewportImportedMeshFixture(
    SceneDocument Scene,
    string ProjectDirectory,
    string AssetSha256)
{
    private const int CellsX = 500;
    private const int CellsZ = 250;

    public static ViewportImportedMeshFixture Prepare(string receiptPath) =>
        Task.Run(() => PrepareAsync(receiptPath)).GetAwaiter().GetResult();

    private static async Task<ViewportImportedMeshFixture> PrepareAsync(string receiptPath)
    {
        var benchmarkDirectory = Path.GetDirectoryName(Path.GetFullPath(receiptPath))
            ?? throw new InvalidOperationException("Viewport benchmark receipt requires a parent directory.");
        Directory.CreateDirectory(benchmarkDirectory);
        var projectDirectory = Path.Combine(benchmarkDirectory, "ImportedMesh250K.pulseproj");
        var sourcePath = Path.Combine(benchmarkDirectory, "imported-mesh-250k.obj");
        WriteObjAtomically(sourcePath);

        var scene = SampleScenes.CreateFlyby();
        scene.Geometry.Clear();
        await ProjectStore.SaveAsync(scene, projectDirectory);
        var asset = await ProjectStore.ImportAssetAsync(
            projectDirectory,
            sourcePath,
            AssetKind.Mesh,
            "model/obj",
            provenance: "PulseGen deterministic 250K imported-viewport acceptance fixture",
            license: "CC0-1.0");
        scene.Assets.RemoveAll(item => item.Kind == AssetKind.Mesh);
        scene.Assets.Add(asset);
        scene.Geometry.Add(new AcousticGeometry
        {
            Id = Guid.Parse("f2f2f2f2-0000-4000-8000-000000000001"),
            Name = "Verified 250K triangle OBJ terrain",
            Kind = GeometryKind.ImportedMesh,
            MaterialId = scene.Materials[0].Id,
            MeshAssetHash = asset.Sha256,
            DimensionsMetres = new(50, 6, 25),
            Transform = new Transform { PositionMetres = new(0, -1.5, 0) },
            Occludes = true,
            Reflects = false
        });
        scene = scene with
        {
            ProjectId = Guid.Parse("f2f2f2f2-0000-4000-8000-000000000000"),
            Name = "Imported Mesh 250K Triangle Stress",
            UpdatedAtUtc = new DateTimeOffset(2026, 8, 31, 0, 0, 0, TimeSpan.Zero)
        };
        await ProjectStore.SaveAsync(scene, projectDirectory);
        return new(scene, projectDirectory, asset.Sha256);
    }

    private static void WriteObjAtomically(string destination)
    {
        var directory = Path.GetDirectoryName(destination)
            ?? throw new InvalidOperationException("Imported viewport fixture requires a parent directory.");
        var temporary = Path.Combine(directory, $".{Path.GetFileName(destination)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, FileOptions.SequentialScan))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), 1024 * 1024))
            {
                writer.WriteLine("o PulseGenImportedViewportStress");
                for (var zIndex = 0; zIndex <= CellsZ; zIndex++)
                {
                    var z = ((zIndex / (double)CellsZ) - 0.5) * 25;
                    for (var xIndex = 0; xIndex <= CellsX; xIndex++)
                    {
                        var x = ((xIndex / (double)CellsX) - 0.5) * 50;
                        var y = 1.08 * ((0.62 * Math.Sin(x * 0.23)) + (0.38 * Math.Cos(z * 0.31)));
                        writer.Write("v ");
                        writer.Write(x.ToString("R", CultureInfo.InvariantCulture));
                        writer.Write(' ');
                        writer.Write(y.ToString("R", CultureInfo.InvariantCulture));
                        writer.Write(' ');
                        writer.WriteLine(z.ToString("R", CultureInfo.InvariantCulture));
                    }
                }

                const int verticesX = CellsX + 1;
                for (var zIndex = 0; zIndex < CellsZ; zIndex++)
                {
                    for (var xIndex = 0; xIndex < CellsX; xIndex++)
                    {
                        var topLeft = (zIndex * verticesX) + xIndex + 1;
                        var topRight = topLeft + 1;
                        var bottomLeft = ((zIndex + 1) * verticesX) + xIndex + 1;
                        var bottomRight = bottomLeft + 1;
                        WriteFace(writer, topLeft, bottomLeft, topRight);
                        WriteFace(writer, topRight, bottomLeft, bottomRight);
                    }
                }
            }
            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private static void WriteFace(TextWriter writer, int a, int b, int c)
    {
        writer.Write("f ");
        writer.Write(a.ToString(CultureInfo.InvariantCulture));
        writer.Write(' ');
        writer.Write(b.ToString(CultureInfo.InvariantCulture));
        writer.Write(' ');
        writer.WriteLine(c.ToString(CultureInfo.InvariantCulture));
    }
}
