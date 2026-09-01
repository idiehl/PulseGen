using PulseGen.Contracts;

namespace PulseGen.Editor;

internal readonly record struct ViewportPickResult(
    Guid GeometryId,
    int TriangleIndex,
    double DistanceMetres,
    Vector3d PositionMetres);

internal static class ViewportPicker
{
    public static ViewportPickResult? Pick(ViewportMesh mesh, ViewportRay ray)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        var closestDistance = double.PositiveInfinity;
        ViewportPickResult? closest = null;
        foreach (var section in mesh.Sections)
        {
            if (!IntersectsBounds(ray, section.BoundsMinimum, section.BoundsMaximum, closestDistance))
            {
                continue;
            }
            var end = checked(section.FirstIndex + section.IndexCount);
            for (var index = section.FirstIndex; index < end; index += 3)
            {
                var a = ReadPosition(mesh, mesh.Indices[index]);
                var b = ReadPosition(mesh, mesh.Indices[index + 1]);
                var c = ReadPosition(mesh, mesh.Indices[index + 2]);
                if (!IntersectTriangle(ray, a, b, c, out var distance) || distance >= closestDistance)
                {
                    continue;
                }
                closestDistance = distance;
                closest = new(
                    section.GeometryId,
                    index / 3,
                    distance,
                    ray.Origin + (ray.Direction * distance));
            }
        }
        return closest;
    }

    private static Vector3d ReadPosition(ViewportMesh mesh, uint vertexIndex)
    {
        var offset = checked((int)vertexIndex * ViewportMesh.FloatsPerVertex);
        return new(
            mesh.InterleavedPositionNormals[offset],
            mesh.InterleavedPositionNormals[offset + 1],
            mesh.InterleavedPositionNormals[offset + 2]);
    }

    private static bool IntersectTriangle(
        ViewportRay ray,
        Vector3d a,
        Vector3d b,
        Vector3d c,
        out double distance)
    {
        const double epsilon = 1.0e-9;
        var edgeAb = b - a;
        var edgeAc = c - a;
        var p = Cross(ray.Direction, edgeAc);
        var determinant = Dot(edgeAb, p);
        if (Math.Abs(determinant) < epsilon)
        {
            distance = 0;
            return false;
        }
        var inverseDeterminant = 1 / determinant;
        var originToA = ray.Origin - a;
        var u = Dot(originToA, p) * inverseDeterminant;
        if (u < 0 || u > 1)
        {
            distance = 0;
            return false;
        }
        var q = Cross(originToA, edgeAb);
        var v = Dot(ray.Direction, q) * inverseDeterminant;
        if (v < 0 || u + v > 1)
        {
            distance = 0;
            return false;
        }
        distance = Dot(edgeAc, q) * inverseDeterminant;
        return distance > epsilon && double.IsFinite(distance);
    }

    private static bool IntersectsBounds(
        ViewportRay ray,
        Vector3d minimum,
        Vector3d maximum,
        double maximumDistance)
    {
        var near = 0D;
        var far = maximumDistance;
        return IntersectSlab(ray.Origin.X, ray.Direction.X, minimum.X, maximum.X, ref near, ref far) &&
            IntersectSlab(ray.Origin.Y, ray.Direction.Y, minimum.Y, maximum.Y, ref near, ref far) &&
            IntersectSlab(ray.Origin.Z, ray.Direction.Z, minimum.Z, maximum.Z, ref near, ref far) &&
            far >= near;
    }

    private static bool IntersectSlab(
        double origin,
        double direction,
        double minimum,
        double maximum,
        ref double near,
        ref double far)
    {
        if (Math.Abs(direction) < 1.0e-12)
        {
            return origin >= minimum && origin <= maximum;
        }
        var first = (minimum - origin) / direction;
        var second = (maximum - origin) / direction;
        if (first > second)
        {
            (first, second) = (second, first);
        }
        near = Math.Max(near, first);
        far = Math.Min(far, second);
        return far >= near;
    }

    private static double Dot(Vector3d left, Vector3d right) =>
        (left.X * right.X) + (left.Y * right.Y) + (left.Z * right.Z);

    private static Vector3d Cross(Vector3d left, Vector3d right) => new(
        (left.Y * right.Z) - (left.Z * right.Y),
        (left.Z * right.X) - (left.X * right.Z),
        (left.X * right.Y) - (left.Y * right.X));
}
