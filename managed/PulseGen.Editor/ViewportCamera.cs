using Avalonia;
using PulseGen.Contracts;
using System.Numerics;

namespace PulseGen.Editor;

internal readonly record struct ViewportRay(Vector3d Origin, Vector3d Direction);

internal readonly record struct ViewportBounds(Vector3d Minimum, Vector3d Maximum)
{
    public Vector3d Centre => (Minimum + Maximum) * 0.5;

    public double Radius => Math.Max(2, (Maximum - Minimum).Length * 0.5);

    public ViewportBounds Include(ViewportBounds other) => new(
        new(
            Math.Min(Minimum.X, other.Minimum.X),
            Math.Min(Minimum.Y, other.Minimum.Y),
            Math.Min(Minimum.Z, other.Minimum.Z)),
        new(
            Math.Max(Maximum.X, other.Maximum.X),
            Math.Max(Maximum.Y, other.Maximum.Y),
            Math.Max(Maximum.Z, other.Maximum.Z)));
}

internal sealed class ViewportCameraController
{
    private const double FieldOfViewRadians = Math.PI / 3.2;
    private const double OrbitDegreesPerPixel = 0.32;
    private const double MinimumPitchDegrees = -85;
    private const double MaximumPitchDegrees = 85;
    private const double MinimumRadiusMetres = 2;
    private const double FitMargin = 1.15;
    private ViewportBounds contentBounds = new(new(-10, -2, -10), new(10, 8, 10));
    private Vector3d target = new(0, 3, 0);
    private double yawDegrees = 38;
    private double pitchDegrees = 26;
    private double distanceMetres = 35;

    public event EventHandler? Changed;

    internal Vector3d Target => target;

    internal double YawDegrees => yawDegrees;

    internal double PitchDegrees => pitchDegrees;

    internal double DistanceMetres => distanceMetres;

    internal ViewportBounds ContentBounds => contentBounds;

    public void SetScene(SceneDocument scene, bool fit)
    {
        ArgumentNullException.ThrowIfNull(scene);
        contentBounds = ViewportSceneBounds.Calculate(scene);
        if (fit)
        {
            Fit();
        }
        else
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    public void ExtendContentBounds(Vector3d minimum, Vector3d maximum, bool fit)
    {
        if (!Finite(minimum) || !Finite(maximum))
        {
            return;
        }
        contentBounds = contentBounds.Include(new(minimum, maximum));
        if (fit)
        {
            Fit();
        }
    }

    public void Fit()
    {
        target = contentBounds.Centre;
        var radius = Math.Max(MinimumRadiusMetres, contentBounds.Radius);
        distanceMetres = radius / Math.Sin(FieldOfViewRadians * 0.5) * FitMargin;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Orbit(double deltaX, double deltaY)
    {
        if (!double.IsFinite(deltaX) || !double.IsFinite(deltaY))
        {
            return;
        }
        yawDegrees = WrapDegrees(yawDegrees - (deltaX * OrbitDegreesPerPixel));
        pitchDegrees = Math.Clamp(
            pitchDegrees - (deltaY * OrbitDegreesPerPixel),
            MinimumPitchDegrees,
            MaximumPitchDegrees);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Pan(double deltaX, double deltaY, Size viewportSize)
    {
        if (!ValidSize(viewportSize) || !double.IsFinite(deltaX) || !double.IsFinite(deltaY))
        {
            return;
        }
        var (_, forward, right, up) = CameraBasis();
        var metresPerPixel = 2 * distanceMetres * Math.Tan(FieldOfViewRadians * 0.5) / viewportSize.Height;
        target += (right * (-deltaX * metresPerPixel)) + (up * (deltaY * metresPerPixel));
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Zoom(double wheelDelta)
    {
        if (!double.IsFinite(wheelDelta) || Math.Abs(wheelDelta) < double.Epsilon)
        {
            return;
        }
        var radius = Math.Max(MinimumRadiusMetres, contentBounds.Radius);
        distanceMetres = Math.Clamp(
            distanceMetres * Math.Exp(-wheelDelta * 0.14),
            radius * 0.15,
            radius * 30);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public Matrix4x4 ViewProjection(Size viewportSize)
    {
        var aspectRatio = ValidSize(viewportSize)
            ? Math.Clamp((float)(viewportSize.Width / viewportSize.Height), 0.1F, 10F)
            : 1F;
        var (eye, _, _, _) = CameraBasis();
        var eyeVector = ToVector3(eye);
        var targetVector = ToVector3(target);
        var radius = Math.Max(MinimumRadiusMetres, contentBounds.Radius);
        var view = Matrix4x4.CreateLookAt(eyeVector, targetVector, Vector3.UnitY);
        var projection = Matrix4x4.CreatePerspectiveFieldOfView(
            (float)FieldOfViewRadians,
            aspectRatio,
            Math.Max(0.01F, (float)(radius * 0.005)),
            Math.Max(100F, (float)(distanceMetres + (radius * 8))));
        return view * projection;
    }

    public Point Project(Vector3d position, Size viewportSize)
    {
        if (!ValidSize(viewportSize))
        {
            return default;
        }
        var clip = Vector4.Transform(
            new Vector4((float)position.X, (float)position.Y, (float)position.Z, 1),
            ViewProjection(viewportSize));
        if (!float.IsFinite(clip.W) || Math.Abs(clip.W) < 1.0e-6F)
        {
            return new(-10_000, -10_000);
        }
        var normalizedX = Math.Clamp(clip.X / clip.W, -100F, 100F);
        var normalizedY = Math.Clamp(clip.Y / clip.W, -100F, 100F);
        return new(
            (normalizedX + 1) * viewportSize.Width * 0.5,
            (1 - normalizedY) * viewportSize.Height * 0.5);
    }

    public ViewportRay Ray(Point point, Size viewportSize)
    {
        if (!ValidSize(viewportSize))
        {
            return new(target, new(0, 0, -1));
        }
        var (eye, forward, right, up) = CameraBasis();
        var normalizedX = ((2 * point.X) / viewportSize.Width) - 1;
        var normalizedY = 1 - ((2 * point.Y) / viewportSize.Height);
        var tangent = Math.Tan(FieldOfViewRadians * 0.5);
        var aspectRatio = viewportSize.Width / viewportSize.Height;
        var direction = Normalize(
            forward +
            (right * (normalizedX * aspectRatio * tangent)) +
            (up * (normalizedY * tangent)));
        return new(eye, direction);
    }

    public Vector3d ProjectToHorizontalPlane(Point point, Size viewportSize, double yMetres)
    {
        var ray = Ray(point, viewportSize);
        if (Math.Abs(ray.Direction.Y) > 1.0e-9)
        {
            var distance = (yMetres - ray.Origin.Y) / ray.Direction.Y;
            if (distance >= 0 && double.IsFinite(distance))
            {
                return ray.Origin + (ray.Direction * distance);
            }
        }
        return new(target.X, yMetres, target.Z);
    }

    private (Vector3d Eye, Vector3d Forward, Vector3d Right, Vector3d Up) CameraBasis()
    {
        var yaw = yawDegrees * Math.PI / 180;
        var pitch = pitchDegrees * Math.PI / 180;
        var cosinePitch = Math.Cos(pitch);
        var offset = new Vector3d(
            distanceMetres * cosinePitch * Math.Sin(yaw),
            distanceMetres * Math.Sin(pitch),
            distanceMetres * cosinePitch * Math.Cos(yaw));
        var eye = target + offset;
        var forward = Normalize(target - eye);
        var right = Normalize(Cross(forward, new(0, 1, 0)));
        var up = Normalize(Cross(right, forward));
        return (eye, forward, right, up);
    }

    private static bool ValidSize(Size size) =>
        double.IsFinite(size.Width) && double.IsFinite(size.Height) && size.Width > 0 && size.Height > 0;

    private static bool Finite(Vector3d value) =>
        double.IsFinite(value.X) && double.IsFinite(value.Y) && double.IsFinite(value.Z);

    private static double WrapDegrees(double degrees)
    {
        var wrapped = degrees % 360;
        return wrapped < -180 ? wrapped + 360 : wrapped > 180 ? wrapped - 360 : wrapped;
    }

    private static Vector3 ToVector3(Vector3d value) => new((float)value.X, (float)value.Y, (float)value.Z);

    private static Vector3d Cross(Vector3d left, Vector3d right) => new(
        (left.Y * right.Z) - (left.Z * right.Y),
        (left.Z * right.X) - (left.X * right.Z),
        (left.X * right.Y) - (left.Y * right.X));

    private static Vector3d Normalize(Vector3d value)
    {
        var length = value.Length;
        return length > 1.0e-12 ? value * (1 / length) : new(0, 0, -1);
    }
}

internal static class ViewportSceneBounds
{
    public static ViewportBounds Calculate(SceneDocument scene)
    {
        ArgumentNullException.ThrowIfNull(scene);
        var minimum = new Vector3d(double.PositiveInfinity, double.PositiveInfinity, double.PositiveInfinity);
        var maximum = new Vector3d(double.NegativeInfinity, double.NegativeInfinity, double.NegativeInfinity);
        var hasPoint = false;

        void Include(Vector3d point)
        {
            if (!double.IsFinite(point.X) || !double.IsFinite(point.Y) || !double.IsFinite(point.Z))
            {
                return;
            }
            minimum = new(
                Math.Min(minimum.X, point.X),
                Math.Min(minimum.Y, point.Y),
                Math.Min(minimum.Z, point.Z));
            maximum = new(
                Math.Max(maximum.X, point.X),
                Math.Max(maximum.Y, point.Y),
                Math.Max(maximum.Z, point.Z));
            hasPoint = true;
        }

        foreach (var path in scene.MotionPaths)
        {
            foreach (var keyframe in path.Keyframes)
            {
                Include(keyframe.PositionMetres);
            }
        }
        foreach (var emitter in scene.Emitters)
        {
            Include(emitter.Transform.PositionMetres);
        }
        foreach (var receiver in scene.ReceiverRigs)
        {
            Include(receiver.Transform.PositionMetres);
        }
        foreach (var geometry in scene.Geometry)
        {
            IncludeTransformedBox(geometry.Transform, geometry.DimensionsMetres, Include);
        }
        foreach (var zone in scene.Atmosphere.WindZones)
        {
            IncludeTransformedBox(zone.Transform, zone.DimensionsMetres, Include);
        }
        foreach (var layer in scene.Atmosphere.Layers)
        {
            Include(new(-32, layer.BaseAltitudeMetres, -10));
            Include(new(32, layer.TopAltitudeMetres, 14));
        }

        if (!hasPoint)
        {
            return new(new(-10, -2, -10), new(10, 8, 10));
        }
        if ((maximum - minimum).Length < 1.0e-6)
        {
            minimum -= new Vector3d(2, 2, 2);
            maximum += new Vector3d(2, 2, 2);
        }
        return new(minimum, maximum);
    }

    private static void IncludeTransformedBox(Transform transform, Vector3d dimensions, Action<Vector3d> include)
    {
        var half = dimensions * 0.5;
        for (var x = -1; x <= 1; x += 2)
        {
            for (var y = -1; y <= 1; y += 2)
            {
                for (var z = -1; z <= 1; z += 2)
                {
                    var scaled = new Vector3d(
                        half.X * x * transform.Scale.X,
                        half.Y * y * transform.Scale.Y,
                        half.Z * z * transform.Scale.Z);
                    include(Rotate(scaled, transform.RotationDegrees) + transform.PositionMetres);
                }
            }
        }
    }

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
}
