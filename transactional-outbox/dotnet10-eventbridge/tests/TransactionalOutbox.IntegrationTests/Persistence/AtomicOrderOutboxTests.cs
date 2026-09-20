using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using TransactionalOutbox.Core.Orders;

namespace TransactionalOutbox.IntegrationTests.Persistence;

[Collection(InfrastructureTestGroup.Name)]
public sealed class AtomicOrderOutboxTests(InfrastructureFixture fixture)
{
    [Fact]
    public async Task SaveChangesAsyncPersistsOrderAndCompleteOutboxEnvelopeAtomically()
    {
        await fixture.RecreateDatabaseAsync();
        var orderId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var customerId = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var occurredOnUtc = new DateTimeOffset(2026, 9, 20, 10, 30, 0, TimeSpan.Zero);
        var order = Order.Place(orderId, customerId, 125.50m, "AUD", occurredOnUtc);
        var domainEvent = Assert.IsType<OrderPlacedDomainEvent>(Assert.Single(order.DomainEvents));

        await using (var context = fixture.CreateContext())
        {
            context.Orders.Add(order);
            await context.SaveChangesAsync(CancellationToken.None);
        }

        await using var verificationContext = fixture.CreateContext();
        var savedOrder = Assert.Single(await verificationContext.Orders
            .AsNoTracking()
            .ToListAsync(CancellationToken.None));
        var outboxMessage = Assert.Single(await verificationContext.OutboxMessages
            .AsNoTracking()
            .ToListAsync(CancellationToken.None));

        Assert.Equal(orderId, savedOrder.Id);
        Assert.Equal(domainEvent.Id, outboxMessage.Id);
        Assert.Equal("orders.order-placed", outboxMessage.EventType);
        Assert.Equal(1, outboxMessage.SchemaVersion);
        Assert.Equal(occurredOnUtc, outboxMessage.OccurredOnUtc);
        Assert.Equal(occurredOnUtc, outboxMessage.NextAttemptOnUtc);
        Assert.Equal(0, outboxMessage.AttemptCount);

        using var payload = JsonDocument.Parse(outboxMessage.Payload);
        var root = payload.RootElement;
        Assert.Equal(8, root.EnumerateObject().Count());
        Assert.Equal(domainEvent.Id, root.GetProperty("eventId").GetGuid());
        Assert.Equal(orderId, root.GetProperty("correlationId").GetGuid());
        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(occurredOnUtc, root.GetProperty("occurredOnUtc").GetDateTimeOffset());
        Assert.Equal(orderId, root.GetProperty("orderId").GetGuid());
        Assert.Equal(customerId, root.GetProperty("customerId").GetGuid());
        Assert.Equal(125.50m, root.GetProperty("totalAmount").GetDecimal());
        Assert.Equal("AUD", root.GetProperty("currency").GetString());
    }

    [Fact]
    public async Task SaveFailureAfterConversionPersistsNeitherOrderNorOutboxMessage()
    {
        await fixture.RecreateDatabaseAsync();
        var interceptor = new ThrowAfterConversionInterceptor(throwOnlyOnce: false);

        await using (var context = fixture.CreateContext(interceptor))
        {
            context.Orders.Add(CreateOrder());

            await Assert.ThrowsAsync<TestSaveException>(
                () => context.SaveChangesAsync(CancellationToken.None));
        }

        await using var verificationContext = fixture.CreateContext();
        Assert.Equal(0, await verificationContext.Orders.CountAsync(CancellationToken.None));
        Assert.Equal(0, await verificationContext.OutboxMessages.CountAsync(CancellationToken.None));
    }

    [Fact]
    public async Task RetryingFailedSaveOnSameContextPersistsOneOrderAndOneOutboxMessage()
    {
        await fixture.RecreateDatabaseAsync();
        var interceptor = new ThrowAfterConversionInterceptor(throwOnlyOnce: true);

        await using (var context = fixture.CreateContext(interceptor))
        {
            context.Orders.Add(CreateOrder());

            await Assert.ThrowsAsync<TestSaveException>(
                () => context.SaveChangesAsync(CancellationToken.None));
            await context.SaveChangesAsync(CancellationToken.None);
        }

        await using var verificationContext = fixture.CreateContext();
        Assert.Equal(1, await verificationContext.Orders.CountAsync(CancellationToken.None));
        Assert.Equal(1, await verificationContext.OutboxMessages.CountAsync(CancellationToken.None));
    }

    [Fact]
    public async Task SynchronousSaveChangesIsRejected()
    {
        await fixture.RecreateDatabaseAsync();

        await using var context = fixture.CreateContext();
        context.Orders.Add(CreateOrder());

        var exception = Assert.Throws<InvalidOperationException>(() => context.SaveChanges());

        Assert.Equal(
            "Use SaveChangesAsync so domain events are captured.",
            exception.Message);
    }

    private static Order CreateOrder() => Order.Place(
        Guid.Parse("33333333-3333-3333-3333-333333333333"),
        Guid.Parse("44444444-4444-4444-4444-444444444444"),
        42.25m,
        "AUD",
        new DateTimeOffset(2026, 9, 20, 11, 0, 0, TimeSpan.Zero));

    private sealed class ThrowAfterConversionInterceptor(bool throwOnlyOnce)
        : SaveChangesInterceptor
    {
        private bool _hasThrown;

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (!throwOnlyOnce || !_hasThrown)
            {
                _hasThrown = true;
                throw new TestSaveException();
            }

            return ValueTask.FromResult(result);
        }
    }

    private sealed class TestSaveException : Exception;
}
