using FinancialTransactions.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FinancialTransactions.Infrastructure.Configurations;

public sealed class AccountConfiguration : IEntityTypeConfiguration<Account>
{
    public void Configure(EntityTypeBuilder<Account> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("accounts");
        builder.HasKey(a => a.Id).HasName("pk_accounts");
        builder.Property(a => a.Id).HasColumnName("id");
        builder.Property(a => a.CustomerId).HasColumnName("customer_id").IsRequired();
        builder.Property(a => a.Currency).HasColumnName("currency").HasColumnType("char(3)").IsRequired();
        builder.Property(a => a.OpenedAt).HasColumnName("opened_at").HasColumnType("timestamp with time zone").IsRequired();

        builder.HasOne<Customer>()
            .WithMany()
            .HasForeignKey(a => a.CustomerId)
            .HasConstraintName("fk_accounts_customers")
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(a => a.CustomerId).HasDatabaseName("ix_accounts_customer_id");

        // A2 abre dois meses depois de A1 de propósito: exercita a exclusão por openedAt (H7).
        builder.HasData(
            new { Id = SeedData.AccountA1, CustomerId = SeedData.CustomerA, Currency = MovementAmount.FixedCurrency, OpenedAt = SeedData.AccountA1OpenedAt },
            new { Id = SeedData.AccountA2, CustomerId = SeedData.CustomerA, Currency = MovementAmount.FixedCurrency, OpenedAt = SeedData.AccountA2OpenedAt },
            new { Id = SeedData.AccountB1, CustomerId = SeedData.CustomerB, Currency = MovementAmount.FixedCurrency, OpenedAt = SeedData.AccountB1OpenedAt });
    }
}
