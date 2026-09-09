using Avalonia;
using PulseGen.Contracts;
using Xunit;

namespace PulseGen.Editor.Tests;

public sealed class WorldViewportInteractionTests
{
    [Fact]
    public void EmptyWorldAndPathSelectionNeverSeekOrDirtyTheScene()
    {
        var viewport = CreateViewport();
        var edits = 0;
        viewport.SceneEdited += (_, _) => edits++;
        viewport.TimeSeconds = 2.3;
        Assert.Equal(WorldInteractionMode.Select, viewport.InteractionMode);
        Assert.False(viewport.BeginPrimaryInteraction(new(1180, 700)));
        var position = viewport.Scene!.MotionPaths[0].Keyframes[1].PositionMetres;
        Assert.False(viewport.BeginPrimaryInteraction(viewport.Camera.Project(position, viewport.Bounds.Size)));
        viewport.UpdatePrimaryInteraction(new(600, 400));
        viewport.CompletePrimaryInteraction();
        Assert.Equal(1, viewport.SelectedKeyframeIndex);
        Assert.Equal(position, viewport.Scene.MotionPaths[0].Keyframes[1].PositionMetres);
        Assert.Equal(2.3, viewport.TimeSeconds);
        Assert.Equal(0, edits);
    }

    [Fact]
    public void MoveModeMereClickDoesNotEditOrStopPlayback()
    {
        var viewport = CreateViewport();
        viewport.InteractionMode = WorldInteractionMode.Move;
        var started = 0;
        var edits = 0;
        viewport.EditStarted += (_, _) => started++;
        viewport.SceneEdited += (_, _) => edits++;
        var original = viewport.SelectedPosition!.Value;
        var point = viewport.Camera.Project(original, viewport.Bounds.Size);
        Assert.True(viewport.BeginPrimaryInteraction(point));
        viewport.UpdatePrimaryInteraction(new(point.X + 1, point.Y + 1));
        viewport.CompletePrimaryInteraction();
        Assert.Equal(original, viewport.SelectedPosition);
        Assert.Equal(0, started);
        Assert.Equal(0, edits);
    }

    [Fact]
    public void MovePreservesHeightAndTimeAndCommitsOnlyOnRelease()
    {
        var viewport = CreateViewport();
        viewport.InteractionMode = WorldInteractionMode.Move;
        viewport.TimeSeconds = 1.25;
        var started = 0;
        var edits = 0;
        viewport.EditStarted += (_, _) => started++;
        viewport.SceneEdited += (_, _) => edits++;
        var original = viewport.SelectedPosition!.Value;
        var point = viewport.Camera.Project(original, viewport.Bounds.Size);
        Assert.True(viewport.BeginPrimaryInteraction(point));
        viewport.UpdatePrimaryInteraction(new(point.X + 35, point.Y - 25));
        Assert.Equal(0, edits);
        Assert.Equal(1, started);
        Assert.NotEqual(original, viewport.SelectedPosition);
        Assert.Equal(original.Y, viewport.SelectedPosition!.Value.Y);
        viewport.CompletePrimaryInteraction();
        Assert.Equal(1, edits);
        Assert.Equal(1.25, viewport.TimeSeconds);
    }

    [Fact]
    public void CancellationRestoresOriginalPathAndDoesNotDirtyTheScene()
    {
        var viewport = CreateViewport();
        viewport.InteractionMode = WorldInteractionMode.Move;
        var original = viewport.SelectedPosition!.Value;
        var raw = viewport.Scene!.MotionPaths[0].RawGesture.ToArray();
        var edits = 0;
        viewport.SceneEdited += (_, _) => edits++;
        var point = viewport.Camera.Project(original, viewport.Bounds.Size);
        Assert.True(viewport.BeginPrimaryInteraction(point));
        viewport.UpdatePrimaryInteraction(new(point.X + 30, point.Y + 10));
        Assert.NotEqual(original, viewport.SelectedPosition);
        viewport.CancelInteraction();
        Assert.Equal(original, viewport.SelectedPosition);
        Assert.Equal(raw, viewport.Scene.MotionPaths[0].RawGesture);
        Assert.Equal(0, edits);
    }

    [Theory]
    [InlineData(WorldInteractionMode.Orbit)]
    [InlineData(WorldInteractionMode.Pan)]
    public void CameraToolsCannotMovePointsOrSeek(WorldInteractionMode mode)
    {
        var viewport = CreateViewport();
        viewport.InteractionMode = mode;
        viewport.TimeSeconds = 3.5;
        var original = viewport.SelectedPosition;
        var edits = 0;
        viewport.SceneEdited += (_, _) => edits++;
        var target = viewport.Camera.Target;
        var yaw = viewport.Camera.YawDegrees;
        Assert.True(viewport.BeginPrimaryInteraction(new(500, 300)));
        viewport.UpdatePrimaryInteraction(new(530, 325));
        viewport.CompletePrimaryInteraction();
        Assert.Equal(original, viewport.SelectedPosition);
        Assert.Equal(3.5, viewport.TimeSeconds);
        Assert.Equal(0, edits);
        if (mode == WorldInteractionMode.Orbit) Assert.NotEqual(yaw, viewport.Camera.YawDegrees);
        else Assert.NotEqual(target, viewport.Camera.Target);
    }

    [Fact]
    public void PreciseNumericPathEditValidatesAndRestoresInvalidSpeed()
    {
        var viewport = CreateViewport();
        var original = viewport.SelectedPosition!.Value;
        var edits = 0;
        var rejected = 0;
        viewport.SceneEdited += (_, _) => edits++;
        viewport.SceneEditRejected += _ => rejected++;
        Assert.False(viewport.TrySetSelectedPosition(new(100000, 2, 3)));
        Assert.Equal(original, viewport.SelectedPosition);
        Assert.Equal(1, rejected);
        Assert.Equal(0, edits);
        var target = original + new Vector3d(0.25, 0.5, 0.75);
        Assert.True(viewport.TrySetSelectedPosition(target));
        Assert.Equal(target, viewport.SelectedPosition);
        Assert.Equal(1, edits);
    }

    [Fact]
    public void StaticSourceSupportsAxisMovesAndCancellation()
    {
        var viewport = CreateViewport();
        var source = viewport.Scene!.Emitters[0];
        viewport.Scene.Emitters[0] = source with { MotionPathId = null, Transform = source.Transform with { PositionMetres = new(8, 2, 7) } };
        viewport.ActiveMotionPathId = null;
        viewport.Selection = SceneSelection.Emitter(source.Id);
        viewport.InteractionMode = WorldInteractionMode.Move;
        viewport.MoveConstraint = WorldMoveConstraint.X;
        var original = viewport.SelectedPosition!.Value;
        var point = viewport.Camera.Project(original, viewport.Bounds.Size);
        Assert.True(viewport.BeginPrimaryInteraction(point));
        viewport.UpdatePrimaryInteraction(new(point.X + 35, point.Y));
        Assert.NotEqual(original.X, viewport.SelectedPosition!.Value.X);
        Assert.Equal(original.Y, viewport.SelectedPosition!.Value.Y);
        Assert.Equal(original.Z, viewport.SelectedPosition!.Value.Z);
        viewport.CancelInteraction();
        Assert.Equal(original, viewport.SelectedPosition);
        Assert.True(viewport.TrySetSelectedPosition(new(8.125, 2.25, 7.5)));
        Assert.Equal(new Vector3d(8.125, 2.25, 7.5), viewport.SelectedPosition);
    }

    [Fact]
    public void ModeSwitchCancelsPendingMoveAndGestureArmingExitsCameraMode()
    {
        var viewport = CreateViewport();
        viewport.InteractionMode = WorldInteractionMode.Move;
        var original = viewport.SelectedPosition!.Value;
        var point = viewport.Camera.Project(original, viewport.Bounds.Size);
        Assert.True(viewport.BeginPrimaryInteraction(point));
        viewport.UpdatePrimaryInteraction(new(point.X + 35, point.Y));
        viewport.InteractionMode = WorldInteractionMode.Orbit;
        Assert.Equal(original, viewport.SelectedPosition);
        viewport.IsGestureRecording = true;
        Assert.Equal(WorldInteractionMode.Select, viewport.InteractionMode);
        viewport.InteractionMode = WorldInteractionMode.Pan;
        Assert.False(viewport.IsGestureRecording);
    }

    private static WorldViewport CreateViewport()
    {
        var scene = SampleScenes.CreateFlyby();
        var viewport = new WorldViewport { Scene = scene, ActiveMotionPathId = scene.MotionPaths[0].Id, Selection = SceneSelection.MotionPath(scene.MotionPaths[0].Id) };
        viewport.Arrange(new Rect(0, 0, 1200, 720));
        viewport.Camera.SetScene(scene, fit: true);
        return viewport;
    }
}
