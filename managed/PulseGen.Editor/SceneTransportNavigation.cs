namespace PulseGen.Editor;

internal static class SceneTransportNavigation
{
    internal static double Clamp(double time, double duration) =>
        double.IsFinite(time) && double.IsFinite(duration) && duration > 0
            ? Math.Clamp(time, 0, duration) : 0;

    internal static double PlayStart(double time, double duration)
    {
        var position = Clamp(time, duration);
        return position >= duration ? 0 : position;
    }

    internal static double Step(double time, double duration, double delta) =>
        Clamp(Clamp(time, duration) + (double.IsFinite(delta) ? delta : 0), duration);
}
