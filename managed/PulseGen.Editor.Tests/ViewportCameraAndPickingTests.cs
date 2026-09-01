using Avalonia;
using PulseGen.Contracts;
using Xunit;

namespace PulseGen.Editor.Tests;

public sealed class ViewportCameraAndPickingTests
{
    [Fact]
    public void SceneCentreProjectsToViewportCentreAndHorizontalPlaneRoundTrips()
    {
        var camera = new ViewportCameraController();
        camera.SetScene(SampleScenes.CreateFlyby(), fit: true);
        var size = new Size(1200, 720);
        var centre = camera.ContentBounds.Centre;

        var projectedCentre = camera.Project(centre, size);
        var worldPoint = new Vector3d(centre.X + 2.5, centre.Y, centre.Z - 1.25);
        var projectedPoint = camera.Project(worldPoint, size);
        var recoveredPoint = camera.ProjectToHorizontalPlane(projectedPoint, size, worldPoint.Y);

        Assert.InRange(projectedCentre.X, 599.999, 600.001);
        Assert.InRange(projectedCentre.Y, 359.999, 360.001);
        AssertVectorNear(worldPoint, recoveredPoint, 2.0e-4);
    }

    [Fact]
    public void OrbitPanZoomAndFitAreBoundedAndDeterministic()
    {
        var camera = new ViewportCameraController();
        camera.SetScene(SampleScenes.CreateFlyby(), fit: true);
        var initialDistance = camera.DistanceMetres;
        var initialTarget = camera.Target;

        camera.Orbit(100, -5000);
        camera.Pan(30, -20, new Size(1000, 600));
        camera.Zoom(2);

        Assert.InRange(camera.PitchDegrees, -85, 85);
        Assert.NotEqual(initialTarget, camera.Target);
        Assert.True(camera.DistanceMetres < initialDistance);

        camera.Fit();

        AssertVectorNear(camera.ContentBounds.Centre, camera.Target, 1.0e-12);
        Assert.Equal(initialDistance, camera.DistanceMetres, 10);
    }

    [Fact]
    public void PixelRaySelectsTheSameTriangleProjectedByTheSharedCamera()
    {
        var camera = new ViewportCameraController();
        camera.SetScene(SampleScenes.CreateFlyby(), fit: true);
        var centre = camera.ContentBounds.Centre;
        var geometryId = Guid.NewGuid();
        var a = centre + new Vector3d(-3, 0, -3);
        var b = centre + new Vector3d(3, 0, -3);
        var c = centre + new Vector3d(0, 0, 3);
        var mesh = TriangleMesh(geometryId, a, b, c);
        var size = new Size(1280, 720);
        var pixel = camera.Project(centre, size);

        var result = ViewportPicker.Pick(mesh, camera.Ray(pixel, size));

        Assert.NotNull(result);
        Assert.Equal(geometryId, result.Value.GeometryId);
        Assert.Equal(0, result.Value.TriangleIndex);
        AssertVectorNear(centre, result.Value.PositionMetres, 2.0e-4);
    }

    [Fact]
    public void PickerReturnsNearestRenderedGeometryAndRejectsMisses()
    {
        var nearId = Guid.NewGuid();
        var farId = Guid.NewGuid();
        var vertices = new[]
        {
            -1F, -1F, 1F, 0F, 0F, 1F, 1F, 1F, 1F,
            1F, -1F, 1F, 0F, 0F, 1F, 1F, 1F, 1F,
            0F, 1F, 1F, 0F, 0F, 1F, 1F, 1F, 1F,
            -1F, -1F, 0F, 0F, 0F, 1F, 1F, 1F, 1F,
            1F, -1F, 0F, 0F, 0F, 1F, 1F, 1F, 1F,
            0F, 1F, 0F, 0F, 0F, 1F, 1F, 1F, 1F
        };
        var mesh = new ViewportMesh(
            vertices,
            [0, 1, 2, 3, 4, 5],
            new(-1, -1, 0),
            new(1, 1, 1),
            0,
            2,
            [
                new(nearId, 0, 3, new(-1, -1, 1), new(1, 1, 1)),
                new(farId, 3, 3, new(-1, -1, 0), new(1, 1, 0))
            ]);

        var hit = ViewportPicker.Pick(mesh, new(new(0, 0, 5), new(0, 0, -1)));
        var miss = ViewportPicker.Pick(mesh, new(new(3, 0, 5), new(0, 0, -1)));

        Assert.NotNull(hit);
        Assert.Equal(nearId, hit.Value.GeometryId);
        Assert.Equal(4, hit.Value.DistanceMetres, 10);
        Assert.Null(miss);
    }

    [Fact]
    public void QuarterMillionTriangleTerrainCanBeSelectedByItsRenderedFace()
    {
        var scene = SampleScenes.CreateViewportStress();
        var mesh = ViewportMeshBuilder.Build(scene);
        var section = Assert.Single(mesh.Sections);
        var centreTriangleOffset = section.FirstIndex + ((section.IndexCount / 6) * 3);
        var a = ReadPosition(mesh, mesh.Indices[centreTriangleOffset]);
        var b = ReadPosition(mesh, mesh.Indices[centreTriangleOffset + 1]);
        var c = ReadPosition(mesh, mesh.Indices[centreTriangleOffset + 2]);
        var faceCentre = (a + b + c) * (1D / 3D);
        var camera = new ViewportCameraController();
        camera.SetScene(scene, fit: true);
        camera.ExtendContentBounds(mesh.BoundsMinimum, mesh.BoundsMaximum, fit: true);
        var size = new Size(1280, 720);

        var hit = ViewportPicker.Pick(mesh, camera.Ray(camera.Project(faceCentre, size), size));

        Assert.NotNull(hit);
        Assert.Equal(scene.Geometry[0].Id, hit.Value.GeometryId);
    }

    private static ViewportMesh TriangleMesh(Guid geometryId, Vector3d a, Vector3d b, Vector3d c)
    {
        var vertices = new float[ViewportMesh.FloatsPerVertex * 3];
        WriteVertex(vertices, 0, a);
        WriteVertex(vertices, ViewportMesh.FloatsPerVertex, b);
        WriteVertex(vertices, ViewportMesh.FloatsPerVertex * 2, c);
        var minimum = new Vector3d(
            Math.Min(a.X, Math.Min(b.X, c.X)),
            Math.Min(a.Y, Math.Min(b.Y, c.Y)),
            Math.Min(a.Z, Math.Min(b.Z, c.Z)));
        var maximum = new Vector3d(
            Math.Max(a.X, Math.Max(b.X, c.X)),
            Math.Max(a.Y, Math.Max(b.Y, c.Y)),
            Math.Max(a.Z, Math.Max(b.Z, c.Z)));
        return new(
            vertices,
            [0, 1, 2],
            minimum,
            maximum,
            0,
            1,
            [new(geometryId, 0, 3, minimum, maximum)]);
    }

    private static void WriteVertex(float[] vertices, int offset, Vector3d value)
    {
        vertices[offset] = (float)value.X;
        vertices[offset + 1] = (float)value.Y;
        vertices[offset + 2] = (float)value.Z;
        vertices[offset + 3] = 0;
        vertices[offset + 4] = 1;
        vertices[offset + 5] = 0;
        vertices[offset + 6] = 1;
        vertices[offset + 7] = 1;
        vertices[offset + 8] = 1;
    }

    private static Vector3d ReadPosition(ViewportMesh mesh, uint vertexIndex)
    {
        var offset = checked((int)vertexIndex * ViewportMesh.FloatsPerVertex);
        return new(
            mesh.InterleavedPositionNormals[offset],
            mesh.InterleavedPositionNormals[offset + 1],
            mesh.InterleavedPositionNormals[offset + 2]);
    }

    private static void AssertVectorNear(Vector3d expected, Vector3d actual, double tolerance)
    {
        Assert.InRange(Math.Abs(expected.X - actual.X), 0, tolerance);
        Assert.InRange(Math.Abs(expected.Y - actual.Y), 0, tolerance);
        Assert.InRange(Math.Abs(expected.Z - actual.Z), 0, tolerance);
    }
}
