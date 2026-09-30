using TransactionalOutbox.Infrastructure.Outbox;

namespace TransactionalOutbox.IntegrationTests.Outbox;

[Collection(InfrastructureTestGroup.Name)]
public sealed class LeaseAndStateTransitionTests(InfrastructureFixture fixture)
{
    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(5);

    [Fact]
    public async Task UnexpiredLeaseCannotBeClaimedByAnotherWorker()
    {
        await fixture.RecreateDatabaseAsync();
        var message = OutboxTestData.CreateDue();
        message.LockedBy = "worker-1";
        message.LockedUntilUtc = DateTimeOffset.UtcNow.AddMinutes(10);
        await SeedAsync(message);

        var claimed = await ClaimAsync("worker-2");

        Assert.Empty(claimed);
    }

    [Fact]
    public async Task ExpiredLeaseCanBeClaimedByAnotherWorker()
    {
        await fixture.RecreateDatabaseAsync();
        var message = OutboxTestData.CreateDue();
        message.LockedBy = "worker-1";
        message.LockedUntilUtc = DateTimeOffset.UtcNow.AddMinutes(-2);
        message.AttemptCount = 1;
        await SeedAsync(message);

        var claimed = await ClaimAsync("worker-2");

        var item = Assert.Single(claimed);
        Assert.Equal(message.Id, item.Id);
        Assert.Equal(2, item.AttemptCount);
        await using var context = fixture.CreateContext();
        var stored = await OutboxTestData.LoadAsync(context, message.Id);
        Assert.Equal("worker-2", stored.LockedBy);
        Assert.True(stored.LockedUntilUtc > DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task MarkProcessedSucceedsForLeaseOwnerAndClearsLease()
    {
        await fixture.RecreateDatabaseAsync();
        var message = OutboxTestData.CreateDue();
        await SeedAsync(message);
        await ClaimAsync("worker-1");

        var updated = await MarkProcessedAsync(message.Id, "worker-1", "eb-123");

        Assert.True(updated);
        await using var context = fixture.CreateContext();
        var stored = await OutboxTestData.LoadAsync(context, message.Id);
        Assert.NotNull(stored.ProcessedOnUtc);
        Assert.Equal("eb-123", stored.EventBridgeEventId);
        Assert.Null(stored.LockedBy);
        Assert.Null(stored.LockedUntilUtc);
    }

    [Fact]
    public async Task MarkProcessedReturnsFalseForStaleWorkerAndLeavesRowUntouched()
    {
        await fixture.RecreateDatabaseAsync();
        var message = OutboxTestData.CreateDue();
        await SeedAsync(message);
        await ClaimAsync("worker-1");

        var updated = await MarkProcessedAsync(message.Id, "worker-2", "eb-123");

        Assert.False(updated);
        await using var context = fixture.CreateContext();
        var stored = await OutboxTestData.LoadAsync(context, message.Id);
        Assert.Null(stored.ProcessedOnUtc);
        Assert.Equal("worker-1", stored.LockedBy);
    }

    [Fact]
    public async Task ScheduleRetryAndDeadLetterReturnFalseForStaleWorker()
    {
        await fixture.RecreateDatabaseAsync();
        var message = OutboxTestData.CreateDue();
        await SeedAsync(message);
        await ClaimAsync("worker-1");

        await using var context = fixture.CreateContext();
        var store = new SqlServerOutboxStore(context);

        Assert.False(await store.ScheduleRetryAsync(
            message.Id, "worker-2", DateTimeOffset.UtcNow.AddMinutes(1), "boom", CancellationToken.None));
        Assert.False(await store.DeadLetterAsync(
            message.Id, "worker-2", "boom", CancellationToken.None));

        var stored = await OutboxTestData.LoadAsync(context, message.Id);
        Assert.Null(stored.DeadLetteredOnUtc);
        Assert.Null(stored.LastError);
        Assert.Equal("worker-1", stored.LockedBy);
    }

    [Fact]
    public async Task ScheduleRetryRecordsTruncatedErrorAndReleasesLease()
    {
        await fixture.RecreateDatabaseAsync();
        var message = OutboxTestData.CreateDue();
        await SeedAsync(message);
        await ClaimAsync("worker-1");
        var nextAttempt = DateTimeOffset.UtcNow.AddMinutes(3);

        await using var context = fixture.CreateContext();
        var store = new SqlServerOutboxStore(context);
        var updated = await store.ScheduleRetryAsync(
            message.Id, "worker-1", nextAttempt, new string('x', 5000), CancellationToken.None);

        Assert.True(updated);
        var stored = await OutboxTestData.LoadAsync(context, message.Id);
        Assert.Equal(2048, stored.LastError!.Length);
        Assert.Equal(nextAttempt, stored.NextAttemptOnUtc, TimeSpan.FromMilliseconds(1));
        Assert.Null(stored.LockedBy);
        Assert.Null(stored.LockedUntilUtc);
        Assert.Null(stored.DeadLetteredOnUtc);
        Assert.Empty(await ClaimAsync("worker-2"));
    }

    [Fact]
    public async Task DeadLetterMarksRowAndExcludesItFromFutureClaims()
    {
        await fixture.RecreateDatabaseAsync();
        var message = OutboxTestData.CreateDue();
        await SeedAsync(message);
        await ClaimAsync("worker-1");

        await using var context = fixture.CreateContext();
        var store = new SqlServerOutboxStore(context);
        var updated = await store.DeadLetterAsync(
            message.Id, "worker-1", "permanent failure", CancellationToken.None);

        Assert.True(updated);
        var stored = await OutboxTestData.LoadAsync(context, message.Id);
        Assert.NotNull(stored.DeadLetteredOnUtc);
        Assert.Equal("permanent failure", stored.LastError);
        Assert.Null(stored.LockedBy);
        Assert.Null(stored.LockedUntilUtc);
        Assert.Empty(await ClaimAsync("worker-2"));
    }

    [Fact]
    public async Task DeleteProcessedBeforeRemovesOnlyOldProcessedRows()
    {
        await fixture.RecreateDatabaseAsync();
        var oldProcessed = OutboxTestData.CreateDue();
        oldProcessed.ProcessedOnUtc = DateTimeOffset.UtcNow.AddDays(-10);
        var recentProcessed = OutboxTestData.CreateDue();
        recentProcessed.ProcessedOnUtc = DateTimeOffset.UtcNow.AddMinutes(-1);
        var pending = OutboxTestData.CreateDue();
        await SeedAsync(oldProcessed, recentProcessed, pending);

        await using var context = fixture.CreateContext();
        var store = new SqlServerOutboxStore(context);
        var deleted = await store.DeleteProcessedBeforeAsync(
            DateTimeOffset.UtcNow.AddDays(-1), CancellationToken.None);

        Assert.Equal(1, deleted);
        var remaining = context.OutboxMessages.Select(m => m.Id).ToHashSet();
        Assert.DoesNotContain(oldProcessed.Id, remaining);
        Assert.Contains(recentProcessed.Id, remaining);
        Assert.Contains(pending.Id, remaining);
    }

    private async Task SeedAsync(params OutboxMessage[] messages)
    {
        await using var context = fixture.CreateContext();
        await OutboxTestData.SeedAsync(context, messages);
    }

    private async Task<IReadOnlyList<ClaimedOutboxMessage>> ClaimAsync(string workerId)
    {
        await using var context = fixture.CreateContext();
        return await new SqlServerOutboxStore(context)
            .ClaimAsync(workerId, 10, Lease, CancellationToken.None);
    }

    private async Task<bool> MarkProcessedAsync(Guid id, string workerId, string eventId)
    {
        await using var context = fixture.CreateContext();
        return await new SqlServerOutboxStore(context)
            .MarkProcessedAsync(id, workerId, eventId, CancellationToken.None);
    }
}
