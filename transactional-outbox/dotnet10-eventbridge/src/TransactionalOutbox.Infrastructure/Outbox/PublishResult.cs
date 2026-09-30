namespace TransactionalOutbox.Infrastructure.Outbox;

public enum PublishOutcome
{
    Success,
    RetryableFailure,
    PermanentFailure,
}

public sealed record PublishResult(
    PublishOutcome Outcome,
    string? EventBridgeEventId,
    string? ErrorCode,
    string ErrorSummary)
{
    public static PublishResult Succeeded(string eventBridgeEventId) =>
        new(PublishOutcome.Success, eventBridgeEventId, null, string.Empty);

    public static PublishResult Retryable(string? errorCode, string errorSummary) =>
        new(PublishOutcome.RetryableFailure, null, errorCode, errorSummary);

    public static PublishResult Permanent(string? errorCode, string errorSummary) =>
        new(PublishOutcome.PermanentFailure, null, errorCode, errorSummary);
}
