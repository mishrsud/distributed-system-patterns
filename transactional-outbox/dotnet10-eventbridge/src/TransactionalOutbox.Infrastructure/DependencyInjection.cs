using Amazon.EventBridge;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using TransactionalOutbox.Infrastructure.Outbox;
using TransactionalOutbox.Infrastructure.Persistence;
using TransactionalOutbox.Infrastructure.Persistence.Interceptors;
using TransactionalOutbox.UseCases.Abstractions;

namespace TransactionalOutbox.Infrastructure;

public static class DependencyInjection
{
    private const string ConnectionStringName = "SqlServer";

    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<SqlServerConnectionOptions>()
            .Configure(options => options.ConnectionString = configuration.GetConnectionString(ConnectionStringName))
            .Validate(
                options => !string.IsNullOrWhiteSpace(options.ConnectionString),
                $"Connection string 'ConnectionStrings:{ConnectionStringName}' is missing or empty.")
            .ValidateOnStart();

        services.AddOptions<OutboxOptions>()
            .Bind(configuration.GetSection("Outbox"))
            .Validate(static options => options.BatchSize > 0, "Outbox:BatchSize must be positive.")
            .Validate(static options => options.MaxAttempts > 0, "Outbox:MaxAttempts must be positive.")
            .Validate(static options => options.LeaseDuration > TimeSpan.Zero, "Outbox:LeaseDuration must be positive.")
            .Validate(static options => options.IdleDelay > TimeSpan.Zero, "Outbox:IdleDelay must be positive.")
            .Validate(static options => options.MaxRetryDelay > TimeSpan.Zero, "Outbox:MaxRetryDelay must be positive.")
            .Validate(
                static options => options.ProcessedRetention > TimeSpan.Zero,
                "Outbox:ProcessedRetention must be positive.")
            .Validate(
                static options => options.CleanupInterval > TimeSpan.Zero,
                "Outbox:CleanupInterval must be positive.")
            .Validate<IOptions<EventBridgeOptions>>(
                static (options, eventBridge) => options.LeaseDuration > eventBridge.Value.MaxPublishDuration,
                "Outbox:LeaseDuration must be longer than one publish budget, " +
                "EventBridge:RequestTimeout x (EventBridge:MaxErrorRetry + 1); " +
                "otherwise a publish could outlive the lease it runs under.")
            .ValidateOnStart();

        services.AddOptions<EventBridgeOptions>()
            .Bind(configuration.GetSection("EventBridge"))
            .ValidateDataAnnotations()
            .Validate(
                static options => options.RequestTimeout > TimeSpan.Zero,
                "EventBridge:RequestTimeout must be positive.")
            .Validate(
                static options => options.MaxErrorRetry >= 0,
                "EventBridge:MaxErrorRetry must not be negative.")
            .ValidateOnStart();

        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<ISaveChangesInterceptor, ConvertDomainEventsToOutboxInterceptor>();
        services.AddDbContext<AppDbContext>((provider, builder) =>
            builder.UseSqlServer(
                provider.GetRequiredService<IOptions<SqlServerConnectionOptions>>().Value.ConnectionString,
                sql => sql.EnableRetryOnFailure()));

        services.AddScoped<IUnitOfWork>(static provider => provider.GetRequiredService<AppDbContext>());
        services.AddScoped<IOrderRepository, OrderRepository>();
        services.AddScoped<IOutboxStore, SqlServerOutboxStore>();
        services.AddScoped<IEventPublisher, EventBridgePublisher>();
        services.AddScoped<RetrySchedule>();
        services.AddScoped<OutboxProcessor>();

        services.AddSingleton<IAmazonEventBridge>(static provider =>
        {
            var options = provider.GetRequiredService<IOptions<EventBridgeOptions>>().Value;
            var config = new AmazonEventBridgeConfig
            {
                // Together these bound one publish to roughly RequestTimeout x (MaxErrorRetry + 1)
                // (15 s by default; the SDK's backoff between retries adds a little). Startup validation
                // keeps Outbox:LeaseDuration above that budget, so a single publish is not expected to
                // outlive its lease, and OutboxProcessor stops a batch before the remaining lease drops
                // below one budget.
                MaxErrorRetry = options.MaxErrorRetry,
                Timeout = options.RequestTimeout,
            };

            if (!string.IsNullOrWhiteSpace(options.ServiceUrl))
            {
                config.ServiceURL = options.ServiceUrl;
            }

            if (!string.IsNullOrWhiteSpace(options.AuthenticationRegion))
            {
                config.AuthenticationRegion = options.AuthenticationRegion;
            }

            return new AmazonEventBridgeClient(config);
        });

        var outboxEnabled = configuration.GetSection("Outbox").GetValue("Enabled", true);
        if (outboxEnabled)
        {
            // Hosted services start in registration order: prove the bus exists before publishing.
            services.AddHostedService<EventBridgeStartupValidator>();
            services.AddHostedService<OutboxPublisherService>();
            services.AddHostedService<OutboxCleanupService>();
        }

        return services;
    }

    private sealed class SqlServerConnectionOptions
    {
        public string? ConnectionString { get; set; }
    }
}
