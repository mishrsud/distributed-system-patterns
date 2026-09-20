using Integration.Tests.Infrastructure;
using Testcontainers.PostgreSql;
using Xunit;

namespace Integration.Tests.PostgreSql;

public sealed class PostgreSqlFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer container = new PostgreSqlBuilder(ImageCatalog.PostgreSql).Build();

    public string GetConnectionString() => container.GetConnectionString();

    public ValueTask InitializeAsync() => new(container.StartAsync(TestContext.Current.CancellationToken));

    public ValueTask DisposeAsync() => container.DisposeAsync();
}
