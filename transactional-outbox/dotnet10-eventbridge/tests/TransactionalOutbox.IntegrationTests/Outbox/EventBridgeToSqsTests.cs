using System.Text.Json;
using Amazon.EventBridge;
using Amazon.Runtime;
using Amazon.SQS;
using Amazon.SQS.Model;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TransactionalOutbox.Infrastructure.Outbox;

namespace TransactionalOutbox.IntegrationTests.Outbox;

/// <summary>
/// Real-infrastructure tests: SQL Server plus LocalStack EventBridge routing to the SQS queue
/// created by localstack/ready.d. Defaults target the local compose stack.
/// </summary>
[Collection(InfrastructureTestGroup.Name)]
public sealed class EventBridgeToSqsTests(InfrastructureFixture fixture) : IAsyncLifetime, IDisposable
{
    private const string QueueName = "order-placed";
    private const string Region = "ap-southeast-2";

    private static readonly string Endpoint =
        Environment.GetEnvironmentVariable("TEST_AWS_SERVICE_URL") ?? "http://localhost:4566";

    private static readonly TimeSpan ReceiveDeadline = TimeSpan.FromSeconds(20);

    private readonly BasicAWSCredentials _credentials = new("test", "test");
    private AmazonSQSClient _sqs = null!;
    private AmazonEventBridgeClient _eventBridge = null!;
    private string _queueUrl = null!;

    public async Task InitializeAsync()
    {
        _sqs = new AmazonSQSClient(
            _credentials,
            new AmazonSQSConfig { ServiceURL = Endpoint, AuthenticationRegion = Region });
        _eventBridge = new AmazonEventBridgeClient(
            _credentials,
            new AmazonEventBridgeConfig { ServiceURL = Endpoint, AuthenticationRegion = Region });
        _queueUrl = (await _sqs.GetQueueUrlAsync(QueueName)).QueueUrl;
        await PurgeQueueAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    public void Dispose()
    {
        _sqs?.Dispose();
        _eventBridge?.Dispose();
    }

    [Fact]
    public async Task ProcessedOutboxMessageArrivesInSqsWithEnvelopeAndStableEventId()
    {
        await fixture.RecreateDatabaseAsync();
        var message = CreateMessage();
        await SeedAsync(message);

        await using var processContext = fixture.CreateContext();
        var processed = await CreateProcessor(new SqlServerOutboxStore(processContext))
            .ProcessBatchAsync("worker-1", CancellationToken.None);

        Assert.Equal(1, processed);
        var envelopes = await ReceiveAsync(expectedCount: 1);
        var envelope = Assert.Single(envelopes).RootElement;
        Assert.Equal("sample.orders", envelope.GetProperty("source").GetString());
        Assert.Equal("OrderPlaced", envelope.GetProperty("detail-type").GetString());
        Assert.Equal(
            message.Id.ToString(),
            envelope.GetProperty("detail").GetProperty("eventId").GetString());

        await using var context = fixture.CreateContext();
        var stored = await OutboxTestData.LoadAsync(context, message.Id);
        Assert.NotNull(stored.ProcessedOnUtc);
        Assert.False(string.IsNullOrEmpty(stored.EventBridgeEventId));
        Assert.Equal(1, stored.AttemptCount);
        Assert.Null(stored.LockedBy);
    }

    // The broker accepts the event, then the process dies before the success update reaches SQL.
    // The lease later expires and another worker republishes the same outbox message, so the
    // consumer sees the same event ID twice. Delivery is at-least-once by design: consumers
    // deduplicate on the event ID, and this duplicate is expected, not a defect to be fixed.
    [Fact]
    public async Task CrashBetweenPublishAndSuccessUpdateCausesDuplicateWithSameEventId()
    {
        await fixture.RecreateDatabaseAsync();
        var message = CreateMessage();
        await SeedAsync(message);

        // Real publisher, real store underneath; every state transition throws as if the process
        // had died right after PutEvents returned. The exception is not a publish failure, so it
        // escapes ProcessBatchAsync without scheduling a retry.
        await using (var crashingContext = fixture.CreateContext())
        {
            var crashingProcessor = CreateProcessor(
                new ProcessDeathStore(new SqlServerOutboxStore(crashingContext)));
            await Assert.ThrowsAsync<ProcessDiedException>(() =>
                crashingProcessor.ProcessBatchAsync("worker-crashed", CancellationToken.None));
        }

        await using (var afterCrash = fixture.CreateContext())
        {
            var stored = await OutboxTestData.LoadAsync(afterCrash, message.Id);
            Assert.Null(stored.ProcessedOnUtc);
            Assert.Equal("worker-crashed", stored.LockedBy);
            await OutboxTestData.ExpireLeaseAsync(afterCrash, message.Id);
        }

        await using var recoveryContext = fixture.CreateContext();
        var processed = await CreateProcessor(new SqlServerOutboxStore(recoveryContext))
            .ProcessBatchAsync("worker-recovery", CancellationToken.None);

        Assert.Equal(1, processed);
        var envelopes = await ReceiveAsync(expectedCount: 2);
        Assert.Equal(2, envelopes.Count);
        Assert.All(envelopes, envelope => Assert.Equal(
            message.Id.ToString(),
            envelope.RootElement.GetProperty("detail").GetProperty("eventId").GetString()));

        await using var context = fixture.CreateContext();
        var final = await OutboxTestData.LoadAsync(context, message.Id);
        Assert.NotNull(final.ProcessedOnUtc);
        Assert.False(string.IsNullOrEmpty(final.EventBridgeEventId));
        Assert.Equal(2, final.AttemptCount);
    }

    private static OutboxMessage CreateMessage()
    {
        var id = Guid.NewGuid();
        return new OutboxMessage
        {
            Id = id,
            EventType = "orders.order-placed",
            SchemaVersion = 1,
            Payload = JsonSerializer.Serialize(new { eventId = id, orderId = Guid.NewGuid() }),
            OccurredOnUtc = DateTimeOffset.UtcNow.AddMinutes(-10),
            NextAttemptOnUtc = DateTimeOffset.UtcNow.AddMinutes(-10),
        };
    }

    private async Task SeedAsync(OutboxMessage message)
    {
        await using var context = fixture.CreateContext();
        await OutboxTestData.SeedAsync(context, message);
    }

    private OutboxProcessor CreateProcessor(IOutboxStore store)
    {
        var outboxOptions = Options.Create(new OutboxOptions { BatchSize = 10 });
        var eventBridgeOptions = Options.Create(new EventBridgeOptions());
        var publisher = new EventBridgePublisher(_eventBridge, eventBridgeOptions);
        return new OutboxProcessor(
            store,
            publisher,
            new RetrySchedule(outboxOptions),
            outboxOptions,
            eventBridgeOptions,
            TimeProvider.System,
            NullLogger<OutboxProcessor>.Instance);
    }

    private async Task PurgeQueueAsync()
    {
        await _sqs.PurgeQueueAsync(new PurgeQueueRequest { QueueUrl = _queueUrl });
        // Drain anything a previous test's routed event delivered after the purge ran.
        while ((await _sqs.ReceiveMessageAsync(new ReceiveMessageRequest
        {
            QueueUrl = _queueUrl,
            MaxNumberOfMessages = 10,
            WaitTimeSeconds = 0,
        })).Messages is { Count: > 0 } leftovers)
        {
            foreach (var leftover in leftovers)
            {
                await _sqs.DeleteMessageAsync(_queueUrl, leftover.ReceiptHandle);
            }
        }
    }

    private async Task<List<JsonDocument>> ReceiveAsync(int expectedCount)
    {
        var envelopes = new List<JsonDocument>();
        var deadline = DateTimeOffset.UtcNow + ReceiveDeadline;
        while (envelopes.Count < expectedCount && DateTimeOffset.UtcNow < deadline)
        {
            var response = await _sqs.ReceiveMessageAsync(new ReceiveMessageRequest
            {
                QueueUrl = _queueUrl,
                MaxNumberOfMessages = 10,
                WaitTimeSeconds = 5,
            });
            foreach (var received in response.Messages ?? [])
            {
                envelopes.Add(JsonDocument.Parse(received.Body));
                await _sqs.DeleteMessageAsync(_queueUrl, received.ReceiptHandle);
            }
        }

        Assert.True(
            envelopes.Count >= expectedCount,
            $"Expected {expectedCount} SQS message(s) within {ReceiveDeadline.TotalSeconds}s, got {envelopes.Count}.");
        return envelopes;
    }

    private sealed class ProcessDiedException : Exception;

    private sealed class ProcessDeathStore(IOutboxStore inner) : IOutboxStore
    {
        public Task<IReadOnlyList<ClaimedOutboxMessage>> ClaimAsync(
            string workerId, int batchSize, TimeSpan leaseDuration, CancellationToken cancellationToken) =>
            inner.ClaimAsync(workerId, batchSize, leaseDuration, cancellationToken);

        public Task<bool> MarkProcessedAsync(
            Guid id, string workerId, string eventBridgeEventId, CancellationToken cancellationToken) =>
            throw new ProcessDiedException();

        public Task<bool> ScheduleRetryAsync(
            Guid id, string workerId, TimeSpan delay, string errorMessage, CancellationToken cancellationToken) =>
            throw new ProcessDiedException();

        public Task<bool> ReleaseAsync(Guid id, string workerId, CancellationToken cancellationToken) =>
            throw new ProcessDiedException();

        public Task<bool> DeadLetterAsync(
            Guid id, string workerId, string errorMessage, CancellationToken cancellationToken) =>
            throw new ProcessDiedException();

        public Task<int> DeleteProcessedBeforeAsync(DateTimeOffset cutoff, CancellationToken cancellationToken) =>
            inner.DeleteProcessedBeforeAsync(cutoff, cancellationToken);
    }
}
