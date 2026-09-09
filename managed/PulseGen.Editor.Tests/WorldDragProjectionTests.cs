using Avalonia;
using PulseGen.Contracts;
using Xunit;

namespace PulseGen.Editor.Tests;

public sealed class WorldDragProjectionTests
{
    private static readonly Size Size = new(1200, 720);

    [Theory]
    [InlineData(WorldMoveConstraint.GroundXZ)]
    [InlineData(WorldMoveConstraint.FrontXY)]
    [InlineData(WorldMoveConstraint.SideYZ)]
    [InlineData(WorldMoveConstraint.X)]
    [InlineData(WorldMoveConstraint.Y)]
    [InlineData(WorldMoveConstraint.Z)]
    public void DragKeepsLockedCoordinatesAndDoesNotJumpOnAnOffCentreGrab(WorldMoveConstraint constraint)
    {
        var camera = new ViewportCameraController();
        var original = new Vector3d(2.31, 1.73, -3.17);
        camera.Focus(original);
        var projected = camera.Project(original, Size);
        var pointer = new Point(projected.X + 8, projected.Y - 5);
        Assert.True(WorldDragProjection.TryBegin(camera, pointer, Size, original, constraint, out var drag));
        Assert.True(drag!.TryMove(camera, pointer, Size, false, 0.1, out var unchanged));
        AssertNear(original, unchanged);
        Assert.True(drag.TryMove(camera, new(pointer.X + 30, pointer.Y - 20), Size, false, 0.1, out var moved));
        Assert.True((moved - original).Length > 0.01);
        if (constraint is not (WorldMoveConstraint.GroundXZ or WorldMoveConstraint.FrontXY or WorldMoveConstraint.X)) Assert.Equal(original.X, moved.X);
        if (constraint is not (WorldMoveConstraint.FrontXY or WorldMoveConstraint.SideYZ or WorldMoveConstraint.Y)) Assert.Equal(original.Y, moved.Y);
        if (constraint is not (WorldMoveConstraint.GroundXZ or WorldMoveConstraint.SideYZ or WorldMoveConstraint.Z)) Assert.Equal(original.Z, moved.Z);
    }

    [Fact]
    public void SnappingOnlyRoundsTheUnlockedAxis()
    {
        var camera = new ViewportCameraController();
        var original = new Vector3d(2.31, 1.73, -3.17);
        camera.Focus(original);
        var pointer = camera.Project(original, Size);
        Assert.True(WorldDragProjection.TryBegin(camera, pointer, Size, original, WorldMoveConstraint.X, out var drag));
        Assert.True(drag!.TryMove(camera, new(pointer.X + 35, pointer.Y), Size, true, 0.1, out var result));
        Assert.Equal(Math.Round(result.X / 0.1), result.X / 0.1, 9);
        Assert.Equal(original.Y, result.Y);
        Assert.Equal(original.Z, result.Z);
    }

    [Theory]
    [InlineData((int)ViewportViewPreset.Front, WorldMoveConstraint.GroundXZ)]
    [InlineData((int)ViewportViewPreset.Right, WorldMoveConstraint.FrontXY)]
    [InlineData((int)ViewportViewPreset.Top, WorldMoveConstraint.SideYZ)]
    [InlineData((int)ViewportViewPreset.Top, WorldMoveConstraint.Y)]
    [InlineData((int)ViewportViewPreset.Right, WorldMoveConstraint.X)]
    [InlineData((int)ViewportViewPreset.Front, WorldMoveConstraint.Z)]
    public void EdgeOnPlanesAndEndOnAxesAreRejectedInsteadOfJumping(int view, WorldMoveConstraint constraint)
    {
        var camera = new ViewportCameraController();
        camera.SetViewPreset((ViewportViewPreset)view);
        Assert.False(WorldDragProjection.TryBegin(camera, new(600, 360), Size, camera.Target, constraint, out var drag));
        Assert.Null(drag);
    }

    [Theory]
    [InlineData((int)ViewportViewPreset.Top, WorldMoveConstraint.GroundXZ)]
    [InlineData((int)ViewportViewPreset.Front, WorldMoveConstraint.FrontXY)]
    [InlineData((int)ViewportViewPreset.Right, WorldMoveConstraint.SideYZ)]
    public void MatchingOrthographicViewGivesPrecisePlanarMovement(int view, WorldMoveConstraint constraint)
    {
        var camera = new ViewportCameraController();
        var original = new Vector3d(3, 2, -1);
        var target = original + (constraint switch
        {
            WorldMoveConstraint.GroundXZ => new Vector3d(1.5, 0, -2.5),
            WorldMoveConstraint.FrontXY => new Vector3d(1.5, 2.5, 0),
            _ => new Vector3d(0, 1.5, -2.5)
        });
        camera.Focus(original);
        camera.SetViewPreset((ViewportViewPreset)view);
        Assert.True(WorldDragProjection.TryBegin(camera, camera.Project(original, Size), Size, original, constraint, out var drag));
        Assert.True(drag!.TryMove(camera, camera.Project(target, Size), Size, false, 0.1, out var result));
        AssertNear(target, result);
    }

    private static void AssertNear(Vector3d expected, Vector3d actual)
        => Assert.InRange((actual - expected).Length, 0, 1.0e-4);
}
