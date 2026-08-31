using PulseGen.Contracts;

namespace PulseGen.Editor;

internal sealed record ViewportMesh(
    float[] InterleavedPositionNormals,
    uint[] Indices,
    Vector3d BoundsMinimum,
    Vector3d BoundsMaximum)
{
    public const int FloatsPerVertex = 6;

    public int VertexCount => InterleavedPositionNormals.Length / FloatsPerVertex;

    public int TriangleCount => Indices.Length / 3;

    public static ViewportMesh Empty { get; } = new([], [], Vector3d.Zero, Vector3d.Zero);
}

internal readonly record struct ViewportMeshBuildKey(
    bool HasTerrain,
    Guid GeometryId,
    Vector3d PositionMetres,
    Vector3d RotationDegrees,
    Vector3d Scale,
    Vector3d DimensionsMetres);

internal static class ViewportMeshBuilder
{
    internal const int TerrainCellsPerMetre = 10;
    internal const int MaximumTerrainCellsPerAxis = 500;
    internal const int MaximumViewportTriangles = 250_000;

    public static ViewportMeshBuildKey KeyFor(SceneDocument scene)
    {
        ArgumentNullException.ThrowIfNull(scene);
        var terrain = scene.Geometry.FirstOrDefault(geometry => geometry.Kind == GeometryKind.Terrain);
        return terrain is null
            ? default
            : new(
                true,
                terrain.Id,
                terrain.Transform.PositionMetres,
                terrain.Transform.RotationDegrees,
                terrain.Transform.Scale,
                terrain.DimensionsMetres);
    }

    public static ViewportMesh Build(SceneDocument scene, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scene);
        var terrain = scene.Geometry.FirstOrDefault(geometry => geometry.Kind == GeometryKind.Terrain);
        if (terrain is null)
        {
            return ViewportMesh.Empty;
        }

        var cellsX = Math.Clamp(
            checked((int)Math.Round(terrain.DimensionsMetres.X * TerrainCellsPerMetre, MidpointRounding.AwayFromZero)),
            1,
            MaximumTerrainCellsPerAxis);
        var cellsZ = Math.Clamp(
            checked((int)Math.Round(terrain.DimensionsMetres.Z * TerrainCellsPerMetre, MidpointRounding.AwayFromZero)),
            1,
            MaximumTerrainCellsPerAxis);
        if ((long)cellsX * cellsZ * 2 > MaximumViewportTriangles)
        {
            var scale = Math.Sqrt(MaximumViewportTriangles / (2.0 * cellsX * cellsZ));
            cellsX = Math.Max(1, (int)Math.Floor(cellsX * scale));
            cellsZ = Math.Max(1, (int)Math.Floor(cellsZ * scale));
        }
        return BuildTerrain(terrain, cellsX, cellsZ, cancellationToken);
    }

    private static ViewportMesh BuildTerrain(
        AcousticGeometry geometry,
        int cellsX,
        int cellsZ,
        CancellationToken cancellationToken)
    {
        var verticesX = checked(cellsX + 1);
        var verticesZ = checked(cellsZ + 1);
        var vertexCount = checked(verticesX * verticesZ);
        var triangleCount = checked(cellsX * cellsZ * 2);
        var vertices = new float[checked(vertexCount * ViewportMesh.FloatsPerVertex)];
        var indices = new uint[checked(triangleCount * 3)];
        var minimum = new Vector3d(double.PositiveInfinity, double.PositiveInfinity, double.PositiveInfinity);
        var maximum = new Vector3d(double.NegativeInfinity, double.NegativeInfinity, double.NegativeInfinity);
        var amplitude = geometry.DimensionsMetres.Y * 0.18;

        for (var zIndex = 0; zIndex < verticesZ; zIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var zAmount = zIndex / (double)cellsZ;
            var localZ = (zAmount - 0.5) * geometry.DimensionsMetres.Z;
            for (var xIndex = 0; xIndex < verticesX; xIndex++)
            {
                var xAmount = xIndex / (double)cellsX;
                var localX = (xAmount - 0.5) * geometry.DimensionsMetres.X;
                var phaseX = localX * 0.23;
                var phaseZ = localZ * 0.31;
                var localY = amplitude * ((0.62 * Math.Sin(phaseX)) + (0.38 * Math.Cos(phaseZ)));
                var derivativeX = amplitude * 0.62 * 0.23 * Math.Cos(phaseX);
                var derivativeZ = -amplitude * 0.38 * 0.31 * Math.Sin(phaseZ);
                var localNormal = Normalize(new Vector3d(-derivativeX, 1, -derivativeZ));
                var position = TransformPoint(geometry.Transform, new(localX, localY, localZ));
                var normal = TransformNormal(geometry.Transform, localNormal);
                var offset = ((zIndex * verticesX) + xIndex) * ViewportMesh.FloatsPerVertex;
                vertices[offset] = checked((float)position.X);
                vertices[offset + 1] = checked((float)position.Y);
                vertices[offset + 2] = checked((float)position.Z);
                vertices[offset + 3] = checked((float)normal.X);
                vertices[offset + 4] = checked((float)normal.Y);
                vertices[offset + 5] = checked((float)normal.Z);
                minimum = new(
                    Math.Min(minimum.X, position.X),
                    Math.Min(minimum.Y, position.Y),
                    Math.Min(minimum.Z, position.Z));
                maximum = new(
                    Math.Max(maximum.X, position.X),
                    Math.Max(maximum.Y, position.Y),
                    Math.Max(maximum.Z, position.Z));
            }
        }

        var indexOffset = 0;
        for (var zIndex = 0; zIndex < cellsZ; zIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (var xIndex = 0; xIndex < cellsX; xIndex++)
            {
                var topLeft = checked((uint)((zIndex * verticesX) + xIndex));
                var topRight = topLeft + 1;
                var bottomLeft = checked((uint)(((zIndex + 1) * verticesX) + xIndex));
                var bottomRight = bottomLeft + 1;
                indices[indexOffset++] = topLeft;
                indices[indexOffset++] = bottomLeft;
                indices[indexOffset++] = topRight;
                indices[indexOffset++] = topRight;
                indices[indexOffset++] = bottomLeft;
                indices[indexOffset++] = bottomRight;
            }
        }

        return new(vertices, indices, minimum, maximum);
    }

    private static Vector3d TransformPoint(Transform transform, Vector3d point) =>
        Rotate(new(
            point.X * transform.Scale.X,
            point.Y * transform.Scale.Y,
            point.Z * transform.Scale.Z), transform.RotationDegrees) + transform.PositionMetres;

    private static Vector3d TransformNormal(Transform transform, Vector3d normal) => Normalize(Rotate(new(
        normal.X / transform.Scale.X,
        normal.Y / transform.Scale.Y,
        normal.Z / transform.Scale.Z), transform.RotationDegrees));

    private static Vector3d Rotate(Vector3d value, Vector3d rotationDegrees)
    {
        var radians = rotationDegrees.X * Math.PI / 180;
        var cosine = Math.Cos(radians);
        var sine = Math.Sin(radians);
        value = new(value.X, (value.Y * cosine) - (value.Z * sine), (value.Y * sine) + (value.Z * cosine));
        radians = rotationDegrees.Y * Math.PI / 180;
        cosine = Math.Cos(radians);
        sine = Math.Sin(radians);
        value = new((value.X * cosine) + (value.Z * sine), value.Y, (-value.X * sine) + (value.Z * cosine));
        radians = rotationDegrees.Z * Math.PI / 180;
        cosine = Math.Cos(radians);
        sine = Math.Sin(radians);
        return new((value.X * cosine) - (value.Y * sine), (value.X * sine) + (value.Y * cosine), value.Z);
    }

    private static Vector3d Normalize(Vector3d value)
    {
        var length = value.Length;
        return length > 1.0e-12 ? value * (1 / length) : new(0, 1, 0);
    }
}
