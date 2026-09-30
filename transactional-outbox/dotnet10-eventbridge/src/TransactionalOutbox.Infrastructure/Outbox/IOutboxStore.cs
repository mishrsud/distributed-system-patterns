namespace TransactionalOutbox.Infrastructure.Outbox;

public interface IOutboxStore
{
    Task<IReadOnlyList<ClaimedOutboxMessage>> ClaimAsync(string workerId, int batchSize, TimeSpan leaseDuration, CancellationToken cancellationToken);

    Task<bool> MarkProcessedAsync(Guid id, string workerId, string eventBridgeEventId, CancellationToken cancellationToken);

    Task<bool> ScheduleRetryAsync(Guid id, string workerId, DateTimeOffset nextAttemptOnUtc, string errorMessage, CancellationToken cancellationToken);

    Task<bool> DeadLetterAsync(Guid id, string workerId, string errorMessage, CancellationToken cancellationToken);

    Task<int> DeleteProcessedBeforeAsync(DateTimeOffset cutoff, CancellationToken cancellationToken);
}
