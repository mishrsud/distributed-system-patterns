using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace TransactionalOutbox.Infrastructure.Outbox;

public sealed partial class OutboxPublisherService(
    IServiceScopeFactory scopeFactory,
    IOptions<OutboxOptions> options,
    ILogger<OutboxPublisherService> logger) : BackgroundService
{
    private const double MaxJitterFraction = 0.2;

    private readonly OutboxOptions _options = options.Value;
    private readonly string _workerId = $"{Environment.MachineName}:{Environment.ProcessId}:{Guid.NewGuid():N}";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        LogStarted(logger, _workerId);
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                if (!await TryProcessBatchAsync(stoppingToken))
                {
                    await Task.Delay(NextDelay(), stoppingToken);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Host shutdown: exit cleanly.
        }

        LogStopped(logger, _workerId);
    }

    // Returns true when a nonempty batch was processed, so the caller polls again immediately.
    private async Task<bool> TryProcessBatchAsync(CancellationToken stoppingToken)
    {
        try
        {
            await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
            var processor = scope.ServiceProvider.GetRequiredService<OutboxProcessor>();
            return await processor.ProcessBatchAsync(_workerId, stoppingToken) > 0;
        }
        catch (Exception ex) when (!(ex is OperationCanceledException && stoppingToken.IsCancellationRequested))
        {
            LogCycleFailed(logger, ex);
            return false;
        }
    }

    private TimeSpan NextDelay() =>
        _options.IdleDelay + (_options.IdleDelay * (Random.Shared.NextDouble() * MaxJitterFraction));

    [LoggerMessage(Level = LogLevel.Information, Message = "Outbox publisher {WorkerId} started.")]
    private static partial void LogStarted(ILogger logger, string workerId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Outbox publisher {WorkerId} stopped.")]
    private static partial void LogStopped(ILogger logger, string workerId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Outbox processing cycle failed; retrying after delay.")]
    private static partial void LogCycleFailed(ILogger logger, Exception exception);
}
