using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TransactionalOutbox.Infrastructure.Outbox;

namespace TransactionalOutbox.Infrastructure.Persistence.Configurations;

public sealed class OutboxMessageConfiguration : IEntityTypeConfiguration<OutboxMessage>
{
    public void Configure(EntityTypeBuilder<OutboxMessage> builder)
    {
        builder.ToTable("OutboxMessages");
        builder.HasKey(message => message.Id);
        builder.Property(message => message.Id).ValueGeneratedNever();
        builder.Property(message => message.EventType).HasMaxLength(200).IsRequired();
        builder.Property(message => message.Payload).HasColumnType("nvarchar(max)").IsRequired();
        builder.Property(message => message.OccurredOnUtc).HasColumnType("datetimeoffset");
        builder.Property(message => message.NextAttemptOnUtc).HasColumnType("datetimeoffset");
        builder.Property(message => message.LockedBy).HasMaxLength(200);
        builder.Property(message => message.LockedUntilUtc).HasColumnType("datetimeoffset");
        builder.Property(message => message.ProcessedOnUtc).HasColumnType("datetimeoffset");
        builder.Property(message => message.EventBridgeEventId).HasMaxLength(200);
        builder.Property(message => message.LastError).HasColumnType("nvarchar(2048)");
        builder.Property(message => message.DeadLetteredOnUtc).HasColumnType("datetimeoffset");
        builder.Property(message => message.RowVersion).IsRowVersion();

        builder.HasIndex(message => new
            {
                message.ProcessedOnUtc,
                message.DeadLetteredOnUtc,
                message.NextAttemptOnUtc,
                message.LockedUntilUtc,
                message.OccurredOnUtc,
                message.Id
            })
            .HasDatabaseName("IX_OutboxMessages_Eligibility")
            .HasFilter("[ProcessedOnUtc] IS NULL AND [DeadLetteredOnUtc] IS NULL");
    }
}
