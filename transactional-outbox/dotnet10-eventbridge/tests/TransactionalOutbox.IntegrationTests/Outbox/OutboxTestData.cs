using Microsoft.EntityFrameworkCore;
using TransactionalOutbox.Infrastructure.Outbox;
using TransactionalOutbox.Infrastructure.Persistence;

namespace TransactionalOutbox.IntegrationTests.Outbox;

internal static class OutboxTestData
{
    // The store compares against SQL Server's clock, so seeded times are minutes away from "now"
    // to keep the tests independent of sub-second clock agreement between host and database.
    public static OutboxMessage CreateDue(Guid? id = null, int minutesAgo = 10) => new()
    {
        Id = id ?? Guid.NewGuid(),
        EventType = "orders.order-placed",
        SchemaVersion = 1,
        Payload = "{}",
        OccurredOnUtc = DateTimeOffset.UtcNow.AddMinutes(-minutesAgo),
        NextAttemptOnUtc = DateTimeOffset.UtcNow.AddMinutes(-minutesAgo)
    };

    public static async Task SeedAsync(AppDbContext context, params OutboxMessage[] messages)
    {
        context.OutboxMessages.AddRange(messages);
        await context.SaveChangesAsync(CancellationToken.None);
    }

    public static Task<OutboxMessage> LoadAsync(AppDbContext context, Guid id) =>
        context.OutboxMessages.AsNoTracking().SingleAsync(m => m.Id == id);

    // Simulates lease expiry using the database clock, exactly as the claim query compares it.
    public static async Task ExpireLeaseAsync(AppDbContext context, Guid id) =>
        await context.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE dbo.OutboxMessages SET LockedUntilUtc = DATEADD(minute, -1, SYSUTCDATETIME()) WHERE Id = {id}");
}
