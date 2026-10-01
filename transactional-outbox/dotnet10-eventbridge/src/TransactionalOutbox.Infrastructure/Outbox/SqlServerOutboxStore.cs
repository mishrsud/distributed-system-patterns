using System.Data;
using System.Data.Common;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using TransactionalOutbox.Infrastructure.Persistence;

namespace TransactionalOutbox.Infrastructure.Outbox;

public sealed class SqlServerOutboxStore(AppDbContext context) : IOutboxStore
{
    private const int MaxErrorLength = 2048;

    private const string ClaimSql = """
        ;WITH claimable AS
        (
            SELECT TOP (@BatchSize) *
            FROM dbo.OutboxMessages WITH (UPDLOCK, READPAST, ROWLOCK)
            WHERE ProcessedOnUtc IS NULL
              AND DeadLetteredOnUtc IS NULL
              AND NextAttemptOnUtc <= SYSUTCDATETIME()
              AND (LockedUntilUtc IS NULL OR LockedUntilUtc < SYSUTCDATETIME())
            ORDER BY OccurredOnUtc, Id
        )
        UPDATE claimable
        SET LockedBy = @WorkerId,
            LockedUntilUtc = DATEADD(second, @LeaseSeconds, SYSUTCDATETIME()),
            AttemptCount = AttemptCount + 1
        OUTPUT INSERTED.Id, INSERTED.EventType, INSERTED.SchemaVersion,
               INSERTED.Payload, INSERTED.OccurredOnUtc, INSERTED.AttemptCount;
        """;

    public async Task<IReadOnlyList<ClaimedOutboxMessage>> ClaimAsync(
        string workerId,
        int batchSize,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workerId);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(batchSize, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(leaseDuration, TimeSpan.Zero);

        var leaseSeconds = (int)Math.Ceiling(leaseDuration.TotalSeconds);

        // The user-initiated transaction must run inside the execution strategy so it stays
        // valid when the context is configured with EnableRetryOnFailure. Each attempt builds a
        // fresh list, so a retry never returns rows from a rolled-back claim.
        var strategy = context.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await context.Database
                .BeginTransactionAsync(cancellationToken);
            var claimed = new List<ClaimedOutboxMessage>();

            await using (var command = context.Database.GetDbConnection().CreateCommand())
            {
                command.Transaction = transaction.GetDbTransaction();
                command.CommandText = ClaimSql;
                command.Parameters.Add(new SqlParameter("@BatchSize", SqlDbType.Int) { Value = batchSize });
                command.Parameters.Add(new SqlParameter("@WorkerId", SqlDbType.NVarChar, 200) { Value = workerId });
                command.Parameters.Add(new SqlParameter("@LeaseSeconds", SqlDbType.Int) { Value = leaseSeconds });

                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    claimed.Add(Map(reader));
                }
            }

            await transaction.CommitAsync(cancellationToken);
            return (IReadOnlyList<ClaimedOutboxMessage>)claimed;
        });
    }

    public async Task<bool> MarkProcessedAsync(
        Guid id,
        string workerId,
        string eventBridgeEventId,
        CancellationToken cancellationToken)
    {
        var rows = await context.Database.ExecuteSqlInterpolatedAsync(
            $"""
            UPDATE dbo.OutboxMessages
            SET ProcessedOnUtc = SYSUTCDATETIME(),
                EventBridgeEventId = {eventBridgeEventId},
                LastError = NULL,
                LockedBy = NULL,
                LockedUntilUtc = NULL
            WHERE Id = {id}
              AND LockedBy = {workerId}
              AND ProcessedOnUtc IS NULL
              AND DeadLetteredOnUtc IS NULL
            """,
            cancellationToken);
        return rows == 1;
    }

    public async Task<bool> ScheduleRetryAsync(
        Guid id,
        string workerId,
        TimeSpan delay,
        string errorMessage,
        CancellationToken cancellationToken)
    {
        var storedError = Truncate(errorMessage);
        var delayMs = (int)Math.Clamp(delay.TotalMilliseconds, 0, int.MaxValue);
        var rows = await context.Database.ExecuteSqlInterpolatedAsync(
            $"""
            UPDATE dbo.OutboxMessages
            SET NextAttemptOnUtc = DATEADD(millisecond, {delayMs}, SYSUTCDATETIME()),
                LastError = {storedError},
                LockedBy = NULL,
                LockedUntilUtc = NULL
            WHERE Id = {id}
              AND LockedBy = {workerId}
              AND ProcessedOnUtc IS NULL
              AND DeadLetteredOnUtc IS NULL
            """,
            cancellationToken);
        return rows == 1;
    }

    public async Task<bool> DeadLetterAsync(
        Guid id,
        string workerId,
        string errorMessage,
        CancellationToken cancellationToken)
    {
        var storedError = Truncate(errorMessage);
        var rows = await context.Database.ExecuteSqlInterpolatedAsync(
            $"""
            UPDATE dbo.OutboxMessages
            SET DeadLetteredOnUtc = SYSUTCDATETIME(),
                LastError = {storedError},
                LockedBy = NULL,
                LockedUntilUtc = NULL
            WHERE Id = {id}
              AND LockedBy = {workerId}
              AND ProcessedOnUtc IS NULL
              AND DeadLetteredOnUtc IS NULL
            """,
            cancellationToken);
        return rows == 1;
    }

    public Task<int> DeleteProcessedBeforeAsync(
        DateTimeOffset cutoff,
        CancellationToken cancellationToken) =>
        context.Database.ExecuteSqlInterpolatedAsync(
            $"""
            DELETE FROM dbo.OutboxMessages
            WHERE ProcessedOnUtc IS NOT NULL
              AND ProcessedOnUtc < {cutoff}
            """,
            cancellationToken);

    private static string Truncate(string error) =>
        error.Length <= MaxErrorLength ? error : error[..MaxErrorLength];

    private static ClaimedOutboxMessage Map(DbDataReader reader) => new(
        reader.GetGuid(0),
        reader.GetString(1),
        reader.GetInt32(2),
        reader.GetString(3),
        reader.GetFieldValue<DateTimeOffset>(4),
        reader.GetInt32(5));
}
