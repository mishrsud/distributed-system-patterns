using TransactionalOutbox.Core.Common;

namespace TransactionalOutbox.Core.Orders;

public enum OrderStatus
{
    Placed
}

public sealed class Order : Entity
{
    private Order()
    {
        Currency = string.Empty;
    }

    private Order(
        Guid id,
        Guid customerId,
        decimal totalAmount,
        string currency,
        DateTimeOffset createdOnUtc)
    {
        Id = id;
        CustomerId = customerId;
        TotalAmount = totalAmount;
        Currency = currency;
        Status = OrderStatus.Placed;
        CreatedOnUtc = createdOnUtc;
    }

    public Guid Id { get; private set; }

    public Guid CustomerId { get; private set; }

    public decimal TotalAmount { get; private set; }

    public string Currency { get; private set; }

    public OrderStatus Status { get; private set; }

    public DateTimeOffset CreatedOnUtc { get; private set; }

    public static Order Place(
        Guid orderId,
        Guid customerId,
        decimal totalAmount,
        string currency,
        DateTimeOffset occurredOnUtc)
    {
        Validate(orderId, customerId, totalAmount, currency, occurredOnUtc);

        var order = new Order(orderId, customerId, totalAmount, currency, occurredOnUtc);
        order.AddDomainEvent(new OrderPlacedDomainEvent(
            Guid.NewGuid(),
            occurredOnUtc,
            orderId,
            customerId,
            totalAmount,
            currency));

        return order;
    }

    private static void Validate(
        Guid orderId,
        Guid customerId,
        decimal totalAmount,
        string currency,
        DateTimeOffset occurredOnUtc)
    {
        if (orderId == Guid.Empty)
        {
            throw new ArgumentException("Order ID must not be empty.", nameof(orderId));
        }

        if (customerId == Guid.Empty)
        {
            throw new ArgumentException("Customer ID must not be empty.", nameof(customerId));
        }

        if (totalAmount <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(totalAmount),
                totalAmount,
                "Total amount must be greater than zero.");
        }

        if (!IsCurrencyCode(currency))
        {
            throw new ArgumentException(
                "Currency must be exactly three uppercase ASCII letters.",
                nameof(currency));
        }

        if (occurredOnUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException(
                "Occurrence time must use the UTC offset of zero.",
                nameof(occurredOnUtc));
        }
    }

    private static bool IsCurrencyCode(string? currency)
    {
        if (currency is null || currency.Length != 3)
        {
            return false;
        }

        return currency.All(static character => character is >= 'A' and <= 'Z');
    }
}
