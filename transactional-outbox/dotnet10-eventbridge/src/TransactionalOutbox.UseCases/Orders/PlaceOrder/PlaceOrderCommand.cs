namespace TransactionalOutbox.UseCases.Orders.PlaceOrder;

public sealed record PlaceOrderCommand(Guid CustomerId, decimal TotalAmount, string Currency);
