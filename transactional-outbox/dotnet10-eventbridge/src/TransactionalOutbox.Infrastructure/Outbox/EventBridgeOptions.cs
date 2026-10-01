using System.ComponentModel.DataAnnotations;

namespace TransactionalOutbox.Infrastructure.Outbox;

public sealed class EventBridgeOptions
{
    [Required]
    public string EventBusName { get; init; } = "orders";

    [Required]
    public string Source { get; init; } = "sample.orders";

    [Required]
    public string DetailType { get; init; } = "OrderPlaced";

    public string? ServiceUrl { get; init; }

    public string? AuthenticationRegion { get; init; }

    /// <summary>Per-request timeout for the EventBridge SDK client.</summary>
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Immediate retries the SDK makes after the first request fails.</summary>
    public int MaxErrorRetry { get; init; } = 2;

    /// <summary>
    /// The longest one publish is expected to take: every SDK attempt running to its timeout. The SDK's
    /// backoff sleeps between retries are not included, so treat this as approximate.
    /// </summary>
    public TimeSpan MaxPublishDuration => RequestTimeout * (MaxErrorRetry + 1);
}
