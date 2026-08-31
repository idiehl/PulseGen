namespace PulseGen.Editor;

internal enum AudioDeviceRecoveryOutcome
{
    Waiting,
    Recovered,
    Exhausted
}

internal readonly record struct AudioDeviceRecoveryResult(
    AudioDeviceRecoveryOutcome Outcome,
    int AttemptsCompleted,
    int MaximumAttempts,
    DateTimeOffset? NextAttemptAtUtc);

internal sealed class AudioDeviceRecoveryController
{
    private static readonly TimeSpan[] DefaultAttemptDelays =
    [
        TimeSpan.Zero,
        TimeSpan.FromMilliseconds(250),
        TimeSpan.FromMilliseconds(500),
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(2)
    ];

    private readonly TimeSpan[] attemptDelays;
    private DateTimeOffset? nextAttemptAtUtc;

    public AudioDeviceRecoveryController(IEnumerable<TimeSpan>? attemptDelays = null)
    {
        this.attemptDelays = (attemptDelays ?? DefaultAttemptDelays).ToArray();
        if (this.attemptDelays.Length == 0 || this.attemptDelays.Any(delay => delay < TimeSpan.Zero))
        {
            throw new ArgumentException("Audio-device recovery requires one or more non-negative attempt delays.", nameof(attemptDelays));
        }
    }

    public bool IsActive { get; private set; }

    public bool IsExhausted { get; private set; }

    public int AttemptsCompleted { get; private set; }

    public int MaximumAttempts => attemptDelays.Length;

    public int NextAttemptNumber => Math.Min(AttemptsCompleted + 1, MaximumAttempts);

    public DateTimeOffset? NextAttemptAtUtc => nextAttemptAtUtc;

    public bool Begin(DateTimeOffset now)
    {
        if (IsActive || IsExhausted)
        {
            return false;
        }
        AttemptsCompleted = 0;
        IsActive = true;
        nextAttemptAtUtc = now + attemptDelays[0];
        return true;
    }

    public bool IsAttemptDue(DateTimeOffset now) =>
        IsActive && nextAttemptAtUtc is { } dueAt && now >= dueAt;

    public AudioDeviceRecoveryResult CompleteAttempt(bool recovered, DateTimeOffset now)
    {
        if (!IsActive)
        {
            throw new InvalidOperationException("Audio-device recovery is not active.");
        }

        AttemptsCompleted++;
        if (recovered)
        {
            IsActive = false;
            nextAttemptAtUtc = null;
            return new(AudioDeviceRecoveryOutcome.Recovered, AttemptsCompleted, MaximumAttempts, null);
        }
        if (AttemptsCompleted >= MaximumAttempts)
        {
            IsActive = false;
            IsExhausted = true;
            nextAttemptAtUtc = null;
            return new(AudioDeviceRecoveryOutcome.Exhausted, AttemptsCompleted, MaximumAttempts, null);
        }

        nextAttemptAtUtc = now + attemptDelays[AttemptsCompleted];
        return new(AudioDeviceRecoveryOutcome.Waiting, AttemptsCompleted, MaximumAttempts, nextAttemptAtUtc);
    }

    public void Cancel()
    {
        IsActive = false;
        IsExhausted = false;
        AttemptsCompleted = 0;
        nextAttemptAtUtc = null;
    }
}
