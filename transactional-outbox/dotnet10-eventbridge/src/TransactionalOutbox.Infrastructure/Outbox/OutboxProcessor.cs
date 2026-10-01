using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace TransactionalOutbox.Infrastructure.Outbox;

public sealed partial class OutboxProcessor(
    IOutboxStore store,
    IEventPublisher publisher,
    RetrySchedule retrySchedule,
    IOptions<OutboxOptions> options,
    IOptions<EventBridgeOptions> eventBridgeOptions,
    TimeProvider timeProvider,
    ILogger<OutboxProcessor> logger)
{
    private static readonly TimeSpan SuccessRecordTimeout = TimeSpan.FromSeconds(5);

    private readonly OutboxOptions _options = options.Value;
    private readonly TimeSpan _maxPublishDuration = eventBridgeOptions.Value.MaxPublishDuration;

    /// <summary>
    /// Claims one batch and publishes each message, recording the outcome. Returns the number claimed.
    /// </summary>
    public async Task<int> ProcessBatchAsync(string workerId, CancellationToken cancellationToken)
    {
        IReadOnlyList<ClaimedOutboxMessage> batch = await store.ClaimAsync(
            workerId, _options.BatchSize, _options.LeaseDuration, cancellationToken);
        long claimedAt = timeProvider.GetTimestamp();

        for (int index = 0; index < batch.Count; index++)
        {
            // Publishes run one at a time under a lease taken for the whole batch. Stop before the
            // remaining lease is shorter than one publish budget, so a slow EventBridge cannot leave
            // later rows publishing after their lease expired, while another worker reclaims them.
            if (timeProvider.GetElapsedTime(claimedAt) + _maxPublishDuration >= _options.LeaseDuration)
            {
                await ReleaseRemainingAsync(batch, index, workerId, cancellationToken);
                break;
            }

            ClaimedOutboxMessage message = batch[index];
            PublishResult result = await PublishAsync(message, cancellationToken);
            bool transitioned = await RecordOutcomeAsync(message, workerId, result, cancellationToken);
            if (!transitioned)
            {
                LogLeaseLost(logger, message.Id, workerId);
            }
        }

        return batch.Count;
    }

    private async Task ReleaseRemainingAsync(
        IReadOnlyList<ClaimedOutboxMessage> batch,
        int firstUnpublished,
        string workerId,
        CancellationToken cancellationToken)
    {
        LogBatchStopped(logger, batch.Count - firstUnpublished, workerId);
        for (int index = firstUnpublished; index < batch.Count; index++)
        {
            Guid id = batch[index].Id;
            if (!await store.ReleaseAsync(id, workerId, cancellationToken))
            {
                LogLeaseLost(logger, id, workerId);
            }
        }
    }

    private async Task<PublishResult> PublishAsync(ClaimedOutboxMessage message, CancellationToken cancellationToken)
    {
        try
        {
            return await publisher.PublishAsync(message, cancellationToken);
        }
        catch (Exception ex) when (!(ex is OperationCanceledException && cancellationToken.IsCancellationRequested))
        {
            LogPublishException(logger, ex, message.Id);
            return PublishResult.Retryable(ex.GetType().Name, $"{ex.GetType().FullName}: {ex.Message}");
        }
    }

    private async Task<bool> RecordOutcomeAsync(
        ClaimedOutboxMessage message,
        string workerId,
        PublishResult result,
        CancellationToken cancellationToken)
    {
        if (result.Outcome == PublishOutcome.Success)
        {
            // EventBridge has accepted the event. Record that even if shutdown began during the publish:
            // abandoning the update now would only guarantee a duplicate after the lease expires. A short
            // timeout of its own (not linked to shutdown) still keeps a hung database from blocking stop.
            using var recordTimeout = new CancellationTokenSource(SuccessRecordTimeout);
            return await store.MarkProcessedAsync(message.Id, workerId, result.EventBridgeEventId!, recordTimeout.Token);
        }

        if (result.Outcome == PublishOutcome.PermanentFailure || message.AttemptCount >= _options.MaxAttempts)
        {
            bool deadLettered = await store.DeadLetterAsync(message.Id, workerId, result.ErrorSummary, cancellationToken);
            if (deadLettered)
            {
                LogDeadLettered(logger, message.Id, message.AttemptCount, result.ErrorSummary);
            }

            return deadLettered;
        }

        TimeSpan delay = retrySchedule.GetDelay(message.AttemptCount, Random.Shared.NextDouble());
        return await store.ScheduleRetryAsync(message.Id, workerId, delay, result.ErrorSummary, cancellationToken);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Outbox message {MessageId} transition skipped: lease no longer owned by worker {WorkerId}.")]
    private static partial void LogLeaseLost(ILogger logger, Guid messageId, string workerId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Outbox batch stopped early: released {ReleasedCount} unpublished message(s) claimed by worker {WorkerId} because the remaining lease is shorter than one publish budget.")]
    private static partial void LogBatchStopped(ILogger logger, int releasedCount, string workerId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Publishing outbox message {MessageId} threw; treating as retryable.")]
    private static partial void LogPublishException(ILogger logger, Exception exception, Guid messageId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Outbox message {MessageId} dead-lettered after attempt {AttemptCount}: {ErrorSummary}")]
    private static partial void LogDeadLettered(ILogger logger, Guid messageId, int attemptCount, string errorSummary);
}
