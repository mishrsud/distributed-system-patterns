using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using TransactionalOutbox.Infrastructure.Persistence;
using TransactionalOutbox.Infrastructure.Persistence.Interceptors;

namespace TransactionalOutbox.IntegrationTests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class InfrastructureTestGroup : ICollectionFixture<InfrastructureFixture>
{
    public const string Name = "Infrastructure";
}

public sealed class InfrastructureFixture : IAsyncLifetime
{
    private const string DefaultConnectionString =
        "Server=localhost,14339;Database=TransactionalOutboxTests;User Id=sa;" +
        "Password=Local_dev_Only_123!;TrustServerCertificate=True";

    public InfrastructureFixture()
    {
        ConnectionString = Environment.GetEnvironmentVariable("ConnectionStrings__SqlServer")
            ?? DefaultConnectionString;

        var connectionStringBuilder = new SqlConnectionStringBuilder(ConnectionString);
        if (!string.Equals(
                connectionStringBuilder.InitialCatalog,
                "TransactionalOutboxTests",
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Integration tests may only recreate the TransactionalOutboxTests database.");
        }
    }

    public string ConnectionString { get; }

    public async Task InitializeAsync() => await RecreateDatabaseAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    public async Task RecreateDatabaseAsync()
    {
        await using var context = CreateContext();
        await context.Database.EnsureDeletedAsync();
        await context.Database.EnsureCreatedAsync();
    }

    public AppDbContext CreateContext(params ISaveChangesInterceptor[] additionalInterceptors)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlServer(ConnectionString)
            .Options;
        ISaveChangesInterceptor[] interceptors =
            [new ConvertDomainEventsToOutboxInterceptor(), .. additionalInterceptors];

        return new AppDbContext(options, interceptors);
    }
}
