using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace TransactionalOutbox.Infrastructure.Outbox;

public sealed partial class OutboxProcessor(
    IOutboxStore store,
    IEventPublisher publisher,
    RetrySchedule retrySchedule,
    IOptions<OutboxOptions> options,
    ILogger<OutboxProcessor> logger)
{
    private readonly OutboxOptions _options = options.Value;

    /// <summary>
    /// Claims one batch and publishes each message, recording the outcome. Returns the number claimed.
    /// </summary>
    public async Task<int> ProcessBatchAsync(string workerId, CancellationToken cancellationToken)
    {
        IReadOnlyList<ClaimedOutboxMessage> batch = await store.ClaimAsync(
            workerId, _options.BatchSize, _options.LeaseDuration, cancellationToken);

        foreach (ClaimedOutboxMessage message in batch)
        {
            PublishResult result = await PublishAsync(message, cancellationToken);
            bool transitioned = await RecordOutcomeAsync(message, workerId, result, cancellationToken);
            if (!transitioned)
            {
                LogLeaseLost(logger, message.Id, workerId);
            }
        }

        return batch.Count;
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
            return await store.MarkProcessedAsync(message.Id, workerId, result.EventBridgeEventId!, cancellationToken);
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

    [LoggerMessage(Level = LogLevel.Warning, Message = "Publishing outbox message {MessageId} threw; treating as retryable.")]
    private static partial void LogPublishException(ILogger logger, Exception exception, Guid messageId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Outbox message {MessageId} dead-lettered after attempt {AttemptCount}: {ErrorSummary}")]
    private static partial void LogDeadLettered(ILogger logger, Guid messageId, int attemptCount, string errorSummary);
}
