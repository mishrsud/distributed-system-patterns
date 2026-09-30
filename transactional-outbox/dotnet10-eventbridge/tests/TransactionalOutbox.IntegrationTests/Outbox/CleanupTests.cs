using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TransactionalOutbox.Infrastructure.Outbox;
using TransactionalOutbox.Infrastructure.Persistence;

namespace TransactionalOutbox.IntegrationTests.Outbox;

[Collection(InfrastructureTestGroup.Name)]
public sealed class CleanupTests(InfrastructureFixture fixture)
{
    [Fact]
    public async Task CleanupServiceDeletesOnlyProcessedRowsOlderThanRetention()
    {
        await fixture.RecreateDatabaseAsync();
        var oldProcessed = Processed(TimeSpan.FromDays(10));
        var newProcessed = Processed(TimeSpan.FromHours(1));
        var pending = OutboxTestData.CreateDue();
        var deadLettered = OutboxTestData.CreateDue();
        deadLettered.DeadLetteredOnUtc = DateTimeOffset.UtcNow.AddDays(-30);
        await using (var seed = fixture.CreateContext())
        {
            await OutboxTestData.SeedAsync(seed, oldProcessed, newProcessed, pending, deadLettered);
        }

        var options = Options.Create(new OutboxOptions
        {
            ProcessedRetention = TimeSpan.FromDays(7),
            CleanupInterval = TimeSpan.FromHours(1),
        });
        var services = new ServiceCollection();
        services.AddScoped<AppDbContext>(_ => fixture.CreateContext());
        services.AddScoped<IOutboxStore, SqlServerOutboxStore>();
        await using ServiceProvider provider = services.BuildServiceProvider();
        var service = new OutboxCleanupService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            options,
            TimeProvider.System,
            NullLogger<OutboxCleanupService>.Instance);

        await service.StartAsync(CancellationToken.None);
        try
        {
            await WaitForAsync(async () =>
            {
                await using var context = fixture.CreateContext();
                return !await context.OutboxMessages.AnyAsync(m => m.Id == oldProcessed.Id);
            });
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }

        await using var verify = fixture.CreateContext();
        var remaining = await verify.OutboxMessages.AsNoTracking().Select(m => m.Id).ToListAsync();
        Assert.DoesNotContain(oldProcessed.Id, remaining);
        Assert.Contains(newProcessed.Id, remaining);
        Assert.Contains(pending.Id, remaining);
        Assert.Contains(deadLettered.Id, remaining);
    }

    private static OutboxMessage Processed(TimeSpan age)
    {
        var message = OutboxTestData.CreateDue();
        message.ProcessedOnUtc = DateTimeOffset.UtcNow - age;
        message.EventBridgeEventId = "eb-1";
        return message;
    }

    private static async Task WaitForAsync(Func<Task<bool>> condition)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
        while (!await condition())
        {
            Assert.True(DateTimeOffset.UtcNow < deadline, "Cleanup did not run within the timeout.");
            await Task.Delay(100);
        }
    }
}
