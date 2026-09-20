using TransactionalOutbox.Core.Orders;
using TransactionalOutbox.UseCases.Abstractions;

namespace TransactionalOutbox.UseCases.Orders.PlaceOrder;

public sealed class PlaceOrderHandler
{
    private readonly IOrderRepository _orderRepository;
    private readonly IUnitOfWork _unitOfWork;
    private readonly TimeProvider _timeProvider;

    public PlaceOrderHandler(
        IOrderRepository orderRepository,
        IUnitOfWork unitOfWork,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(orderRepository);
        ArgumentNullException.ThrowIfNull(unitOfWork);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _orderRepository = orderRepository;
        _unitOfWork = unitOfWork;
        _timeProvider = timeProvider;
    }

    public async Task<PlaceOrderResult> HandleAsync(
        PlaceOrderCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var orderId = Guid.NewGuid();
        var occurredOnUtc = _timeProvider.GetUtcNow();
        var order = Order.Place(
            orderId,
            command.CustomerId,
            command.TotalAmount,
            command.Currency,
            occurredOnUtc);

        _orderRepository.Add(order);
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return new PlaceOrderResult(order.Id);
    }
}
