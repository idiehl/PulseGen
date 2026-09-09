using Avalonia;
using PulseGen.Contracts;
using System.Numerics;
using Xunit;

namespace PulseGen.Editor.Tests;

public sealed class ViewportCoordinateCameraTests
{
    [Theory]
    [InlineData(0, 1200, 720)]
    [InlineData(1, 1200, 720)]
    [InlineData(2, 1200, 720)]
    [InlineData(3, 1200, 720)]
    [InlineData(0, 2400, 100)]
    [InlineData(1, 2400, 100)]
    [InlineData(0, 100, 2400)]
    [InlineData(3, 100, 2400)]
    public void OverlayProjectionGpuMatrixAndPickingRayAgree(int preset, int width, int height)
    {
        var camera = Camera((ViewportViewPreset)preset);
        var size = new Size(width, height);
        var point = camera.Target + new Vector3d(2, 1.25, -1.5);
        var projected = camera.Project(point, size);
        var clip = Vector4.Transform(new Vector4((float)point.X, (float)point.Y, (float)point.Z, 1), camera.ViewProjection(size));
        Assert.Equal((clip.X / clip.W + 1) * size.Width * 0.5, projected.X, 5);
        Assert.Equal((1 - clip.Y / clip.W) * size.Height * 0.5, projected.Y, 5);

        var ray = camera.Ray(projected, size);
        var displacement = point - ray.Origin;
        var rayDistance = Dot(displacement, ray.Direction);
        Assert.True(rayDistance > 0);
        AssertVectorNear(point, ray.Origin + (ray.Direction * rayDistance), 2.0e-4);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void OrthographicViewsHaveParallelRaysAndNoDepthScaling(int preset)
    {
        var camera = Camera((ViewportViewPreset)preset);
        var size = new Size(1200, 720);
        var centreRay = camera.Ray(new(600, 360), size);
        var cornerRay = camera.Ray(new(130, 87), size);
        AssertVectorNear(centreRay.Direction, cornerRay.Direction, 1.0e-12);
        Assert.NotEqual(centreRay.Origin, cornerRay.Origin);

        var point = camera.Target + new Vector3d(2, 1.5, -2);
        var near = camera.Project(point - (centreRay.Direction * 5), size);
        var far = camera.Project(point + (centreRay.Direction * 5), size);
        Assert.Equal(near.X, far.X, 4);
        Assert.Equal(near.Y, far.Y, 4);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void OpenGlClipDepthPlacesTheNearAndFarPlanesAtMinusOneAndPlusOne(int preset)
    {
        var camera = Camera((ViewportViewPreset)preset);
        var size = new Size(1200, 720);
        var ray = camera.Ray(new(600, 360), size);
        var radius = camera.ContentBounds.Radius;
        var nearDistance = Math.Max(0.01, radius * 0.005);
        var farDistance = Math.Max(100, camera.DistanceMetres + (radius * 8));
        var matrix = camera.ViewProjection(size);

        double ClipDepthAt(double distance)
        {
            var point = ray.Origin + (ray.Direction * distance);
            var clip = Vector4.Transform(new Vector4((float)point.X, (float)point.Y, (float)point.Z, 1), matrix);
            return clip.Z / clip.W;
        }

        Assert.InRange(ClipDepthAt(nearDistance), -1.001, -0.999);
        Assert.InRange(ClipDepthAt(farDistance), 0.999, 1.001);
        if (preset != 0)
        {
            Assert.True(ClipDepthAt(-1) < -1);
        }
    }

    [Theory]
    [InlineData(1, 1, 0, 0, 0, 0, -1)]
    [InlineData(2, 1, 0, 0, 0, 1, 0)]
    [InlineData(3, 0, 0, -1, 0, 1, 0)]
    public void AxisViewsHaveConsistentCoordinateDirections(int preset, double rx, double ry, double rz, double ux, double uy, double uz)
    {
        var camera = Camera((ViewportViewPreset)preset);
        var size = new Size(1200, 720);
        var centre = camera.Project(camera.Target, size);
        var right = camera.Project(camera.Target + new Vector3d(rx, ry, rz), size);
        var up = camera.Project(camera.Target + new Vector3d(ux, uy, uz), size);
        Assert.True(right.X > centre.X);
        Assert.Equal(centre.Y, right.Y, 4);
        Assert.True(up.Y < centre.Y);
        Assert.Equal(centre.X, up.X, 4);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void FocusAndOrbitKeepTheSelectedPivotFixedAtTheCentre(int preset)
    {
        var camera = Camera((ViewportViewPreset)preset);
        var size = new Size(1200, 720);
        var pivot = new Vector3d(7.25, 1.7, -4.5);
        var originalDistance = camera.DistanceMetres;
        camera.Focus(pivot);
        camera.Orbit(67, -19);

        Assert.Equal(ViewportViewPreset.Perspective, camera.ViewPreset);
        Assert.Equal(pivot, camera.Target);
        Assert.Equal(originalDistance, camera.DistanceMetres);
        var projected = camera.Project(pivot, size);
        Assert.InRange(projected.X, 599.999, 600.001);
        Assert.InRange(projected.Y, 359.999, 360.001);
        Assert.Equal(originalDistance, (camera.Ray(projected, size).Origin - pivot).Length, 8);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void PanAndZoomHaveTheSameMeaningAcrossAllViews(int preset)
    {
        var camera = Camera((ViewportViewPreset)preset);
        var size = new Size(1200, 720);
        var point = camera.Target;
        camera.Pan(30, -25, size);
        var projected = camera.Project(point, size);
        Assert.InRange(projected.X, 629.999, 630.001);
        Assert.InRange(projected.Y, 334.999, 335.001);
        var beforeZoom = camera.DistanceMetres;
        camera.Zoom(2);
        Assert.True(camera.DistanceMetres < beforeZoom);
        Assert.Equal((ViewportViewPreset)preset, camera.ViewPreset);
    }

    [Theory]
    [InlineData(1, 0, 1, 0)]
    [InlineData(2, 0, 0, 1)]
    [InlineData(3, 1, 0, 0)]
    public void EditingPlaneRoundTripsAlongEachPrincipalView(int preset, double nx, double ny, double nz)
    {
        var camera = Camera((ViewportViewPreset)preset);
        var size = new Size(1200, 720);
        var point = camera.Target + new Vector3d(2, -1, 3);
        Assert.True(camera.TryProjectToPlane(camera.Project(point, size), size, point, new Vector3d(nx, ny, nz) * 9, out var hit));
        AssertVectorNear(point, hit, 2.0e-4);
    }

    [Fact]
    public void PerspectiveEditingSupportsAnObliquePlane()
    {
        var camera = Camera(ViewportViewPreset.Perspective);
        var size = new Size(1200, 720);
        var point = camera.Target + new Vector3d(2, 1, -3);
        Assert.True(camera.TryProjectToPlane(camera.Project(point, size), size, point, new(1, 2, 3), out var hit));
        AssertVectorNear(point, hit, 2.0e-4);
    }

    [Fact]
    public void UnsafeEditingPlaneIntersectionsAreRejectedWithoutInventingAPosition()
    {
        var camera = Camera(ViewportViewPreset.Top);
        var size = new Size(1200, 720);
        var point = new Point(600, 360);
        var ray = camera.Ray(point, size);
        Assert.False(camera.TryProjectToPlane(point, size, camera.Target, new(1, 0, 0), out var edgeOn));
        Assert.False(camera.TryProjectToPlane(point, size, camera.Target, new(1, 0.00001, 0), out var nearEdgeOn));
        Assert.False(camera.TryProjectToPlane(point, size, ray.Origin + new Vector3d(0, 1, 0), new(0, 1, 0), out var behindCamera));
        Assert.False(camera.TryProjectToPlane(point, size, camera.Target, default, out _));
        Assert.False(camera.TryProjectToPlane(point, default, camera.Target, new(0, 1, 0), out _));
        Assert.False(camera.TryProjectToPlane(new(double.NaN, 0), size, camera.Target, new(0, 1, 0), out _));
        Assert.False(camera.TryProjectToPlane(point, size, new(double.PositiveInfinity, 0, 0), new(0, 1, 0), out _));
        Assert.Equal(default, edgeOn);
        Assert.Equal(default, nearEdgeOn);
        Assert.Equal(default, behindCamera);
    }

    [Fact]
    public void InvalidCameraInputDoesNotChangeTheUsersView()
    {
        var camera = Camera(ViewportViewPreset.Top);
        var target = camera.Target;
        var distance = camera.DistanceMetres;
        camera.Focus(new(double.NaN, 0, 0));
        camera.SetViewPreset((ViewportViewPreset)99);
        camera.Orbit(double.PositiveInfinity, 1);
        camera.Orbit(0, 0);
        camera.Pan(1, 2, default);
        camera.Zoom(double.NaN);
        Assert.Equal(ViewportViewPreset.Top, camera.ViewPreset);
        Assert.Equal(target, camera.Target);
        Assert.Equal(distance, camera.DistanceMetres);
    }

    private static ViewportCameraController Camera(ViewportViewPreset preset)
    {
        var camera = new ViewportCameraController();
        camera.SetScene(SampleScenes.CreateFlyby(), fit: true);
        camera.SetViewPreset(preset);
        return camera;
    }

    private static double Dot(Vector3d left, Vector3d right) =>
        (left.X * right.X) + (left.Y * right.Y) + (left.Z * right.Z);

    private static void AssertVectorNear(Vector3d expected, Vector3d actual, double tolerance)
    {
        Assert.InRange(Math.Abs(expected.X - actual.X), 0, tolerance);
        Assert.InRange(Math.Abs(expected.Y - actual.Y), 0, tolerance);
        Assert.InRange(Math.Abs(expected.Z - actual.Z), 0, tolerance);
    }
}
