using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TransactionalOutbox.Infrastructure.Outbox;

namespace TransactionalOutbox.UnitTests.Outbox;

public sealed class OutboxPublisherServiceTests
{
    [Fact]
    public async Task StopAsyncCancelsABlockedProcessorPromptly()
    {
        var store = new BlockingStore();
        var options = Options.Create(new OutboxOptions { IdleDelay = TimeSpan.FromMilliseconds(50) });
        var services = new ServiceCollection();
        services.AddSingleton<IOptions<OutboxOptions>>(options);
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IOutboxStore>(store);
        services.AddSingleton<IEventPublisher, NoopPublisher>();
        services.AddSingleton<RetrySchedule>();
        services.AddLogging();
        services.AddScoped<OutboxProcessor>();
        await using ServiceProvider provider = services.BuildServiceProvider();
        var service = new OutboxPublisherService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            options,
            NullLogger<OutboxPublisherService>.Instance);

        await service.StartAsync(CancellationToken.None);
        await store.ClaimStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var stopwatch = Stopwatch.StartNew();
        await service.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));
        stopwatch.Stop();

        Assert.True(store.CancellationObserved);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2));
    }

    private sealed class NoopPublisher : IEventPublisher
    {
        public Task<PublishResult> PublishAsync(ClaimedOutboxMessage message, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class BlockingStore : IOutboxStore
    {
        public TaskCompletionSource ClaimStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool CancellationObserved { get; private set; }

        public async Task<IReadOnlyList<ClaimedOutboxMessage>> ClaimAsync(
            string workerId, int batchSize, TimeSpan leaseDuration, CancellationToken cancellationToken)
        {
            ClaimStarted.TrySetResult();
            try
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                CancellationObserved = true;
                throw;
            }

            return [];
        }

        public Task<bool> MarkProcessedAsync(Guid id, string workerId, string eventBridgeEventId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<bool> ScheduleRetryAsync(Guid id, string workerId, DateTimeOffset nextAttemptOnUtc, string errorMessage, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<bool> DeadLetterAsync(Guid id, string workerId, string errorMessage, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<int> DeleteProcessedBeforeAsync(DateTimeOffset cutoff, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
