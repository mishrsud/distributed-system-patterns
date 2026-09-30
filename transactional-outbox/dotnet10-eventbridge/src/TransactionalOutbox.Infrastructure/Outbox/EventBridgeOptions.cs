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
}
