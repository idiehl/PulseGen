using PulseGen.Contracts;
using System.Text;
using Xunit;

namespace PulseGen.Editor.Tests;

public sealed class ViewportMeshBuilderTests
{
    [Fact]
    public void StressTerrainBuildsExactlyQuarterMillionIndexedTriangles()
    {
        var scene = SampleScenes.CreateViewportStress();
        var mesh = ViewportMeshBuilder.Build(scene);

        Assert.Equal(250_000, mesh.TriangleCount);
        Assert.Equal(125_751, mesh.VertexCount);
        Assert.Equal(750_000, mesh.Indices.Length);
        Assert.All(mesh.Indices, index => Assert.InRange(index, 0U, (uint)mesh.VertexCount - 1));
        Assert.True(mesh.BoundsMinimum.X < -24.9);
        Assert.True(mesh.BoundsMaximum.X > 24.9);
        Assert.True(mesh.BoundsMinimum.Z < -12.4);
        Assert.True(mesh.BoundsMaximum.Z > 12.4);
        var terrain = Assert.Single(mesh.Sections);
        Assert.Equal(scene.Geometry[0].Id, terrain.GeometryId);
        Assert.Equal(mesh.Indices.Length, terrain.IndexCount);
    }

    [Fact]
    public void TerrainMeshIsDeterministicAndCarriesUnitNormals()
    {
        var scene = SampleScenes.CreateViewportStress();
        var first = ViewportMeshBuilder.Build(scene);
        var second = ViewportMeshBuilder.Build(scene);

        Assert.Equal(first.Indices, second.Indices);
        Assert.Equal(first.InterleavedPositionNormals, second.InterleavedPositionNormals);
        for (var offset = 0; offset < first.InterleavedPositionNormals.Length; offset += ViewportMesh.FloatsPerVertex * 997)
        {
            var normalOffset = offset + 3;
            var x = first.InterleavedPositionNormals[normalOffset];
            var y = first.InterleavedPositionNormals[normalOffset + 1];
            var z = first.InterleavedPositionNormals[normalOffset + 2];
            Assert.InRange(Math.Sqrt((x * x) + (y * y) + (z * z)), 0.9999, 1.0001);
        }
    }

    [Fact]
    public void SceneWithoutTerrainProducesAnEmptyGpuMesh()
    {
        var mesh = ViewportMeshBuilder.Build(SampleScenes.CreateFlyby());

        Assert.Equal(0, mesh.VertexCount);
        Assert.Equal(0, mesh.TriangleCount);
    }

    [Fact]
    public void MeshBuildKeyIgnoresAudioEditsButTracksGeometryAndAcousticMaterialColoring()
    {
        var scene = SampleScenes.CreateViewportStress();
        var original = ViewportMeshBuilder.KeyFor(scene);

        scene.Emitters[0] = scene.Emitters[0] with { Name = "Renamed source" };
        Assert.Equal(original, ViewportMeshBuilder.KeyFor(scene));

        var terrain = scene.Geometry[0];
        scene.Geometry[0] = terrain with
        {
            Transform = terrain.Transform with { PositionMetres = new(3, -1.5, 0) }
        };
        var moved = ViewportMeshBuilder.KeyFor(scene);
        Assert.NotEqual(original, moved);

        scene.Materials[0] = scene.Materials[0] with
        {
            Absorption = [0.8, 0.8, 0.8, 0.8, 0.8, 0.8, 0.8, 0.8]
        };
        Assert.NotEqual(moved, ViewportMeshBuilder.KeyFor(scene));
    }

    [Fact]
    public async Task VerifiedObjTrianglesAreCombinedWithTerrainInsteadOfUsingTheAuthoredProxy()
    {
        var root = Path.Combine(Path.GetTempPath(), $"pulsegen-viewport-mesh-{Guid.NewGuid():N}");
        var project = Path.Combine(root, "ViewportMesh.pulseproj");
        var sourcePath = Path.Combine(root, "wall.obj");
        const string obj = """
            o VisibleWall
            v -1 -1 0
            v 1 -1 0
            v 1 1 0
            v -1 1 0
            f 1 2 3
            f 1 3 4
            """;
        try
        {
            Directory.CreateDirectory(root);
            await File.WriteAllTextAsync(sourcePath, obj, Encoding.UTF8);
            var scene = SampleScenes.CreateFlyby();
            scene.Geometry.Clear();
            await ProjectStore.SaveAsync(scene, project);
            var asset = await ProjectStore.ImportAssetAsync(project, sourcePath, AssetKind.Mesh, "model/obj");
            scene.Assets.Add(asset);
            scene.Geometry.Add(new AcousticGeometry
            {
                Id = Guid.NewGuid(),
                Name = "Actual imported wall",
                Kind = GeometryKind.ImportedMesh,
                MaterialId = scene.Materials[0].Id,
                MeshAssetHash = asset.Sha256,
                Transform = new Transform
                {
                    PositionMetres = new(2, 0, 4),
                    Scale = new(2, 1, 1)
                },
                DimensionsMetres = new(99, 99, 99),
                Occludes = true,
                Reflects = false
            });
            scene.Geometry.Add(new AcousticGeometry
            {
                Id = Guid.NewGuid(),
                Name = "Small terrain",
                Kind = GeometryKind.Terrain,
                MaterialId = scene.Materials[0].Id,
                DimensionsMetres = new(1, 1, 1),
                Occludes = false,
                Reflects = false
            });
            await ProjectStore.SaveAsync(scene, project);

            var mesh = ViewportMeshBuilder.Build(scene, project);

            Assert.Equal(202, mesh.TriangleCount);
            Assert.Equal(200, mesh.TerrainTriangleCount);
            Assert.Equal(2, mesh.ImportedTriangleCount);
            Assert.Equal(1, mesh.ImportedMeshCount);
            Assert.Equal(2, mesh.Sections.Count);
            Assert.Contains(mesh.Sections, section => section.GeometryId == scene.Geometry[0].Id && section.IndexCount == 6);
            Assert.Contains(mesh.Sections, section => section.GeometryId == scene.Geometry[1].Id && section.IndexCount == 600);
            Assert.Equal(127, mesh.VertexCount);
            Assert.Contains(mesh.InterleavedPositionNormals.Chunk(ViewportMesh.FloatsPerVertex), vertex =>
                Math.Abs(vertex[0] + 0.0F) < 1.0e-6F &&
                Math.Abs(vertex[1] + 1.0F) < 1.0e-6F &&
                Math.Abs(vertex[2] - 4.0F) < 1.0e-6F);
            Assert.NotEqual(
                ViewportMeshBuilder.KeyFor(scene, project),
                ViewportMeshBuilder.KeyFor(scene, Path.Combine(root, "Other.pulseproj")));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
