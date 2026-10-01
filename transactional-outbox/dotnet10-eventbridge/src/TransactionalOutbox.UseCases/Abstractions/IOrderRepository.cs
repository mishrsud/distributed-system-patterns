using TransactionalOutbox.Core.Orders;

namespace TransactionalOutbox.UseCases.Abstractions;

public interface IOrderRepository
{
    void Add(Order order);

    Task<Order?> GetByIdAsync(Guid id, CancellationToken cancellationToken);
}
