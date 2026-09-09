using Avalonia;
using PulseGen.Contracts;

namespace PulseGen.Editor;

public enum WorldInteractionMode { Select, Move, Orbit, Pan }

public enum WorldMoveConstraint { GroundXZ, FrontXY, SideYZ, X, Y, Z }

/// <summary>A drag is anchored to its initial pointer hit, with a fixed plane and locked coordinates.</summary>
internal sealed class WorldDragProjection
{
    private readonly Vector3d original;
    private readonly Vector3d initialHit;
    private readonly Vector3d normal;
    private readonly WorldMoveConstraint constraint;

    private WorldDragProjection(Vector3d original, Vector3d initialHit, Vector3d normal, WorldMoveConstraint constraint)
        => (this.original, this.initialHit, this.normal, this.constraint) = (original, initialHit, normal, constraint);

    internal static bool TryBegin(ViewportCameraController camera, Point pointer, Size size,
        Vector3d original, WorldMoveConstraint constraint, out WorldDragProjection? drag)
    {
        drag = null;
        if (!Finite(original)) return false;
        var direction = camera.Ray(pointer, size).Direction;
        var normal = constraint switch
        {
            WorldMoveConstraint.GroundXZ => new Vector3d(0, 1, 0),
            WorldMoveConstraint.FrontXY => new Vector3d(0, 0, 1),
            WorldMoveConstraint.SideYZ => new Vector3d(1, 0, 0),
            WorldMoveConstraint.X => new Vector3d(0, direction.Y, direction.Z),
            WorldMoveConstraint.Y => new Vector3d(direction.X, 0, direction.Z),
            WorldMoveConstraint.Z => new Vector3d(direction.X, direction.Y, 0),
            _ => default
        };
        if (normal.Length < 0.08) return false;
        normal *= 1 / normal.Length;
        if (Math.Abs(Dot(direction, normal)) < 0.08 ||
            !camera.TryProjectToPlane(pointer, size, original, normal, out var hit)) return false;
        drag = new(original, hit, normal, constraint);
        return true;
    }

    internal bool TryMove(ViewportCameraController camera, Point pointer, Size size,
        bool snap, double stepMetres, out Vector3d position)
    {
        position = original;
        // Near-edge-on rays amplify small pointer movements into enormous jumps. Refuse them.
        if (Math.Abs(Dot(camera.Ray(pointer, size).Direction, normal)) < 0.08 ||
            !camera.TryProjectToPlane(pointer, size, original, normal, out var hit)) return false;
        var candidate = original + hit - initialHit;
        double Coordinate(double value) => snap && double.IsFinite(stepMetres) && stepMetres >= 0.001
            ? Math.Round(value / stepMetres, MidpointRounding.AwayFromZero) * stepMetres
            : value;
        position = new(
            constraint is WorldMoveConstraint.GroundXZ or WorldMoveConstraint.FrontXY or WorldMoveConstraint.X ? Coordinate(candidate.X) : original.X,
            constraint is WorldMoveConstraint.FrontXY or WorldMoveConstraint.SideYZ or WorldMoveConstraint.Y ? Coordinate(candidate.Y) : original.Y,
            constraint is WorldMoveConstraint.GroundXZ or WorldMoveConstraint.SideYZ or WorldMoveConstraint.Z ? Coordinate(candidate.Z) : original.Z);
        return Finite(position);
    }

    private static double Dot(Vector3d a, Vector3d b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
    private static bool Finite(Vector3d value) => double.IsFinite(value.X) && double.IsFinite(value.Y) && double.IsFinite(value.Z);
}
