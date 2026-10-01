using System.Text.Json;
using Microsoft.EntityFrameworkCore.Diagnostics;
using TransactionalOutbox.Core.Common;
using TransactionalOutbox.Core.Orders;
using TransactionalOutbox.Infrastructure.Outbox;

namespace TransactionalOutbox.Infrastructure.Persistence.Interceptors;

public sealed class ConvertDomainEventsToOutboxInterceptor : SaveChangesInterceptor
{
    private const string AsyncSaveRequiredMessage =
        "Use SaveChangesAsync so domain events are captured.";

    private static readonly JsonSerializerOptions SerializerOptions =
        new(JsonSerializerDefaults.Web);

    public override InterceptionResult<int> SavingChanges(
        DbContextEventData eventData,
        InterceptionResult<int> result)
    {
        throw new InvalidOperationException(AsyncSaveRequiredMessage);
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        if (eventData.Context is not { } context)
        {
            return ValueTask.FromResult(result);
        }

        var entries = context.ChangeTracker
            .Entries<Entity>()
            .Where(entry => entry.Entity.DomainEvents.Count > 0)
            .ToArray();
        var outboxMessages = entries
            .SelectMany(entry => entry.Entity.DomainEvents)
            .Select(ToOutboxMessage)
            .ToArray();

        context.Set<OutboxMessage>().AddRange(outboxMessages);

        foreach (var entry in entries)
        {
            entry.Entity.DequeueDomainEvents();
        }

        return ValueTask.FromResult(result);
    }

    private static OutboxMessage ToOutboxMessage(IDomainEvent domainEvent) =>
        domainEvent switch
        {
            OrderPlacedDomainEvent orderPlaced => new OutboxMessage
            {
                Id = orderPlaced.Id,
                EventType = "orders.order-placed",
                SchemaVersion = 1,
                Payload = JsonSerializer.Serialize(
                    new
                    {
                        eventId = orderPlaced.Id,
                        correlationId = orderPlaced.OrderId,
                        schemaVersion = 1,
                        occurredOnUtc = orderPlaced.OccurredOnUtc,
                        orderId = orderPlaced.OrderId,
                        customerId = orderPlaced.CustomerId,
                        totalAmount = orderPlaced.TotalAmount,
                        currency = orderPlaced.Currency
                    },
                    SerializerOptions),
                OccurredOnUtc = orderPlaced.OccurredOnUtc,
                NextAttemptOnUtc = orderPlaced.OccurredOnUtc
            },
            _ => throw new NotSupportedException(
                $"Domain event type '{domainEvent.GetType().Name}' is not supported.")
        };
}
