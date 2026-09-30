using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace TransactionalOutbox.IntegrationTests.Web;

[Collection(InfrastructureTestGroup.Name)]
public sealed class OrderEndpointsTests(InfrastructureFixture fixture)
{
    [Fact]
    public async Task PostThenGetReturnsCreatedOrder()
    {
        await fixture.RecreateDatabaseAsync();
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var customerId = Guid.NewGuid();

        var response = await client.PostAsJsonAsync(
            "/orders",
            new { customerId, totalAmount = 29.95m, currency = "AUD" });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var created = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var orderId = created.RootElement.GetProperty("orderId").GetGuid();
        Assert.Equal($"/orders/{orderId}", response.Headers.Location?.OriginalString);

        var get = await client.GetAsync($"/orders/{orderId}");

        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        using var order = JsonDocument.Parse(await get.Content.ReadAsStringAsync());
        Assert.Equal(orderId, order.RootElement.GetProperty("id").GetGuid());
        Assert.Equal(customerId, order.RootElement.GetProperty("customerId").GetGuid());
        Assert.Equal(29.95m, order.RootElement.GetProperty("totalAmount").GetDecimal());
        Assert.Equal("AUD", order.RootElement.GetProperty("currency").GetString());
        Assert.Equal("Placed", order.RootElement.GetProperty("status").GetString());

        await using var context = fixture.CreateContext();
        Assert.Equal(1, await context.OutboxMessages.CountAsync());
    }

    [Fact]
    public async Task GetUnknownOrderReturnsNotFound()
    {
        await fixture.RecreateDatabaseAsync();
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync($"/orders/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Theory]
    [InlineData("00000000-0000-0000-0000-000000000000", 10, "AUD")]
    [InlineData("3f2504e0-4f89-41d3-9a0c-0305e82c3301", 0, "AUD")]
    [InlineData("3f2504e0-4f89-41d3-9a0c-0305e82c3301", -5, "AUD")]
    [InlineData("3f2504e0-4f89-41d3-9a0c-0305e82c3301", 10, "aud")]
    [InlineData("3f2504e0-4f89-41d3-9a0c-0305e82c3301", 10, "AUDD")]
    [InlineData("3f2504e0-4f89-41d3-9a0c-0305e82c3301", 10, null)]
    public async Task InvalidOrderReturnsProblemDetailsAndWritesNothing(
        string customerId, decimal totalAmount, string? currency)
    {
        await fixture.RecreateDatabaseAsync();
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/orders",
            new { customerId = Guid.Parse(customerId), totalAmount, currency });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        await AssertNothingWrittenAsync();
    }

    [Fact]
    public async Task MalformedJsonReturnsBadRequestAndWritesNothing()
    {
        await fixture.RecreateDatabaseAsync();
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();
        using var content = new StringContent("{ not json", System.Text.Encoding.UTF8, "application/json");

        var response = await client.PostAsync("/orders", content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertNothingWrittenAsync();
    }

    [Fact]
    public async Task GetWithNonGuidIdIsNotFound()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/orders/not-a-guid");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task LiveHealthCheckReturnsOkWithoutDatabase()
    {
        await using var factory = CreateFactory(
            "Server=localhost,1;Database=Unreachable;User Id=sa;Password=x;TrustServerCertificate=True;Connect Timeout=1");
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/health/live");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task MissingConnectionStringFailsStartup()
    {
        await using var factory = CreateFactory(string.Empty);

        var exception = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

        Assert.Contains("ConnectionStrings:SqlServer", exception.ToString(), StringComparison.Ordinal);
    }

    private async Task AssertNothingWrittenAsync()
    {
        await using var context = fixture.CreateContext();
        Assert.Equal(0, await context.Orders.CountAsync());
        Assert.Equal(0, await context.OutboxMessages.CountAsync());
    }

    private WebApplicationFactory<Program> CreateFactory(string? connectionString = null) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            // Development is the environment where minimal APIs throw on bad requests; cover that path.
            builder.UseEnvironment("Development");
            // UseSetting is visible to Program before Build; ConfigureAppConfiguration is applied too late
            // for the Outbox:Enabled check made while services are registered.
            builder.UseSetting("ConnectionStrings:SqlServer", connectionString ?? fixture.ConnectionString);
            builder.UseSetting("Outbox:Enabled", "false");
            // appsettings.Development.json would otherwise point at the application database.
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:SqlServer"] = connectionString ?? fixture.ConnectionString,
                }));
        });
}
