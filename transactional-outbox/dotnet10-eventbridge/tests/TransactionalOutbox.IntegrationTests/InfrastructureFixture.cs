using System.Net;
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

    public const string RemoteDatabaseOptInVariable =
        "TRANSACTIONAL_OUTBOX_ALLOW_REMOTE_TEST_DATABASE";

    public InfrastructureFixture()
        : this(
            Environment.GetEnvironmentVariable("ConnectionStrings__SqlServer")
                ?? DefaultConnectionString,
            Environment.GetEnvironmentVariable(RemoteDatabaseOptInVariable))
    {
    }

    // Internal so xUnit still sees exactly one public constructor on the collection fixture.
    internal InfrastructureFixture(string connectionString, string? destructiveDatabaseTestsOptIn)
    {
        ConnectionString = connectionString;

        var connectionStringBuilder = new SqlConnectionStringBuilder(ConnectionString);
        if (!string.Equals(
                connectionStringBuilder.InitialCatalog,
                "TransactionalOutboxTests",
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Integration tests may only recreate the TransactionalOutboxTests database.");
        }

        var remoteAllowed = string.Equals(
            destructiveDatabaseTestsOptIn,
            "true",
            StringComparison.OrdinalIgnoreCase);
        if (!remoteAllowed && !IsLoopbackDataSource(connectionStringBuilder.DataSource))
        {
            throw new InvalidOperationException(
                "Integration tests drop and recreate their database, so they only run against a " +
                $"loopback SQL Server. Set {RemoteDatabaseOptInVariable}=true to allow a remote host.");
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

    private static bool IsLoopbackDataSource(string dataSource)
    {
        var host = dataSource.Trim();
        if (host.StartsWith("tcp:", StringComparison.OrdinalIgnoreCase))
        {
            host = host["tcp:".Length..];
        }

        var separator = host.IndexOfAny([',', '\\']);
        if (separator >= 0)
        {
            host = host[..separator];
        }

        host = host.Trim().Trim('[', ']');

        return host is "." or "(local)"
            || string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
            || (IPAddress.TryParse(host, out var address) && IPAddress.IsLoopback(address));
    }
}
