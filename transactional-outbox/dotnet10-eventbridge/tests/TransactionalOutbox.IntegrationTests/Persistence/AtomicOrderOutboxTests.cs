using System.Data.Common;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using TransactionalOutbox.Core.Common;
using TransactionalOutbox.Core.Orders;
using TransactionalOutbox.Infrastructure.Outbox;
using TransactionalOutbox.Infrastructure.Persistence;
using TransactionalOutbox.Infrastructure.Persistence.Interceptors;

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
    public async Task SaveFailureAfterDatabaseCommandPersistsNeitherOrderNorOutboxMessage()
    {
        await fixture.RecreateDatabaseAsync();
        var interceptor = new ThrowAfterInsertCommandInterceptor();

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlServer(fixture.ConnectionString)
            .AddInterceptors(interceptor)
            .Options;
        await using (var context = new AppDbContext(
            options,
            [new ConvertDomainEventsToOutboxInterceptor()]))
        {
            context.Orders.Add(CreateOrder());

            var exception = await Assert.ThrowsAsync<DbUpdateException>(
                () => context.SaveChangesAsync(CancellationToken.None));
            Assert.IsType<TestSaveException>(exception.InnerException);
        }

        Assert.True(interceptor.InsertCommandExecuted);

        await using var verificationContext = fixture.CreateContext();
        Assert.Equal(0, await verificationContext.Orders.CountAsync(CancellationToken.None));
        Assert.Equal(0, await verificationContext.OutboxMessages.CountAsync(CancellationToken.None));
    }

    [Fact]
    public async Task RetryingFailedSaveOnSameContextPersistsOneOrderAndOneOutboxMessage()
    {
        await fixture.RecreateDatabaseAsync();
        var interceptor = new ThrowAfterConversionInterceptor(throwOnlyOnce: true);
        var order = CreateOrder();

        await using (var context = fixture.CreateContext(interceptor))
        {
            context.Orders.Add(order);

            await Assert.ThrowsAsync<TestSaveException>(
                () => context.SaveChangesAsync(CancellationToken.None));

            Assert.True(interceptor.SawAddedOutboxMessage);
            Assert.Empty(order.DomainEvents);
            var outboxEntry = Assert.Single(context.ChangeTracker.Entries<OutboxMessage>());
            Assert.Equal(EntityState.Added, outboxEntry.State);

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

    [Fact]
    public async Task UnsupportedDomainEventRemainsPendingWhenSaveChangesAsyncFails()
    {
        var domainEvent = new UnsupportedDomainEvent(
            Guid.Parse("55555555-5555-5555-5555-555555555555"),
            new DateTimeOffset(2026, 9, 20, 12, 0, 0, TimeSpan.Zero));
        var entity = new TestEntity(Guid.Parse("66666666-6666-6666-6666-666666666666"));
        entity.Raise(domainEvent);

        var options = new DbContextOptionsBuilder<TestDbContext>()
            .UseSqlServer(fixture.ConnectionString)
            .Options;

        await using var context = new TestDbContext(options);
        context.Entities.Add(entity);

        await Assert.ThrowsAsync<NotSupportedException>(
            () => context.SaveChangesAsync(CancellationToken.None));

        Assert.Same(domainEvent, Assert.Single(entity.DomainEvents));

        await Assert.ThrowsAsync<NotSupportedException>(
            () => context.SaveChangesAsync(CancellationToken.None));

        Assert.Same(domainEvent, Assert.Single(entity.DomainEvents));
        Assert.Empty(context.ChangeTracker.Entries<OutboxMessage>());
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

        public bool SawAddedOutboxMessage { get; private set; }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            SawAddedOutboxMessage = eventData.Context?.ChangeTracker
                .Entries<OutboxMessage>()
                .Count(entry => entry.State == EntityState.Added) == 1;

            if (!throwOnlyOnce || !_hasThrown)
            {
                _hasThrown = true;
                throw new TestSaveException();
            }

            return ValueTask.FromResult(result);
        }
    }

    private sealed class ThrowAfterInsertCommandInterceptor : DbCommandInterceptor
    {
        public bool InsertCommandExecuted { get; private set; }

        public override DbDataReader ReaderExecuted(
            DbCommand command,
            CommandExecutedEventData eventData,
            DbDataReader result)
        {
            ThrowAfterInsert(command, result);
            return base.ReaderExecuted(command, eventData, result);
        }

        public override ValueTask<DbDataReader> ReaderExecutedAsync(
            DbCommand command,
            CommandExecutedEventData eventData,
            DbDataReader result,
            CancellationToken cancellationToken = default)
        {
            ThrowAfterInsert(command, result);
            return base.ReaderExecutedAsync(command, eventData, result, cancellationToken);
        }

        public override int NonQueryExecuted(
            DbCommand command,
            CommandExecutedEventData eventData,
            int result)
        {
            ThrowAfterInsert(command);
            return base.NonQueryExecuted(command, eventData, result);
        }

        public override ValueTask<int> NonQueryExecutedAsync(
            DbCommand command,
            CommandExecutedEventData eventData,
            int result,
            CancellationToken cancellationToken = default)
        {
            ThrowAfterInsert(command);
            return base.NonQueryExecutedAsync(command, eventData, result, cancellationToken);
        }

        private void ThrowAfterInsert(DbCommand command, DbDataReader? reader = null)
        {
            if (command.CommandText.Contains("INSERT INTO", StringComparison.OrdinalIgnoreCase))
            {
                InsertCommandExecuted = true;
                // An open reader would block the transaction rollback this test depends on.
                reader?.Dispose();
                throw new TestSaveException();
            }
        }
    }

    private sealed class TestSaveException : Exception;

    private sealed class TestDbContext(
        DbContextOptions<TestDbContext> options)
        : DbContext(options)
    {
        public DbSet<TestEntity> Entities => Set<TestEntity>();

        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder) =>
            optionsBuilder.AddInterceptors(new ConvertDomainEventsToOutboxInterceptor());

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<TestEntity>(builder =>
            {
                builder.HasKey(entity => entity.Id);
                builder.Ignore(entity => entity.DomainEvents);
            });
            modelBuilder.Entity<OutboxMessage>();
        }
    }

    private sealed class TestEntity(Guid id) : Entity
    {
        public Guid Id { get; } = id;

        public void Raise(IDomainEvent domainEvent) => AddDomainEvent(domainEvent);
    }

    private sealed record UnsupportedDomainEvent(Guid Id, DateTimeOffset OccurredOnUtc)
        : IDomainEvent;
}
