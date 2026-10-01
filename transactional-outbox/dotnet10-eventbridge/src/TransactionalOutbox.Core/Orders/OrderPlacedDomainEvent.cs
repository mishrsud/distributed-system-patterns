using TransactionalOutbox.Core.Common;

namespace TransactionalOutbox.Core.Orders;

public sealed record OrderPlacedDomainEvent(
    Guid Id,
    DateTimeOffset OccurredOnUtc,
    Guid OrderId,
    Guid CustomerId,
    decimal TotalAmount,
    string Currency) : IDomainEvent;
