namespace TransactionalOutbox.Infrastructure.Outbox;

public sealed class OutboxMessage
{
    public Guid Id { get; init; }

    public required string EventType { get; init; }

    public int SchemaVersion { get; init; }

    public required string Payload { get; init; }

    public DateTimeOffset OccurredOnUtc { get; init; }

    public int AttemptCount { get; set; }

    public DateTimeOffset NextAttemptOnUtc { get; set; }

    public string? LockedBy { get; set; }

    public DateTimeOffset? LockedUntilUtc { get; set; }

    public DateTimeOffset? ProcessedOnUtc { get; set; }

    public string? EventBridgeEventId { get; set; }

    public string? LastError { get; set; }

    public DateTimeOffset? DeadLetteredOnUtc { get; set; }

    public byte[] RowVersion { get; private set; } = [];
}
