using Microsoft.EntityFrameworkCore;
using TransactionalOutbox.Core.Orders;
using TransactionalOutbox.UseCases.Abstractions;

namespace TransactionalOutbox.Infrastructure.Persistence;

public sealed class OrderRepository(AppDbContext context) : IOrderRepository
{
    public void Add(Order order)
    {
        context.Orders.Add(order);
    }

    public Task<Order?> GetByIdAsync(Guid id, CancellationToken cancellationToken) =>
        context.Orders
            .AsNoTracking()
            .SingleOrDefaultAsync(order => order.Id == id, cancellationToken);
}
