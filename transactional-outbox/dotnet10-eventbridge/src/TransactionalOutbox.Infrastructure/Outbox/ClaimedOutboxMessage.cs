namespace TransactionalOutbox.Infrastructure.Outbox;

public sealed record ClaimedOutboxMessage(
    Guid Id,
    string EventType,
    int SchemaVersion,
    string Payload,
    DateTimeOffset OccurredOnUtc,
    int AttemptCount);
