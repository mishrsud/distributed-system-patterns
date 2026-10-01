using TransactionalOutbox.Core.Orders;
using TransactionalOutbox.Infrastructure;
using TransactionalOutbox.UseCases.Abstractions;
using TransactionalOutbox.UseCases.Orders.PlaceOrder;
using TransactionalOutbox.Web;
using TransactionalOutbox.Web.Contracts;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddScoped<PlaceOrderHandler>();
builder.Services.AddExceptionHandler<RequestExceptionHandler>();
builder.Services.AddProblemDetails();

var app = builder.Build();

app.UseExceptionHandler();

app.MapGet("/health/live", () => Results.Ok());

app.MapPost("/orders", async (PlaceOrderRequest request, PlaceOrderHandler handler, CancellationToken ct) =>
{
    PlaceOrderResult result = await handler.HandleAsync(
        new PlaceOrderCommand(request.CustomerId, request.TotalAmount, request.Currency ?? string.Empty), ct);
    return Results.Created($"/orders/{result.OrderId}", new { result.OrderId });
});

app.MapGet("/orders/{id:guid}", async (Guid id, IOrderRepository repository, CancellationToken ct) =>
{
    Order? order = await repository.GetByIdAsync(id, ct);
    return order is null ? Results.NotFound() : Results.Ok(OrderResponse.From(order));
});

app.Run();

public partial class Program;
