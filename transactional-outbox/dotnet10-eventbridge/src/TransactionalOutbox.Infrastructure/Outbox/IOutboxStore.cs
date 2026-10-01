namespace TransactionalOutbox.Infrastructure.Outbox;

public interface IOutboxStore
{
    Task<IReadOnlyList<ClaimedOutboxMessage>> ClaimAsync(string workerId, int batchSize, TimeSpan leaseDuration, CancellationToken cancellationToken);

    Task<bool> MarkProcessedAsync(Guid id, string workerId, string eventBridgeEventId, CancellationToken cancellationToken);

    /// <summary>
    /// Releases the lease and makes the row eligible again <paramref name="delay"/> from now, measured with the
    /// database clock so application-host clock skew cannot shorten or lengthen the backoff.
    /// </summary>
    Task<bool> ScheduleRetryAsync(Guid id, string workerId, TimeSpan delay, string errorMessage, CancellationToken cancellationToken);

    Task<bool> DeadLetterAsync(Guid id, string workerId, string errorMessage, CancellationToken cancellationToken);

    Task<int> DeleteProcessedBeforeAsync(DateTimeOffset cutoff, CancellationToken cancellationToken);
}
