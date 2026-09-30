namespace TransactionalOutbox.Web.Contracts;

public sealed record PlaceOrderRequest(Guid CustomerId, decimal TotalAmount, string? Currency);
