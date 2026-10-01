using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TransactionalOutbox.Core.Orders;

namespace TransactionalOutbox.Infrastructure.Persistence.Configurations;

public sealed class OrderConfiguration : IEntityTypeConfiguration<Order>
{
    public void Configure(EntityTypeBuilder<Order> builder)
    {
        builder.ToTable("Orders");
        builder.HasKey(order => order.Id);
        builder.Property(order => order.Id).ValueGeneratedNever();
        builder.Property(order => order.TotalAmount).HasPrecision(18, 2);
        builder.Property(order => order.Currency).HasMaxLength(3).IsRequired();
        builder.Property(order => order.Status).HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.Property(order => order.CreatedOnUtc).HasColumnType("datetimeoffset");
        builder.Ignore(order => order.DomainEvents);
    }
}
