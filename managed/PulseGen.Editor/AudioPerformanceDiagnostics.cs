using PulseGen.Engine.Client;

namespace PulseGen.Editor;

internal enum AudioPerformanceSeverity
{
    Starting,
    Healthy,
    Warning,
    Critical
}

internal readonly record struct AudioPerformanceSummary(
    AudioPerformanceSeverity Severity,
    string BadgeText,
    string Detail);

internal static class AudioPerformanceDiagnostics
{
    private const double TightBudgetPercent = 80;

    public static AudioPerformanceSummary Summarize(PreviewDeviceStatus status)
    {
        if (status.CallbackCount == 0)
        {
            return new(
                AudioPerformanceSeverity.Starting,
                "AUDIO STARTING",
                $"Waiting for the first {status.PeriodFrames:N0}-frame audio block at {status.SampleRate:N0} Hz.");
        }

        var load = status.CallbackLoadPercentMaximum;
        var misses = status.CallbackDeadlineMissCount;
        var detail =
            $"Peak render {status.CallbackDurationMillisecondsMaximum:0.000} ms of the configured {status.CallbackBudgetMilliseconds:0.000} ms block budget ({load:0.0}%); " +
            $"average {status.CallbackDurationMillisecondsAverage:0.000} ms across {status.CallbackCount:N0} callbacks; " +
            $"{misses:N0} render deadline {(misses == 1 ? "miss" : "misses")}.";
        if (misses > 0)
        {
            return new(
                AudioPerformanceSeverity.Critical,
                $"AUDIO {load:0}% · {misses:N0} {(misses == 1 ? "MISS" : "MISSES")}",
                detail);
        }
        if (load >= TightBudgetPercent)
        {
            return new(AudioPerformanceSeverity.Warning, $"AUDIO {load:0}% · TIGHT", detail);
        }
        return new(AudioPerformanceSeverity.Healthy, $"AUDIO {load:0}% · CLEAN", detail);
    }
}
