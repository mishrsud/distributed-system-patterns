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
            .Validate(
                static options => options.BatchSize > 0
                    && options.MaxAttempts > 0
                    && options.LeaseDuration > TimeSpan.Zero
                    && options.IdleDelay > TimeSpan.Zero
                    && options.MaxRetryDelay > TimeSpan.Zero
                    && options.ProcessedRetention > TimeSpan.Zero
                    && options.CleanupInterval > TimeSpan.Zero,
                "Outbox options BatchSize, MaxAttempts, LeaseDuration, IdleDelay, MaxRetryDelay, "
                + "ProcessedRetention and CleanupInterval must all be positive.")
            .ValidateOnStart();

        services.AddOptions<EventBridgeOptions>()
            .Bind(configuration.GetSection("EventBridge"))
            .ValidateDataAnnotations()
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
                MaxErrorRetry = 2,
                Timeout = TimeSpan.FromSeconds(10),
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
