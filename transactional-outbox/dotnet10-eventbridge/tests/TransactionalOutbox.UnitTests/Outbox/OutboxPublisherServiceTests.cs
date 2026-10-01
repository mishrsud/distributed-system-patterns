using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TransactionalOutbox.Infrastructure.Outbox;

namespace TransactionalOutbox.UnitTests.Outbox;

public sealed class OutboxPublisherServiceTests
{
    [Fact]
    public async Task StopAsyncCancelsABlockedProcessorPromptlyAndCleanly()
    {
        var store = new ScriptedStore(throwOnFirstClaim: false);
        var logger = new CapturingLogger<OutboxPublisherService>();
        await using ServiceProvider provider = BuildProvider(store);
        var service = CreateService(provider, logger);

        await service.StartAsync(CancellationToken.None);
        await store.BlockedClaimStarted.WaitAsync(TimeSpan.FromSeconds(5));

        var stopwatch = Stopwatch.StartNew();
        await service.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));
        stopwatch.Stop();

        Assert.True(store.CancellationObserved);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2));
        Assert.DoesNotContain(logger.Entries, e => e.Level >= LogLevel.Error);
        Assert.True(service.ExecuteTask!.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task CycleFailureIsLoggedOnceAndPollingContinues()
    {
        var store = new ScriptedStore(throwOnFirstClaim: true);
        var logger = new CapturingLogger<OutboxPublisherService>();
        await using ServiceProvider provider = BuildProvider(store);
        var service = CreateService(provider, logger);

        await service.StartAsync(CancellationToken.None);
        await store.BlockedClaimStarted.WaitAsync(TimeSpan.FromSeconds(5));
        await service.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(2, store.ClaimCount);
        Assert.Single(logger.Entries, e => e.Level == LogLevel.Error);
        Assert.True(service.ExecuteTask!.IsCompletedSuccessfully);
    }

    private static OutboxPublisherService CreateService(
        ServiceProvider provider,
        CapturingLogger<OutboxPublisherService> logger) =>
        new(
            provider.GetRequiredService<IServiceScopeFactory>(),
            provider.GetRequiredService<IOptions<OutboxOptions>>(),
            logger);

    private static ServiceProvider BuildProvider(ScriptedStore store)
    {
        var options = Options.Create(new OutboxOptions { IdleDelay = TimeSpan.FromMilliseconds(20) });
        var services = new ServiceCollection();
        services.AddSingleton<IOptions<OutboxOptions>>(options);
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IOutboxStore>(store);
        services.AddSingleton<IEventPublisher, NoopPublisher>();
        services.AddSingleton<RetrySchedule>();
        services.AddLogging();
        services.AddScoped<OutboxProcessor>();
        return services.BuildServiceProvider();
    }

    private sealed class NoopPublisher : IEventPublisher
    {
        public Task<PublishResult> PublishAsync(ClaimedOutboxMessage message, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    // Optionally throws on the first claim; every later claim blocks until cancelled.
    private sealed class ScriptedStore(bool throwOnFirstClaim) : IOutboxStore
    {
        private readonly TaskCompletionSource _firstBlockingClaim = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _claimCount;

        public int ClaimCount => Volatile.Read(ref _claimCount);

        public bool CancellationObserved { get; private set; }

        public Task BlockedClaimStarted => _firstBlockingClaim.Task;

        public async Task<IReadOnlyList<ClaimedOutboxMessage>> ClaimAsync(
            string workerId, int batchSize, TimeSpan leaseDuration, CancellationToken cancellationToken)
        {
            int count = Interlocked.Increment(ref _claimCount);
            if (throwOnFirstClaim && count == 1)
            {
                throw new InvalidOperationException("transient claim failure");
            }

            _firstBlockingClaim.TrySetResult();
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

        public Task<bool> ScheduleRetryAsync(Guid id, string workerId, TimeSpan delay, string errorMessage, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<bool> ReleaseAsync(Guid id, string workerId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<bool> DeadLetterAsync(Guid id, string workerId, string errorMessage, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<int> DeleteProcessedBeforeAsync(DateTimeOffset cutoff, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
