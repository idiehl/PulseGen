using Xunit;

namespace PulseGen.Editor.Tests;

public sealed class AudioDeviceRecoveryControllerTests
{
    [Fact]
    public void FailedAttemptsUseBoundedBackoffAndRemainExhaustedUntilCancelled()
    {
        var controller = new AudioDeviceRecoveryController(
        [
            TimeSpan.Zero,
            TimeSpan.FromMilliseconds(100),
            TimeSpan.FromMilliseconds(300)
        ]);
        var startedAt = new DateTimeOffset(2026, 8, 30, 12, 0, 0, TimeSpan.Zero);

        Assert.True(controller.Begin(startedAt));
        Assert.True(controller.IsAttemptDue(startedAt));
        Assert.Equal(1, controller.NextAttemptNumber);

        var first = controller.CompleteAttempt(recovered: false, startedAt);
        Assert.Equal(AudioDeviceRecoveryOutcome.Waiting, first.Outcome);
        Assert.Equal(startedAt + TimeSpan.FromMilliseconds(100), first.NextAttemptAtUtc);
        Assert.False(controller.IsAttemptDue(startedAt + TimeSpan.FromMilliseconds(99)));
        Assert.True(controller.IsAttemptDue(startedAt + TimeSpan.FromMilliseconds(100)));

        var secondAt = startedAt + TimeSpan.FromMilliseconds(100);
        var second = controller.CompleteAttempt(recovered: false, secondAt);
        Assert.Equal(secondAt + TimeSpan.FromMilliseconds(300), second.NextAttemptAtUtc);
        Assert.Equal(3, controller.NextAttemptNumber);

        var third = controller.CompleteAttempt(recovered: false, secondAt + TimeSpan.FromMilliseconds(300));
        Assert.Equal(AudioDeviceRecoveryOutcome.Exhausted, third.Outcome);
        Assert.True(controller.IsExhausted);
        Assert.False(controller.Begin(secondAt + TimeSpan.FromSeconds(10)));

        controller.Cancel();
        Assert.False(controller.IsExhausted);
        Assert.True(controller.Begin(secondAt + TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public void SuccessfulAttemptAllowsAFutureIndependentRecoveryCycle()
    {
        var controller = new AudioDeviceRecoveryController([TimeSpan.Zero, TimeSpan.FromSeconds(1)]);
        var now = DateTimeOffset.UtcNow;

        controller.Begin(now);
        var result = controller.CompleteAttempt(recovered: true, now);

        Assert.Equal(AudioDeviceRecoveryOutcome.Recovered, result.Outcome);
        Assert.Equal(1, result.AttemptsCompleted);
        Assert.False(controller.IsActive);
        Assert.False(controller.IsExhausted);
        Assert.True(controller.Begin(now + TimeSpan.FromSeconds(5)));
        Assert.Equal(0, controller.AttemptsCompleted);
    }

    [Fact]
    public void RecoveryScheduleRejectsEmptyOrNegativeDelays()
    {
        Assert.Throws<ArgumentException>(() => new AudioDeviceRecoveryController([]));
        Assert.Throws<ArgumentException>(() => new AudioDeviceRecoveryController([TimeSpan.FromMilliseconds(-1)]));
    }
}
