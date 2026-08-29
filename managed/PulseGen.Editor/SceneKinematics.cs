using PulseGen.Contracts;

namespace PulseGen.Editor;

internal static class SceneKinematics
{
    public static Vector3d PositionAt(MotionPath path, double timeSeconds)
    {
        if (path.Keyframes.Count == 0)
        {
            return Vector3d.Zero;
        }

        if (timeSeconds <= path.Keyframes[0].TimeSeconds)
        {
            return path.Keyframes[0].PositionMetres;
        }

        for (var index = 1; index < path.Keyframes.Count; index++)
        {
            var right = path.Keyframes[index];
            if (timeSeconds <= right.TimeSeconds)
            {
                var left = path.Keyframes[index - 1];
                var span = right.TimeSeconds - left.TimeSeconds;
                var amount = span <= 0 ? 0 : (timeSeconds - left.TimeSeconds) / span;
                return left.PositionMetres + ((right.PositionMetres - left.PositionMetres) * amount);
            }
        }

        return path.Keyframes[^1].PositionMetres;
    }

    public static Vector3d VelocityAt(MotionPath path, double timeSeconds)
    {
        const double sampleDelta = 0.002;
        var leftTime = Math.Max(path.Keyframes.FirstOrDefault()?.TimeSeconds ?? 0, timeSeconds - sampleDelta);
        var rightTime = Math.Min(path.Keyframes.LastOrDefault()?.TimeSeconds ?? timeSeconds, timeSeconds + sampleDelta);
        var elapsed = rightTime - leftTime;
        return elapsed <= 0 ? Vector3d.Zero : (PositionAt(path, rightTime) - PositionAt(path, leftTime)) * (1 / elapsed);
    }
}
