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

    /// <summary>
    /// Gives a claimed row back without attempting it: clears the lease and undoes the attempt the claim counted.
    /// </summary>
    Task<bool> ReleaseAsync(Guid id, string workerId, CancellationToken cancellationToken);

    Task<bool> DeadLetterAsync(Guid id, string workerId, string errorMessage, CancellationToken cancellationToken);

    Task<int> DeleteProcessedBeforeAsync(DateTimeOffset cutoff, CancellationToken cancellationToken);
}
