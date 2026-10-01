using TransactionalOutbox.Core.Common;
using TransactionalOutbox.Core.Orders;

namespace TransactionalOutbox.UnitTests.Core;

public sealed class OrderTests
{
    [Fact]
    public void PlaceCreatesPlacedOrderAndOneOrderPlacedEvent()
    {
        var orderId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var customerId = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var occurredOnUtc = new DateTimeOffset(2026, 9, 20, 10, 30, 0, TimeSpan.Zero);

        var order = Order.Place(orderId, customerId, 125.50m, "AUD", occurredOnUtc);

        Assert.Equal(orderId, order.Id);
        Assert.Equal(customerId, order.CustomerId);
        Assert.Equal(125.50m, order.TotalAmount);
        Assert.Equal("AUD", order.Currency);
        Assert.Equal(OrderStatus.Placed, order.Status);
        Assert.Equal(occurredOnUtc, order.CreatedOnUtc);

        var events = order.DequeueDomainEvents();
        var domainEvent = Assert.Single(events);
        var orderPlaced = Assert.IsType<OrderPlacedDomainEvent>(domainEvent);

        Assert.NotEqual(Guid.Empty, orderPlaced.Id);
        Assert.Equal(occurredOnUtc, orderPlaced.OccurredOnUtc);
        Assert.Equal(orderId, orderPlaced.OrderId);
        Assert.Equal(customerId, orderPlaced.CustomerId);
        Assert.Equal(125.50m, orderPlaced.TotalAmount);
        Assert.Equal("AUD", orderPlaced.Currency);
    }

    [Fact]
    public void PlaceRejectsAnEmptyOrderId()
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            Order.Place(Guid.Empty, Guid.NewGuid(), 125.50m, "AUD", UtcTime));

        Assert.Equal("orderId", exception.ParamName);
    }

    [Fact]
    public void PlaceRejectsAnEmptyCustomerId()
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            Order.Place(Guid.NewGuid(), Guid.Empty, 125.50m, "AUD", UtcTime));

        Assert.Equal("customerId", exception.ParamName);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void PlaceRejectsANonPositiveTotal(decimal totalAmount)
    {
        var exception = Assert.Throws<ArgumentOutOfRangeException>(() =>
            Order.Place(Guid.NewGuid(), Guid.NewGuid(), totalAmount, "AUD", UtcTime));

        Assert.Equal("totalAmount", exception.ParamName);
    }

    [Theory]
    [InlineData("")]
    [InlineData("AU")]
    [InlineData("AUDD")]
    [InlineData("aud")]
    [InlineData("AuD")]
    [InlineData("A1D")]
    [InlineData("AÜD")]
    public void PlaceRejectsCurrencyThatIsNotThreeUppercaseAsciiLetters(string currency)
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            Order.Place(Guid.NewGuid(), Guid.NewGuid(), 125.50m, currency, UtcTime));

        Assert.Equal("currency", exception.ParamName);
    }

    [Fact]
    public void PlaceRejectsANonUtcTimestamp()
    {
        var nonUtcTime = new DateTimeOffset(2026, 9, 20, 10, 30, 0, TimeSpan.FromHours(10));

        var exception = Assert.Throws<ArgumentException>(() =>
            Order.Place(Guid.NewGuid(), Guid.NewGuid(), 125.50m, "AUD", nonUtcTime));

        Assert.Equal("occurredOnUtc", exception.ParamName);
    }

    [Fact]
    public void DequeueDomainEventsPreservesInsertionOrderAndClearsEvents()
    {
        var entity = new TestEntity();
        var first = new TestDomainEvent(Guid.NewGuid(), UtcTime);
        var second = new TestDomainEvent(Guid.NewGuid(), UtcTime.AddMinutes(1));

        entity.Raise(first);
        entity.Raise(second);

        Assert.Equal(new IDomainEvent[] { first, second }, entity.DomainEvents);
        Assert.Equal(new IDomainEvent[] { first, second }, entity.DequeueDomainEvents());
        Assert.Empty(entity.DomainEvents);
        Assert.Empty(entity.DequeueDomainEvents());
    }

    private static DateTimeOffset UtcTime =>
        new(2026, 9, 20, 10, 30, 0, TimeSpan.Zero);

    private sealed class TestEntity : Entity
    {
        public void Raise(IDomainEvent domainEvent) => AddDomainEvent(domainEvent);
    }

    private sealed record TestDomainEvent(Guid Id, DateTimeOffset OccurredOnUtc) : IDomainEvent;
}
