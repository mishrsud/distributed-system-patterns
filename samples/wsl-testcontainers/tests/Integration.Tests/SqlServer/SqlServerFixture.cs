using Integration.Tests.Infrastructure;
using Testcontainers.MsSql;
using Xunit;

namespace Integration.Tests.SqlServer;

public sealed class SqlServerFixture : IAsyncLifetime
{
    private readonly MsSqlContainer container = new MsSqlBuilder(ImageCatalog.MsSql).Build();

    public string ConnectionString => container.GetConnectionString();

    public ValueTask InitializeAsync() => new(container.StartAsync(TestContext.Current.CancellationToken));

    public ValueTask DisposeAsync() => container.DisposeAsync();
}
