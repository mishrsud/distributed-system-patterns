using TransactionalOutbox.Core.Orders;
using TransactionalOutbox.UseCases.Abstractions;
using TransactionalOutbox.UseCases.Orders.PlaceOrder;

namespace TransactionalOutbox.UnitTests.UseCases;

public sealed class PlaceOrderHandlerTests
{
    [Fact]
    public async Task HandleAsyncAddsOrderBeforeSavingAndReturnsItsId()
    {
        var callLog = new List<string>();
        var repository = new RecordingOrderRepository(callLog);
        var unitOfWork = new RecordingUnitOfWork(callLog);
        var handler = new PlaceOrderHandler(repository, unitOfWork, TimeProvider.System);
        var cancellationToken = new CancellationTokenSource().Token;

        var result = await handler.HandleAsync(
            new PlaceOrderCommand(Guid.NewGuid(), 125.50m, "AUD"),
            cancellationToken);

        var addedOrder = Assert.Single(repository.AddedOrders);
        Assert.NotEqual(Guid.Empty, addedOrder.Id);
        Assert.Equal(addedOrder.Id, result.OrderId);
        Assert.Equal(2, callLog.Count);
        Assert.Equal("add", callLog[0]);
        Assert.Equal("save", callLog[1]);
        Assert.Equal(1, unitOfWork.SaveCalls);
        Assert.Equal(cancellationToken, unitOfWork.ReceivedCancellationToken);
    }

    [Fact]
    public async Task HandleAsyncUsesOneSuppliedUtcTimestampForOrderAndEvent()
    {
        var occurredOnUtc = new DateTimeOffset(2026, 9, 20, 10, 30, 0, TimeSpan.Zero);
        var timeProvider = new FixedTimeProvider(occurredOnUtc);
        var repository = new RecordingOrderRepository();
        var unitOfWork = new RecordingUnitOfWork();
        var handler = new PlaceOrderHandler(repository, unitOfWork, timeProvider);

        await handler.HandleAsync(
            new PlaceOrderCommand(Guid.NewGuid(), 125.50m, "AUD"),
            CancellationToken.None);

        var order = Assert.Single(repository.AddedOrders);
        Assert.Equal(occurredOnUtc, order.CreatedOnUtc);
        var orderPlaced = Assert.IsType<OrderPlacedDomainEvent>(Assert.Single(order.DomainEvents));
        Assert.Equal(occurredOnUtc, orderPlaced.OccurredOnUtc);
        Assert.Equal(1, timeProvider.UtcNowCalls);
    }

    [Fact]
    public async Task HandleAsyncPropagatesSaveFailure()
    {
        var saveException = new InvalidOperationException("save failed");
        var repository = new RecordingOrderRepository();
        var unitOfWork = new RecordingUnitOfWork(saveException: saveException);
        var handler = new PlaceOrderHandler(repository, unitOfWork, TimeProvider.System);

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            handler.HandleAsync(
                new PlaceOrderCommand(Guid.NewGuid(), 125.50m, "AUD"),
                CancellationToken.None));

        Assert.Same(saveException, thrown);
        Assert.Single(repository.AddedOrders);
        Assert.Equal(1, unitOfWork.SaveCalls);
    }

    [Fact]
    public async Task HandleAsyncRejectsNullCommand()
    {
        var handler = new PlaceOrderHandler(
            new RecordingOrderRepository(),
            new RecordingUnitOfWork(),
            TimeProvider.System);

        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            handler.HandleAsync(null!, CancellationToken.None));
    }

    [Fact]
    public void ConstructorRejectsNullDependencies()
    {
        var repository = new RecordingOrderRepository();
        var unitOfWork = new RecordingUnitOfWork();

        Assert.Throws<ArgumentNullException>(() =>
            new PlaceOrderHandler(null!, unitOfWork, TimeProvider.System));
        Assert.Throws<ArgumentNullException>(() =>
            new PlaceOrderHandler(repository, null!, TimeProvider.System));
        Assert.Throws<ArgumentNullException>(() =>
            new PlaceOrderHandler(repository, unitOfWork, null!));
    }

    private sealed class RecordingOrderRepository(List<string>? callLog = null) : IOrderRepository
    {
        public List<Order> AddedOrders { get; } = [];

        public void Add(Order order)
        {
            callLog?.Add("add");
            AddedOrders.Add(order);
        }

        public Task<Order?> GetByIdAsync(Guid id, CancellationToken cancellationToken) =>
            Task.FromResult<Order?>(null);
    }

    private sealed class RecordingUnitOfWork(
        List<string>? callLog = null,
        Exception? saveException = null) : IUnitOfWork
    {
        public int SaveCalls { get; private set; }

        public CancellationToken? ReceivedCancellationToken { get; private set; }

        public Task<int> SaveChangesAsync(CancellationToken cancellationToken)
        {
            callLog?.Add("save");
            SaveCalls++;
            ReceivedCancellationToken = cancellationToken;
            if (saveException is not null)
            {
                return Task.FromException<int>(saveException);
            }

            return Task.FromResult(1);
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public int UtcNowCalls { get; private set; }

        public override DateTimeOffset GetUtcNow()
        {
            UtcNowCalls++;
            return utcNow;
        }
    }
}
