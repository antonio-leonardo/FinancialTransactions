using FinancialTransactions.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FinancialTransactions.Infrastructure.Configurations;

public sealed class CustomerConfiguration : IEntityTypeConfiguration<Customer>
{
    public void Configure(EntityTypeBuilder<Customer> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("customers");
        builder.HasKey(c => c.Id).HasName("pk_customers");
        builder.Property(c => c.Id).HasColumnName("id");
        builder.Property(c => c.Name).HasColumnName("name").HasColumnType("text").IsRequired();
        builder.Property(c => c.CreatedAt).HasColumnName("created_at").HasColumnType("timestamp with time zone").IsRequired();

        builder.HasData(
            new { Id = SeedData.CustomerA, Name = "Cliente A", CreatedAt = SeedData.AccountA1OpenedAt },
            new { Id = SeedData.CustomerB, Name = "Cliente B", CreatedAt = SeedData.AccountB1OpenedAt });
    }
}
