using Amazon;
using Amazon.EventBridge;
using Amazon.EventBridge.Model;
using Amazon.Runtime;
using Microsoft.Extensions.Options;
using TransactionalOutbox.Infrastructure.Outbox;

namespace TransactionalOutbox.UnitTests.Outbox;

public sealed class EventBridgePublisherTests
{
    private const string Payload =
        """{"eventId":"11111111-1111-1111-1111-111111111111","correlationId":"22222222-2222-2222-2222-222222222222","schemaVersion":1,"occurredOnUtc":"2026-01-01T00:00:00+00:00","orderId":"33333333-3333-3333-3333-333333333333","customerId":"cust-1","totalAmount":12.50,"currency":"AUD"}""";

    private static readonly ClaimedOutboxMessage Message = new(
        Guid.Parse("11111111-1111-1111-1111-111111111111"),
        "OrderPlaced",
        1,
        Payload,
        DateTimeOffset.Parse("2026-01-01T00:00:00+00:00", System.Globalization.CultureInfo.InvariantCulture),
        1);

    private static EventBridgePublisher Create(FakeEventBridge fake) =>
        new(fake, Options.Create(new EventBridgeOptions()));

    private static PutEventsResponse Response(int failed, params PutEventsResultEntry[] entries) =>
        new() { FailedEntryCount = failed, Entries = [.. entries] };

    [Fact]
    public async Task SuccessWhenSingleEntryHasEventIdAndNoError()
    {
        var fake = new FakeEventBridge { PutEvents = _ => Response(0, new PutEventsResultEntry { EventId = "eb-1" }) };

        PublishResult result = await Create(fake).PublishAsync(Message, CancellationToken.None);

        Assert.Equal(PublishOutcome.Success, result.Outcome);
        Assert.Equal("eb-1", result.EventBridgeEventId);
        Assert.Null(result.ErrorCode);
    }

    [Fact]
    public async Task RequestUsesConfiguredFieldsAndStoredPayloadVerbatim()
    {
        PutEventsRequest? captured = null;
        var fake = new FakeEventBridge
        {
            PutEvents = r =>
            {
                captured = r;
                return Response(0, new PutEventsResultEntry { EventId = "eb-1" });
            },
        };

        await Create(fake).PublishAsync(Message, CancellationToken.None);

        PutEventsRequestEntry entry = Assert.Single(Assert.IsType<PutEventsRequest>(captured).Entries);
        Assert.Equal("orders", entry.EventBusName);
        Assert.Equal("sample.orders", entry.Source);
        Assert.Equal("OrderPlaced", entry.DetailType);
        Assert.Equal(Payload, entry.Detail);
    }

    [Theory]
    [InlineData("InternalFailure")]
    [InlineData("ThrottlingException")]
    [InlineData("SomethingNew")]
    public async Task RetryableEntryErrorCodes(string code)
    {
        var fake = new FakeEventBridge
        {
            PutEvents = _ => Response(1, new PutEventsResultEntry { ErrorCode = code, ErrorMessage = "Rate exceeded" }),
        };

        PublishResult result = await Create(fake).PublishAsync(Message, CancellationToken.None);

        Assert.Equal(PublishOutcome.RetryableFailure, result.Outcome);
        Assert.Equal(code, result.ErrorCode);
        Assert.Equal($"{code}: Rate exceeded", result.ErrorSummary);
        Assert.Null(result.EventBridgeEventId);
    }

    [Theory]
    [InlineData("AccessDeniedException")]
    [InlineData("InvalidArgument")]
    [InlineData("MalformedDetail")]
    public async Task PermanentEntryErrorCodes(string code)
    {
        var fake = new FakeEventBridge
        {
            PutEvents = _ => Response(1, new PutEventsResultEntry { ErrorCode = code, ErrorMessage = "nope" }),
        };

        PublishResult result = await Create(fake).PublishAsync(Message, CancellationToken.None);

        Assert.Equal(PublishOutcome.PermanentFailure, result.Outcome);
        Assert.Equal(code, result.ErrorCode);
    }

    [Fact]
    public async Task NullResponseIsRetryable()
    {
        var fake = new FakeEventBridge { PutEvents = _ => null! };
        PublishResult result = await Create(fake).PublishAsync(Message, CancellationToken.None);
        Assert.Equal(PublishOutcome.RetryableFailure, result.Outcome);
    }

    [Fact]
    public async Task NullEntriesAreRetryable()
    {
        var fake = new FakeEventBridge { PutEvents = _ => new PutEventsResponse { FailedEntryCount = 0 } };
        PublishResult result = await Create(fake).PublishAsync(Message, CancellationToken.None);
        Assert.Equal(PublishOutcome.RetryableFailure, result.Outcome);
    }

    [Fact]
    public async Task EmptyEntriesAreRetryable()
    {
        var fake = new FakeEventBridge { PutEvents = _ => Response(0) };
        PublishResult result = await Create(fake).PublishAsync(Message, CancellationToken.None);
        Assert.Equal(PublishOutcome.RetryableFailure, result.Outcome);
    }

    [Fact]
    public async Task MissingEventIdIsRetryable()
    {
        var fake = new FakeEventBridge { PutEvents = _ => Response(0, new PutEventsResultEntry { EventId = "" }) };
        PublishResult result = await Create(fake).PublishAsync(Message, CancellationToken.None);
        Assert.Equal(PublishOutcome.RetryableFailure, result.Outcome);
    }

    [Fact]
    public async Task FailedEntryCountWithoutErrorCodeIsRetryable()
    {
        var fake = new FakeEventBridge { PutEvents = _ => Response(1, new PutEventsResultEntry { EventId = "eb-1" }) };
        PublishResult result = await Create(fake).PublishAsync(Message, CancellationToken.None);
        Assert.Equal(PublishOutcome.RetryableFailure, result.Outcome);
    }

    [Fact]
    public async Task NullFailedEntryCountIsRetryable()
    {
        var fake = new FakeEventBridge
        {
            PutEvents = _ => new PutEventsResponse
            {
                FailedEntryCount = null,
                Entries = [new PutEventsResultEntry { EventId = "eb-1" }],
            },
        };

        PublishResult result = await Create(fake).PublishAsync(Message, CancellationToken.None);

        Assert.Equal(PublishOutcome.RetryableFailure, result.Outcome);
        Assert.Null(result.EventBridgeEventId);
    }

    [Theory]
    [InlineData(typeof(OperationCanceledException))]
    [InlineData(typeof(TaskCanceledException))]
    public async Task ClientCancellationWithoutSuppliedTokenCancelledIsRetryable(Type exceptionType)
    {
        var fake = new FakeEventBridge { PutEvents = _ => throw (Exception)Activator.CreateInstance(exceptionType)! };

        PublishResult result = await Create(fake).PublishAsync(Message, CancellationToken.None);

        Assert.Equal(PublishOutcome.RetryableFailure, result.Outcome);
        Assert.Equal(exceptionType.Name, result.ErrorCode);
    }

    [Fact]
    public async Task ServiceExceptionIsRetryable()
    {
        var fake = new FakeEventBridge
        {
            PutEvents = _ => throw new AmazonEventBridgeException("Rate exceeded") { ErrorCode = "ThrottlingException" },
        };

        PublishResult result = await Create(fake).PublishAsync(Message, CancellationToken.None);

        Assert.Equal(PublishOutcome.RetryableFailure, result.Outcome);
        Assert.Equal("ThrottlingException", result.ErrorCode);
        Assert.Equal("ThrottlingException: Rate exceeded", result.ErrorSummary);
    }

    [Fact]
    public async Task TransportExceptionIsRetryable()
    {
        var fake = new FakeEventBridge { PutEvents = _ => throw new HttpRequestException("connection refused") };

        PublishResult result = await Create(fake).PublishAsync(Message, CancellationToken.None);

        Assert.Equal(PublishOutcome.RetryableFailure, result.Outcome);
        Assert.Equal("HttpRequestException", result.ErrorCode);
        Assert.Contains("connection refused", result.ErrorSummary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CancellationOfSuppliedTokenPropagates()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var fake = new FakeEventBridge { PutEvents = _ => throw new OperationCanceledException(cts.Token) };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Create(fake).PublishAsync(Message, cts.Token));
    }

    [Fact]
    public async Task StartupValidatorSucceedsWhenBusExists()
    {
        DescribeEventBusRequest? captured = null;
        var fake = new FakeEventBridge
        {
            Describe = r =>
            {
                captured = r;
                return new DescribeEventBusResponse { Name = r.Name };
            },
        };

        await new EventBridgeStartupValidator(fake, Options.Create(new EventBridgeOptions()))
            .StartAsync(CancellationToken.None);

        Assert.Equal("orders", Assert.IsType<DescribeEventBusRequest>(captured).Name);
    }

    [Fact]
    public async Task StartupValidatorThrowsWithBusNameWhenBusMissing()
    {
        var fake = new FakeEventBridge { Describe = _ => throw new ResourceNotFoundException("no bus") };
        var validator = new EventBridgeStartupValidator(
            fake, Options.Create(new EventBridgeOptions { EventBusName = "missing-bus" }));

        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => validator.StartAsync(CancellationToken.None));

        Assert.Contains("missing-bus", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StartupValidatorThrowsWithBusNameOnTransportFailure()
    {
        var fake = new FakeEventBridge { Describe = _ => throw new HttpRequestException("down") };
        var validator = new EventBridgeStartupValidator(
            fake, Options.Create(new EventBridgeOptions { EventBusName = "flaky-bus" }));

        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => validator.StartAsync(CancellationToken.None));

        Assert.Contains("flaky-bus", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StartupValidatorPropagatesCancellation()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var fake = new FakeEventBridge { Describe = _ => throw new OperationCanceledException(cts.Token) };
        var validator = new EventBridgeStartupValidator(fake, Options.Create(new EventBridgeOptions()));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => validator.StartAsync(cts.Token));
    }

    private sealed class FakeEventBridge : AmazonEventBridgeClient
    {
        public FakeEventBridge()
            : base(new AnonymousAWSCredentials(), new AmazonEventBridgeConfig { RegionEndpoint = RegionEndpoint.USEast1 })
        {
        }

        public Func<PutEventsRequest, PutEventsResponse> PutEvents { get; set; } =
            _ => throw new InvalidOperationException("PutEvents not configured");

        public Func<DescribeEventBusRequest, DescribeEventBusResponse> Describe { get; set; } =
            _ => throw new InvalidOperationException("Describe not configured");

        public override Task<PutEventsResponse> PutEventsAsync(
            PutEventsRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(PutEvents(request));

        public override Task<DescribeEventBusResponse> DescribeEventBusAsync(
            DescribeEventBusRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(Describe(request));
    }
}
