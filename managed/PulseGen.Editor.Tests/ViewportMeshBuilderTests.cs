using PulseGen.Contracts;
using Xunit;

namespace PulseGen.Editor.Tests;

public sealed class ViewportMeshBuilderTests
{
    [Fact]
    public void StressTerrainBuildsExactlyQuarterMillionIndexedTriangles()
    {
        var mesh = ViewportMeshBuilder.Build(SampleScenes.CreateViewportStress());

        Assert.Equal(250_000, mesh.TriangleCount);
        Assert.Equal(125_751, mesh.VertexCount);
        Assert.Equal(750_000, mesh.Indices.Length);
        Assert.All(mesh.Indices, index => Assert.InRange(index, 0U, (uint)mesh.VertexCount - 1));
        Assert.True(mesh.BoundsMinimum.X < -24.9);
        Assert.True(mesh.BoundsMaximum.X > 24.9);
        Assert.True(mesh.BoundsMinimum.Z < -12.4);
        Assert.True(mesh.BoundsMaximum.Z > 12.4);
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
    public void MeshBuildKeyIgnoresAudioEditsButTracksTerrainMovement()
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
        Assert.NotEqual(original, ViewportMeshBuilder.KeyFor(scene));
    }
}
