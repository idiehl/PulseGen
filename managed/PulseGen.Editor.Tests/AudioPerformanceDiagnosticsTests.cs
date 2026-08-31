using PulseGen.Engine.Client;
using Xunit;

namespace PulseGen.Editor.Tests;

public sealed class AudioPerformanceDiagnosticsTests
{
    [Fact]
    public void EmptyTelemetryIsReportedAsStarting()
    {
        var summary = AudioPerformanceDiagnostics.Summarize(Status(callbackCount: 0));

        Assert.Equal(AudioPerformanceSeverity.Starting, summary.Severity);
        Assert.Equal("AUDIO STARTING", summary.BadgeText);
    }

    [Theory]
    [InlineData(425_000, 0, (int)AudioPerformanceSeverity.Healthy, "CLEAN")]
    [InlineData(850_000, 0, (int)AudioPerformanceSeverity.Warning, "TIGHT")]
    [InlineData(1_125_000, 2, (int)AudioPerformanceSeverity.Critical, "2 MISSES")]
    public void PeakLoadAndDeadlineMissesMapToPlainLanguageHealth(
        uint loadPartsPerMillion,
        ulong deadlineMisses,
        int expectedSeverity,
        string expectedLabel)
    {
        var summary = AudioPerformanceDiagnostics.Summarize(Status(
            callbackCount: 4,
            totalNanoseconds: 8_000_000,
            maximumNanoseconds: 4_500_000,
            deadlineMisses: deadlineMisses,
            loadPartsPerMillion: loadPartsPerMillion));

        Assert.Equal((AudioPerformanceSeverity)expectedSeverity, summary.Severity);
        Assert.Contains(expectedLabel, summary.BadgeText, StringComparison.Ordinal);
        Assert.Contains("average 2.000 ms", summary.Detail, StringComparison.Ordinal);
        Assert.Contains("10.667 ms block budget", summary.Detail, StringComparison.Ordinal);
    }

    private static PreviewDeviceStatus Status(
        ulong callbackCount,
        ulong totalNanoseconds = 0,
        ulong maximumNanoseconds = 0,
        ulong deadlineMisses = 0,
        uint loadPartsPerMillion = 0) =>
        new(
            Running: true,
            Recoverable: true,
            SampleRate: 48000,
            PeriodFrames: 512,
            ReceiverIndex: 0,
            FramesProcessed: callbackCount * 512,
            CallbackCount: callbackCount,
            InterruptionCount: 0,
            RecoveryCount: 0,
            CallbackDurationNanosecondsTotal: totalNanoseconds,
            CallbackDurationNanosecondsMaximum: maximumNanoseconds,
            CallbackDeadlineMissCount: deadlineMisses,
            CallbackLoadPartsPerMillionMaximum: loadPartsPerMillion,
            CallbackFramesMaximum: 512);
}
