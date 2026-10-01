using Amazon.EventBridge;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using TransactionalOutbox.Infrastructure;
using TransactionalOutbox.Infrastructure.Outbox;

namespace TransactionalOutbox.IntegrationTests.Web;

public sealed class CompositionTests
{
    [Fact]
    public void EnabledOutboxRegistersValidatorBeforePublisherAndCleanup()
    {
        var services = Build(("Outbox:Enabled", "true"));

        var hosted = HostedTypes(services);

        Assert.Equal(
            [typeof(EventBridgeStartupValidator), typeof(OutboxPublisherService), typeof(OutboxCleanupService)],
            hosted);
    }

    [Fact]
    public void DisabledOutboxRegistersNoOutboxHostedServices()
    {
        var services = Build(("Outbox:Enabled", "false"));

        Assert.Empty(HostedTypes(services));
    }

    [Theory]
    [InlineData("BatchSize", "0")]
    [InlineData("MaxAttempts", "-1")]
    [InlineData("LeaseDuration", "00:00:00")]
    [InlineData("IdleDelay", "00:00:00")]
    [InlineData("MaxRetryDelay", "00:00:00")]
    [InlineData("ProcessedRetention", "00:00:00")]
    [InlineData("CleanupInterval", "00:00:00")]
    public void NonPositiveOutboxOptionFailsValidationNamingTheOption(string name, string value)
    {
        using var provider = Build(($"Outbox:{name}", value)).BuildServiceProvider();

        var exception = Assert.Throws<OptionsValidationException>(
            () => provider.GetRequiredService<IOptions<OutboxOptions>>().Value);

        Assert.Contains($"Outbox:{name}", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("EventBusName", "")]
    [InlineData("Source", "")]
    [InlineData("DetailType", "")]
    public void EmptyRequiredEventBridgeOptionFailsValidation(string name, string value)
    {
        using var provider = Build(($"EventBridge:{name}", value)).BuildServiceProvider();

        var exception = Assert.Throws<OptionsValidationException>(
            () => provider.GetRequiredService<IOptions<EventBridgeOptions>>().Value);

        Assert.Contains(name, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ConfiguredEndpointSetsServiceUrlAndAuthenticationRegionButNotRegionEndpoint()
    {
        using var provider = Build(
            ("EventBridge:ServiceUrl", "http://localhost:4566"),
            ("EventBridge:AuthenticationRegion", "ap-southeast-2")).BuildServiceProvider();

        var config = provider.GetRequiredService<IAmazonEventBridge>().Config;

        Assert.Equal("http://localhost:4566/", config.ServiceURL); // SDK normalises with a trailing slash
        Assert.Equal("ap-southeast-2", config.AuthenticationRegion);
        Assert.Null(config.RegionEndpoint);
    }

    [Fact]
    public void DefaultSdkTimeoutAndRetriesComeFromEventBridgeOptions()
    {
        using var provider = Build().BuildServiceProvider();

        var config = provider.GetRequiredService<IAmazonEventBridge>().Config;
        var options = provider.GetRequiredService<IOptions<EventBridgeOptions>>().Value;

        Assert.Equal(TimeSpan.FromSeconds(5), config.Timeout);
        Assert.Equal(2, config.MaxErrorRetry);
        Assert.Equal(TimeSpan.FromSeconds(15), options.MaxPublishDuration);
        Assert.True(provider.GetRequiredService<IOptions<OutboxOptions>>().Value.LeaseDuration > options.MaxPublishDuration);
    }

    [Fact]
    public void ConfiguredSdkTimeoutAndRetriesReachTheClient()
    {
        using var provider = Build(
            ("EventBridge:RequestTimeout", "00:00:04"),
            ("EventBridge:MaxErrorRetry", "3")).BuildServiceProvider();

        var config = provider.GetRequiredService<IAmazonEventBridge>().Config;

        Assert.Equal(TimeSpan.FromSeconds(4), config.Timeout);
        Assert.Equal(3, config.MaxErrorRetry);
        Assert.Equal(
            TimeSpan.FromSeconds(16),
            provider.GetRequiredService<IOptions<EventBridgeOptions>>().Value.MaxPublishDuration);
    }

    [Theory]
    [InlineData("00:00:15", "00:00:05", "2")] // lease equal to the 15 s budget
    [InlineData("00:00:30", "00:00:10", "2")] // 30 s budget exceeds the default lease
    public void LeaseNotLongerThanOnePublishBudgetFailsValidation(string lease, string timeout, string retries)
    {
        using var provider = Build(
            ("Outbox:LeaseDuration", lease),
            ("EventBridge:RequestTimeout", timeout),
            ("EventBridge:MaxErrorRetry", retries)).BuildServiceProvider();

        var exception = Assert.Throws<OptionsValidationException>(
            () => provider.GetRequiredService<IOptions<OutboxOptions>>().Value);

        Assert.Contains("Outbox:LeaseDuration", exception.Message, StringComparison.Ordinal);
        Assert.Contains("EventBridge:RequestTimeout", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("RequestTimeout", "00:00:00")]
    [InlineData("MaxErrorRetry", "-1")]
    public void InvalidSdkBudgetOptionFailsValidationNamingTheOption(string name, string value)
    {
        using var provider = Build(($"EventBridge:{name}", value)).BuildServiceProvider();

        var exception = Assert.Throws<OptionsValidationException>(
            () => provider.GetRequiredService<IOptions<EventBridgeOptions>>().Value);

        Assert.Contains($"EventBridge:{name}", exception.Message, StringComparison.Ordinal);
    }

    private static List<Type?> HostedTypes(IServiceCollection services) =>
        [.. services.Where(d => d.ServiceType == typeof(IHostedService)).Select(d => d.ImplementationType)];

    private static ServiceCollection Build(params (string Key, string Value)[] settings)
    {
        var values = new Dictionary<string, string?>
        {
            ["ConnectionStrings:SqlServer"] = "Server=localhost;Database=x",
            ["EventBridge:EventBusName"] = "orders",
            ["EventBridge:Source"] = "sample.orders",
            ["EventBridge:DetailType"] = "OrderPlaced",
        };
        foreach (var (key, value) in settings)
        {
            values[key] = value;
        }

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddInfrastructure(configuration);
        return services;
    }
}
