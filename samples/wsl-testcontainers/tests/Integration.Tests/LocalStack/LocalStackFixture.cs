using Integration.Tests.Infrastructure;
using Testcontainers.LocalStack;
using Xunit;

namespace Integration.Tests.LocalStack;

public sealed class LocalStackFixture : IAsyncLifetime
{
    private const string MissingTokenMessage = "Set LOCALSTACK_AUTH_TOKEN in the WSL environment or CI secret store.";
    private readonly LocalStackContainer container;
    private readonly string token;

    public LocalStackFixture()
    {
        token = Environment.GetEnvironmentVariable("LOCALSTACK_AUTH_TOKEN") ?? string.Empty;
        if (string.IsNullOrWhiteSpace(token))
        {
            throw new InvalidOperationException(MissingTokenMessage);
        }

        container = new LocalStackBuilder(ImageCatalog.LocalStack)
            .WithEnvironment("LOCALSTACK_AUTH_TOKEN", token)
            .WithEnvironment("ENFORCE_IAM", "1")
            .Build();
    }

    public string ServiceUrl => container.GetConnectionString();

    public LocalStackClientFactory CreateClients() => new(ServiceUrl);

    public async Task<string> GetSanitizedLogsAsync(CancellationToken cancellationToken)
    {
        var logs = await container.GetLogsAsync(DateTime.MinValue, DateTime.UtcNow, false, cancellationToken);
        return $"stdout: {Sanitize(logs.Stdout)}\nstderr: {Sanitize(logs.Stderr)}";
    }

    private string Sanitize(string value) => value.Replace(token, "[REDACTED]", StringComparison.Ordinal);

    public ValueTask InitializeAsync() => new(container.StartAsync(TestContext.Current.CancellationToken));

    public ValueTask DisposeAsync() => container.DisposeAsync();
}
