using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TransactionalOutbox.Infrastructure.Outbox;

namespace TransactionalOutbox.UnitTests.Outbox;

public sealed class OutboxProcessorTests
{
    private const string WorkerId = "worker-1";
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public enum ExpectedTransition
    {
        Processed,
        RetryScheduled,
        DeadLettered,
    }

    private static ClaimedOutboxMessage Message(int attemptCount = 1, Guid? id = null) => new(
        id ?? Guid.NewGuid(), "OrderPlaced", 1, "{}", Now, attemptCount);

    private static OutboxProcessor Create(
        FakeStore store,
        FakeEventPublisher publisher,
        FakeLogger? logger = null,
        int maxAttempts = 10,
        ManualTimeProvider? timeProvider = null)
    {
        var options = Options.Create(new OutboxOptions
        {
            MaxAttempts = maxAttempts,
            BatchSize = 5,
            LeaseDuration = TimeSpan.FromSeconds(30),
        });

        // 5 s x (2 + 1) = a 15 s publish budget under the 30 s lease.
        var eventBridgeOptions = Options.Create(new EventBridgeOptions
        {
            RequestTimeout = TimeSpan.FromSeconds(5),
            MaxErrorRetry = 2,
        });
        return new OutboxProcessor(
            store,
            publisher,
            new RetrySchedule(options),
            options,
            eventBridgeOptions,
            timeProvider ?? new ManualTimeProvider(),
            logger ?? new FakeLogger());
    }

    [Theory]
    [InlineData(PublishOutcome.Success, ExpectedTransition.Processed)]
    [InlineData(PublishOutcome.RetryableFailure, ExpectedTransition.RetryScheduled)]
    [InlineData(PublishOutcome.PermanentFailure, ExpectedTransition.DeadLettered)]
    public async Task ProcessBatchTransitionsEachClaimedMessage(
        PublishOutcome outcome,
        ExpectedTransition expectedTransition)
    {
        var message = Message();
        var store = new FakeStore { Claimed = [message] };
        var publisher = new FakeEventPublisher
        {
            Handler = _ => new PublishResult(outcome, outcome == PublishOutcome.Success ? "eb-1" : null, "Code", "summary"),
        };

        int claimed = await Create(store, publisher).ProcessBatchAsync(WorkerId, CancellationToken.None);

        Assert.Equal(1, claimed);
        Assert.Equal(WorkerId, store.ClaimedWorkerId);
        Assert.Equal(5, store.ClaimedBatchSize);
        string transition = Assert.Single(store.Calls);
        switch (expectedTransition)
        {
            case ExpectedTransition.Processed:
                Assert.Equal($"processed:{message.Id}:{WorkerId}:eb-1", transition);
                break;
            case ExpectedTransition.RetryScheduled:
                Assert.StartsWith($"retry:{message.Id}:{WorkerId}:", transition);
                Assert.True(Assert.Single(store.RetryDelays) > TimeSpan.Zero);
                Assert.Equal("summary", store.LastError);
                break;
            case ExpectedTransition.DeadLettered:
                Assert.Equal($"deadletter:{message.Id}:{WorkerId}", transition);
                Assert.Equal("summary", store.LastError);
                break;
        }
    }

    [Fact]
    public async Task ProcessBatchReturnsZeroWithoutPublishingWhenNothingIsClaimed()
    {
        var store = new FakeStore();
        var publisher = new FakeEventPublisher();

        int claimed = await Create(store, publisher).ProcessBatchAsync(WorkerId, CancellationToken.None);

        Assert.Equal(0, claimed);
        Assert.Empty(publisher.Published);
        Assert.Empty(store.Calls);
    }

    [Fact]
    public async Task ProcessBatchDeadLettersRetryableFailureAtMaxAttempts()
    {
        var message = Message(attemptCount: 3);
        var store = new FakeStore { Claimed = [message] };
        var publisher = new FakeEventPublisher { Handler = _ => PublishResult.Retryable("Throttled", "slow down") };

        await Create(store, publisher, maxAttempts: 3).ProcessBatchAsync(WorkerId, CancellationToken.None);

        Assert.Equal([$"deadletter:{message.Id}:{WorkerId}"], store.Calls);
    }

    [Fact]
    public async Task ProcessBatchSchedulesRetryBelowMaxAttempts()
    {
        var message = Message(attemptCount: 2);
        var store = new FakeStore { Claimed = [message] };
        var publisher = new FakeEventPublisher { Handler = _ => PublishResult.Retryable("Throttled", "slow down") };

        await Create(store, publisher, maxAttempts: 3).ProcessBatchAsync(WorkerId, CancellationToken.None);

        Assert.StartsWith("retry:", Assert.Single(store.Calls));
    }

    [Fact]
    public async Task ProcessBatchTreatsPublisherExceptionAsRetryableAndContinues()
    {
        var failing = Message();
        var next = Message();
        var store = new FakeStore { Claimed = [failing, next] };
        var publisher = new FakeEventPublisher
        {
            Handler = m => m.Id == failing.Id
                ? throw new InvalidOperationException("boom")
                : PublishResult.Succeeded("eb-2"),
        };

        int claimed = await Create(store, publisher).ProcessBatchAsync(WorkerId, CancellationToken.None);

        Assert.Equal(2, claimed);
        Assert.StartsWith($"retry:{failing.Id}:", store.Calls[0]);
        Assert.Equal($"processed:{next.Id}:{WorkerId}:eb-2", store.Calls[1]);
        Assert.Equal("System.InvalidOperationException: boom", store.LastRetryError);
    }

    [Fact]
    public async Task ProcessBatchTreatsTimeoutCancellationWithLiveTokenAsRetryable()
    {
        var message = Message();
        var store = new FakeStore { Claimed = [message] };
        var publisher = new FakeEventPublisher { Handler = _ => throw new TaskCanceledException() };

        await Create(store, publisher).ProcessBatchAsync(WorkerId, CancellationToken.None);

        Assert.StartsWith($"retry:{message.Id}:", Assert.Single(store.Calls));
    }

    [Fact]
    public async Task ProcessBatchDeadLettersPublisherExceptionAtMaxAttempts()
    {
        var message = Message(attemptCount: 2);
        var store = new FakeStore { Claimed = [message] };
        var publisher = new FakeEventPublisher { Handler = _ => throw new InvalidOperationException("boom") };

        await Create(store, publisher, maxAttempts: 2).ProcessBatchAsync(WorkerId, CancellationToken.None);

        Assert.Equal([$"deadletter:{message.Id}:{WorkerId}"], store.Calls);
        Assert.Equal("System.InvalidOperationException: boom", store.LastError);
    }

    [Fact]
    public async Task ProcessBatchPropagatesCancellationWithoutCallingATransition()
    {
        using var cts = new CancellationTokenSource();
        var store = new FakeStore { Claimed = [Message()] };
        var publisher = new FakeEventPublisher
        {
            Handler = _ =>
            {
                cts.Cancel();
                throw new OperationCanceledException(cts.Token);
            },
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Create(store, publisher).ProcessBatchAsync(WorkerId, cts.Token));

        Assert.Empty(store.Calls);
    }

    [Fact]
    public async Task ProcessBatchLogsWarningAndDoesNotRetryWhenOwnerIsStale()
    {
        var message = Message();
        var store = new FakeStore { Claimed = [message], TransitionResult = false };
        var publisher = new FakeEventPublisher { Handler = _ => PublishResult.Succeeded("eb-1") };
        var logger = new FakeLogger();

        int claimed = await Create(store, publisher, logger).ProcessBatchAsync(WorkerId, CancellationToken.None);

        Assert.Equal(1, claimed);
        Assert.Single(store.Calls);
        Assert.Single(publisher.Published);
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning);
    }

    [Fact]
    public async Task ProcessBatchPublishesEveryClaimedMessageWhenTimeDoesNotAdvance()
    {
        Guid[] ids = [.. Enumerable.Range(0, 5).Select(_ => Guid.NewGuid())];
        var store = new FakeStore { Claimed = [.. ids.Select(id => Message(id: id))] };
        var publisher = new FakeEventPublisher();

        int claimed = await Create(store, publisher).ProcessBatchAsync(WorkerId, CancellationToken.None);

        Assert.Equal(5, claimed);
        Assert.Equal(ids, publisher.Published.Select(m => m.Id));
        Assert.All(store.Calls, call => Assert.StartsWith("processed:", call));
        Assert.Equal(5, store.Calls.Count);
    }

    [Fact]
    public async Task ProcessBatchReleasesRemainingMessagesOnceTheLeaseCannotCoverAnotherPublish()
    {
        Guid[] ids = [.. Enumerable.Range(0, 5).Select(_ => Guid.NewGuid())];
        var store = new FakeStore { Claimed = [.. ids.Select(id => Message(id: id))] };
        var time = new ManualTimeProvider();
        var publisher = new FakeEventPublisher
        {
            // Each publish takes 10 s. Before the third, 20 s have elapsed and 20 + 15 >= 30.
            Handler = _ =>
            {
                time.Advance(TimeSpan.FromSeconds(10));
                return PublishResult.Succeeded("eb");
            },
        };
        var logger = new FakeLogger();

        int claimed = await Create(store, publisher, logger, timeProvider: time)
            .ProcessBatchAsync(WorkerId, CancellationToken.None);

        Assert.Equal(5, claimed);
        Assert.Equal(ids[..2], publisher.Published.Select(m => m.Id));
        Assert.Equal(
            [
                $"processed:{ids[0]}:{WorkerId}:eb",
                $"processed:{ids[1]}:{WorkerId}:eb",
                $"release:{ids[2]}:{WorkerId}",
                $"release:{ids[3]}:{WorkerId}",
                $"release:{ids[4]}:{WorkerId}",
            ],
            store.Calls);
        var warning = Assert.Single(logger.Entries, e => e.Level == LogLevel.Warning);
        Assert.Contains("3", warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProcessBatchRecordsSuccessEvenWhenShutdownIsRequestedDuringThePublish()
    {
        using var shutdown = new CancellationTokenSource();
        var message = Message();
        var store = new FakeStore { Claimed = [message] };
        var publisher = new FakeEventPublisher
        {
            Handler = _ =>
            {
                shutdown.Cancel();
                return PublishResult.Succeeded("eb-1");
            },
        };

        await Create(store, publisher).ProcessBatchAsync(WorkerId, shutdown.Token);

        Assert.Equal([$"processed:{message.Id}:{WorkerId}:eb-1"], store.Calls);
        Assert.False(store.MarkProcessedTokenWasCancelled);
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private long _ticks;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => _ticks;

        public override DateTimeOffset GetUtcNow() => Now.AddTicks(_ticks);

        public void Advance(TimeSpan by) => _ticks += by.Ticks;
    }

    private sealed class FakeEventPublisher : IEventPublisher
    {
        public List<ClaimedOutboxMessage> Published { get; } = [];

        public Func<ClaimedOutboxMessage, PublishResult> Handler { get; init; } =
            _ => PublishResult.Succeeded("eb-default");

        public Task<PublishResult> PublishAsync(ClaimedOutboxMessage message, CancellationToken cancellationToken)
        {
            Published.Add(message);
            return Task.FromResult(Handler(message));
        }
    }

    private sealed class FakeStore : IOutboxStore
    {
        public IReadOnlyList<ClaimedOutboxMessage> Claimed { get; init; } = [];

        public bool TransitionResult { get; init; } = true;

        public List<string> Calls { get; } = [];

        public List<TimeSpan> RetryDelays { get; } = [];

        public string? ClaimedWorkerId { get; private set; }

        public int ClaimedBatchSize { get; private set; }

        public string? LastError { get; private set; }

        public string? LastRetryError { get; private set; }

        public bool MarkProcessedTokenWasCancelled { get; private set; }

        public Task<IReadOnlyList<ClaimedOutboxMessage>> ClaimAsync(
            string workerId, int batchSize, TimeSpan leaseDuration, CancellationToken cancellationToken)
        {
            ClaimedWorkerId = workerId;
            ClaimedBatchSize = batchSize;
            return Task.FromResult(Claimed);
        }

        public Task<bool> MarkProcessedAsync(Guid id, string workerId, string eventBridgeEventId, CancellationToken cancellationToken)
        {
            Calls.Add($"processed:{id}:{workerId}:{eventBridgeEventId}");
            MarkProcessedTokenWasCancelled = cancellationToken.IsCancellationRequested;
            return Task.FromResult(TransitionResult);
        }

        public Task<bool> ScheduleRetryAsync(Guid id, string workerId, TimeSpan delay, string errorMessage, CancellationToken cancellationToken)
        {
            Calls.Add($"retry:{id}:{workerId}:{delay}");
            RetryDelays.Add(delay);
            LastError = errorMessage;
            LastRetryError = errorMessage;
            return Task.FromResult(TransitionResult);
        }

        public Task<bool> ReleaseAsync(Guid id, string workerId, CancellationToken cancellationToken)
        {
            Calls.Add($"release:{id}:{workerId}");
            return Task.FromResult(TransitionResult);
        }

        public Task<bool> DeadLetterAsync(Guid id, string workerId, string errorMessage, CancellationToken cancellationToken)
        {
            Calls.Add($"deadletter:{id}:{workerId}");
            LastError = errorMessage;
            return Task.FromResult(TransitionResult);
        }

        public Task<int> DeleteProcessedBeforeAsync(DateTimeOffset cutoff, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class FakeLogger : ILogger<OutboxProcessor>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }
}
