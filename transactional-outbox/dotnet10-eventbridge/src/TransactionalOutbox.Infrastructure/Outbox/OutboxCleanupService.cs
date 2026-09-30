using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace TransactionalOutbox.Infrastructure.Outbox;

public sealed partial class OutboxCleanupService(
    IServiceScopeFactory scopeFactory,
    IOptions<OutboxOptions> options,
    TimeProvider timeProvider,
    ILogger<OutboxCleanupService> logger) : BackgroundService
{
    private readonly OutboxOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await CleanAsync(stoppingToken);
                await Task.Delay(_options.CleanupInterval, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Host shutdown: exit cleanly.
        }
    }

    private async Task CleanAsync(CancellationToken stoppingToken)
    {
        try
        {
            await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
            var store = scope.ServiceProvider.GetRequiredService<IOutboxStore>();
            DateTimeOffset cutoff = timeProvider.GetUtcNow() - _options.ProcessedRetention;
            int deleted = await store.DeleteProcessedBeforeAsync(cutoff, stoppingToken);
            LogCleaned(logger, deleted, cutoff);
        }
        catch (Exception ex) when (!(ex is OperationCanceledException && stoppingToken.IsCancellationRequested))
        {
            LogCleanupFailed(logger, ex);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Outbox cleanup deleted {DeletedCount} processed messages older than {Cutoff:O}.")]
    private static partial void LogCleaned(ILogger logger, int deletedCount, DateTimeOffset cutoff);

    [LoggerMessage(Level = LogLevel.Error, Message = "Outbox cleanup failed; will retry next interval.")]
    private static partial void LogCleanupFailed(ILogger logger, Exception exception);
}
