using PulseGen.Contracts;
using PulseGen.Engine.Client;
using System.Globalization;
using System.Text;

namespace PulseGen.Editor;

internal sealed record ViewportMesh(
    float[] InterleavedPositionNormals,
    uint[] Indices,
    Vector3d BoundsMinimum,
    Vector3d BoundsMaximum,
    int TerrainTriangleCount,
    int ImportedMeshCount,
    IReadOnlyList<ViewportMeshSection> Sections)
{
    public const int FloatsPerVertex = 6;

    public int VertexCount => InterleavedPositionNormals.Length / FloatsPerVertex;

    public int TriangleCount => Indices.Length / 3;

    public int ImportedTriangleCount => TriangleCount - TerrainTriangleCount;

    public static ViewportMesh Empty { get; } = new([], [], Vector3d.Zero, Vector3d.Zero, 0, 0, []);
}

internal readonly record struct ViewportMeshSection(
    Guid GeometryId,
    int FirstIndex,
    int IndexCount,
    Vector3d BoundsMinimum,
    Vector3d BoundsMaximum);

internal readonly record struct ViewportMeshBuildKey(string Signature);

internal static class ViewportMeshBuilder
{
    internal const int TerrainCellsPerMetre = 10;
    internal const int MaximumTerrainCellsPerAxis = 500;
    internal const int MaximumTrianglesPerSource = 250_000;
    internal const int MaximumViewportTriangles = 1_000_000;

    public static ViewportMeshBuildKey KeyFor(SceneDocument scene, string? projectDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(scene);
        var signature = new StringBuilder();
        signature.Append(projectDirectory is null ? "<local>" : Path.GetFullPath(projectDirectory));
        foreach (var geometry in scene.Geometry.Where(item => item.Kind is GeometryKind.Terrain or GeometryKind.ImportedMesh))
        {
            signature.Append('|').Append(geometry.Id.ToString("D", CultureInfo.InvariantCulture));
            signature.Append(':').Append((int)geometry.Kind);
            AppendVector(signature, geometry.Transform.PositionMetres);
            AppendVector(signature, geometry.Transform.RotationDegrees);
            AppendVector(signature, geometry.Transform.Scale);
            signature.Append(':').Append(geometry.IsDynamic ? '1' : '0');
            if (geometry.Kind == GeometryKind.Terrain)
            {
                AppendVector(signature, geometry.DimensionsMetres);
            }
            else
            {
                signature.Append(':').Append(geometry.MeshAssetHash);
                var asset = scene.Assets.FirstOrDefault(item =>
                    item.Kind == AssetKind.Mesh &&
                    string.Equals(item.Sha256, geometry.MeshAssetHash, StringComparison.OrdinalIgnoreCase));
                if (asset is not null)
                {
                    signature.Append(':').Append(asset.FileName);
                    signature.Append(':').Append(asset.SizeBytes.ToString(CultureInfo.InvariantCulture));
                }
            }
        }
        return new(signature.ToString());
    }

    public static ViewportMesh Build(
        SceneDocument scene,
        string? projectDirectory = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scene);
        cancellationToken.ThrowIfCancellationRequested();
        var parts = new List<ViewportMesh>();
        foreach (var terrain in scene.Geometry.Where(geometry => geometry.Kind == GeometryKind.Terrain))
        {
            cancellationToken.ThrowIfCancellationRequested();
            parts.Add(BuildTerrain(terrain, cancellationToken));
        }

        var importedIds = scene.Geometry
            .Where(geometry => geometry.Kind == GeometryKind.ImportedMesh)
            .Select(geometry => geometry.Id)
            .ToArray();
        if (importedIds.Length > 0)
        {
            if (projectDirectory is null)
            {
                throw new InvalidOperationException("Imported viewport meshes require their owning .pulseproj directory for asset verification.");
            }
            var importedMeshes = NativeSceneRenderer.DecodeImportedMeshes(scene, importedIds, projectDirectory);
            foreach (var imported in importedMeshes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                parts.Add(BuildImportedMesh(imported, cancellationToken));
            }
        }
        return Combine(parts, cancellationToken);
    }

    private static ViewportMesh BuildTerrain(AcousticGeometry geometry, CancellationToken cancellationToken)
    {
        var cellsX = Math.Clamp(
            checked((int)Math.Round(geometry.DimensionsMetres.X * TerrainCellsPerMetre, MidpointRounding.AwayFromZero)),
            1,
            MaximumTerrainCellsPerAxis);
        var cellsZ = Math.Clamp(
            checked((int)Math.Round(geometry.DimensionsMetres.Z * TerrainCellsPerMetre, MidpointRounding.AwayFromZero)),
            1,
            MaximumTerrainCellsPerAxis);
        if ((long)cellsX * cellsZ * 2 > MaximumTrianglesPerSource)
        {
            var scale = Math.Sqrt(MaximumTrianglesPerSource / (2.0 * cellsX * cellsZ));
            cellsX = Math.Max(1, (int)Math.Floor(cellsX * scale));
            cellsZ = Math.Max(1, (int)Math.Floor(cellsZ * scale));
        }

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
                var localNormal = Normalize(new(-derivativeX, 1, -derivativeZ));
                var position = TransformPoint(geometry.Transform, new(localX, localY, localZ));
                var normal = TransformNormal(geometry.Transform, localNormal);
                var offset = ((zIndex * verticesX) + xIndex) * ViewportMesh.FloatsPerVertex;
                WriteVertex(vertices, offset, position, normal);
                ExpandBounds(ref minimum, ref maximum, position);
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

        return new(
            vertices,
            indices,
            minimum,
            maximum,
            triangleCount,
            0,
            [new(geometry.Id, 0, indices.Length, minimum, maximum)]);
    }

    private static ViewportMesh BuildImportedMesh(
        ImportedMeshTriangleData imported,
        CancellationToken cancellationToken)
    {
        if (imported.TriangleVertices.Length == 0 || imported.TriangleVertices.Length % 3 != 0)
        {
            throw new InvalidDataException("Imported viewport triangle data must contain complete triangles.");
        }
        if (imported.TriangleCount > MaximumTrianglesPerSource)
        {
            throw new InvalidDataException("Imported viewport mesh exceeds the 250,000-triangle per-asset safety cap.");
        }

        var vertices = new float[checked(imported.TriangleVertices.Length * ViewportMesh.FloatsPerVertex)];
        var indices = new uint[imported.TriangleVertices.Length];
        for (var triangleIndex = 0; triangleIndex < imported.TriangleCount; triangleIndex++)
        {
            if ((triangleIndex & 2047) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }
            var sourceOffset = triangleIndex * 3;
            var a = imported.TriangleVertices[sourceOffset];
            var b = imported.TriangleVertices[sourceOffset + 1];
            var c = imported.TriangleVertices[sourceOffset + 2];
            var normal = Normalize(Cross(b - a, c - a));
            for (var vertexIndex = 0; vertexIndex < 3; vertexIndex++)
            {
                var flatIndex = sourceOffset + vertexIndex;
                WriteVertex(
                    vertices,
                    flatIndex * ViewportMesh.FloatsPerVertex,
                    imported.TriangleVertices[flatIndex],
                    normal);
                indices[flatIndex] = checked((uint)flatIndex);
            }
        }
        return new(
            vertices,
            indices,
            imported.BoundsMinimum,
            imported.BoundsMaximum,
            0,
            1,
            [new(imported.GeometryId, 0, indices.Length, imported.BoundsMinimum, imported.BoundsMaximum)]);
    }

    private static ViewportMesh Combine(IReadOnlyList<ViewportMesh> parts, CancellationToken cancellationToken)
    {
        var nonEmpty = parts.Where(part => part.TriangleCount > 0).ToArray();
        if (nonEmpty.Length == 0)
        {
            return ViewportMesh.Empty;
        }
        var triangleCount = nonEmpty.Sum(part => (long)part.TriangleCount);
        if (triangleCount > MaximumViewportTriangles)
        {
            throw new InvalidDataException("Viewport geometry exceeds the 1,000,000-triangle retained-rendering safety cap.");
        }
        if (nonEmpty.Length == 1)
        {
            return nonEmpty[0];
        }

        var vertexCount = checked(nonEmpty.Sum(part => part.VertexCount));
        var indexCount = checked(nonEmpty.Sum(part => part.Indices.Length));
        var vertices = new float[checked(vertexCount * ViewportMesh.FloatsPerVertex)];
        var indices = new uint[indexCount];
        var minimum = new Vector3d(double.PositiveInfinity, double.PositiveInfinity, double.PositiveInfinity);
        var maximum = new Vector3d(double.NegativeInfinity, double.NegativeInfinity, double.NegativeInfinity);
        var vertexOffset = 0;
        var floatOffset = 0;
        var indexOffset = 0;
        var terrainTriangles = 0;
        var importedMeshCount = 0;
        var sections = new List<ViewportMeshSection>(nonEmpty.Sum(part => part.Sections.Count));
        foreach (var part in nonEmpty)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var combinedFirstIndex = indexOffset;
            Array.Copy(part.InterleavedPositionNormals, 0, vertices, floatOffset, part.InterleavedPositionNormals.Length);
            for (var index = 0; index < part.Indices.Length; index++)
            {
                indices[indexOffset + index] = checked(part.Indices[index] + (uint)vertexOffset);
            }
            minimum = new(
                Math.Min(minimum.X, part.BoundsMinimum.X),
                Math.Min(minimum.Y, part.BoundsMinimum.Y),
                Math.Min(minimum.Z, part.BoundsMinimum.Z));
            maximum = new(
                Math.Max(maximum.X, part.BoundsMaximum.X),
                Math.Max(maximum.Y, part.BoundsMaximum.Y),
                Math.Max(maximum.Z, part.BoundsMaximum.Z));
            foreach (var section in part.Sections)
            {
                sections.Add(section with { FirstIndex = checked(section.FirstIndex + combinedFirstIndex) });
            }
            vertexOffset += part.VertexCount;
            floatOffset += part.InterleavedPositionNormals.Length;
            indexOffset += part.Indices.Length;
            terrainTriangles += part.TerrainTriangleCount;
            importedMeshCount += part.ImportedMeshCount;
        }
        return new(vertices, indices, minimum, maximum, terrainTriangles, importedMeshCount, sections);
    }

    private static void WriteVertex(float[] destination, int offset, Vector3d position, Vector3d normal)
    {
        destination[offset] = checked((float)position.X);
        destination[offset + 1] = checked((float)position.Y);
        destination[offset + 2] = checked((float)position.Z);
        destination[offset + 3] = checked((float)normal.X);
        destination[offset + 4] = checked((float)normal.Y);
        destination[offset + 5] = checked((float)normal.Z);
    }

    private static void ExpandBounds(ref Vector3d minimum, ref Vector3d maximum, Vector3d point)
    {
        minimum = new(
            Math.Min(minimum.X, point.X),
            Math.Min(minimum.Y, point.Y),
            Math.Min(minimum.Z, point.Z));
        maximum = new(
            Math.Max(maximum.X, point.X),
            Math.Max(maximum.Y, point.Y),
            Math.Max(maximum.Z, point.Z));
    }

    private static void AppendVector(StringBuilder signature, Vector3d vector)
    {
        AppendDouble(signature, vector.X);
        AppendDouble(signature, vector.Y);
        AppendDouble(signature, vector.Z);
    }

    private static void AppendDouble(StringBuilder signature, double value) =>
        signature.Append(':').Append(BitConverter.DoubleToInt64Bits(value).ToString("X16", CultureInfo.InvariantCulture));

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

    private static Vector3d Cross(Vector3d left, Vector3d right) => new(
        (left.Y * right.Z) - (left.Z * right.Y),
        (left.Z * right.X) - (left.X * right.Z),
        (left.X * right.Y) - (left.Y * right.X));

    private static Vector3d Normalize(Vector3d value)
    {
        var length = value.Length;
        return length > 1.0e-12 ? value * (1 / length) : new(0, 1, 0);
    }
}
