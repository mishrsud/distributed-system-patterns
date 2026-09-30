using TransactionalOutbox.Core.Orders;

namespace TransactionalOutbox.Web.Contracts;

public sealed record OrderResponse(
    Guid Id,
    Guid CustomerId,
    decimal TotalAmount,
    string Currency,
    string Status,
    DateTimeOffset CreatedOnUtc)
{
    public static OrderResponse From(Order order)
    {
        ArgumentNullException.ThrowIfNull(order);

        return new OrderResponse(
            order.Id,
            order.CustomerId,
            order.TotalAmount,
            order.Currency,
            order.Status.ToString(),
            order.CreatedOnUtc);
    }
}
