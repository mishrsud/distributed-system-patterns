namespace TransactionalOutbox.Infrastructure.Outbox;

public sealed class OutboxOptions
{
    public bool Enabled { get; init; } = true;

    public int BatchSize { get; init; } = 20;

    public int MaxAttempts { get; init; } = 10;

    public TimeSpan LeaseDuration { get; init; } = TimeSpan.FromSeconds(30);

    public TimeSpan IdleDelay { get; init; } = TimeSpan.FromSeconds(2);

    public TimeSpan MaxRetryDelay { get; init; } = TimeSpan.FromMinutes(5);

    public TimeSpan ProcessedRetention { get; init; } = TimeSpan.FromDays(7);

    public TimeSpan CleanupInterval { get; init; } = TimeSpan.FromHours(1);
}
