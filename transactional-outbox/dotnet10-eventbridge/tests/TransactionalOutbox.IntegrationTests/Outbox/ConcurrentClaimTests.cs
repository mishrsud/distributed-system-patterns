using TransactionalOutbox.Infrastructure.Outbox;

namespace TransactionalOutbox.IntegrationTests.Outbox;

[Collection(InfrastructureTestGroup.Name)]
public sealed class ConcurrentClaimTests(InfrastructureFixture fixture)
{
    private const int BatchSize = 10;

    [Fact]
    public async Task TwoWorkersClaimDisjointBatchesFromTheSameDueRows()
    {
        await fixture.RecreateDatabaseAsync();
        await using (var seedContext = fixture.CreateContext())
        {
            await OutboxTestData.SeedAsync(
                seedContext,
                [.. Enumerable.Range(0, 20).Select(i => OutboxTestData.CreateDue(minutesAgo: 60 - i))]);
        }

        using var barrier = new Barrier(2);
        var first = ClaimOnOwnContextAsync("worker-1", barrier);
        var second = ClaimOnOwnContextAsync("worker-2", barrier);
        var results = await Task.WhenAll(first, second);

        var firstIds = results[0].Select(x => x.Id).ToList();
        var secondIds = results[1].Select(x => x.Id).ToList();
        Assert.Equal(10, firstIds.Count);
        Assert.Equal(10, secondIds.Count);
        Assert.Equal(20, firstIds.Concat(secondIds).Distinct().Count());
        Assert.Empty(firstIds.Intersect(secondIds));
        Assert.All(results.SelectMany(r => r), m => Assert.Equal(1, m.AttemptCount));
    }

    // With UPDLOCK + READPAST a TOP (n) claim that needs a sort can lock every candidate row while
    // it scans, so a racing worker may legitimately see an empty batch and simply polls again.
    // Each worker therefore tops itself up until it holds its share; the claimed sets must still
    // be disjoint however the race resolves.
    private Task<List<ClaimedOutboxMessage>> ClaimOnOwnContextAsync(
        string workerId,
        Barrier barrier) =>
        Task.Run(async () =>
        {
            await using var context = fixture.CreateContext();
            var store = new SqlServerOutboxStore(context);
            var claimed = new List<ClaimedOutboxMessage>();
            barrier.SignalAndWait();
            for (var attempt = 0; attempt < 50 && claimed.Count < BatchSize; attempt++)
            {
                claimed.AddRange(await store.ClaimAsync(
                    workerId,
                    BatchSize - claimed.Count,
                    TimeSpan.FromMinutes(5),
                    CancellationToken.None));
            }

            return claimed;
        });
}
