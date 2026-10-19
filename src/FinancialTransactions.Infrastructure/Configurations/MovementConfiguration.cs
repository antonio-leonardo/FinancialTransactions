using FinancialTransactions.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FinancialTransactions.Infrastructure.Configurations;

public sealed class MovementConfiguration : IEntityTypeConfiguration<Movement>
{
    /// <summary>Nome da restrição UNIQUE (account_id, idempotency_key); o tratamento do conflito 23505 filtra por ele.</summary>
    public const string IdempotencyUniqueConstraintName = "ux_movements_account_id_idempotency_key";

    public void Configure(EntityTypeBuilder<Movement> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("movements", table => table.HasCheckConstraint("ck_movements_amount_positive", "amount > 0"));
        builder.HasKey(m => m.Id).HasName("pk_movements");

        builder.Property(m => m.Id).HasColumnName("id");
        builder.Property(m => m.AccountId).HasColumnName("account_id").IsRequired();
        builder.Property(m => m.Type).HasColumnName("type").HasConversion<short>().HasColumnType("smallint").IsRequired();
        builder.Property(m => m.Amount).HasColumnName("amount").HasColumnType("numeric(18,2)").IsRequired();
        builder.Property(m => m.Currency).HasColumnName("currency").HasColumnType("char(3)").IsRequired();
        builder.Property(m => m.EffectiveAt).HasColumnName("effective_at").HasColumnType("timestamp with time zone").IsRequired();
        builder.Property(m => m.RegisteredAt).HasColumnName("registered_at").HasColumnType("timestamp with time zone").IsRequired();
        builder.Property(m => m.IdempotencyKey).HasColumnName("idempotency_key").HasColumnType("varchar(128)").HasMaxLength(IdempotencyKey.MaxLength).IsRequired();
        builder.Property(m => m.RequestFingerprint).HasColumnName("request_fingerprint").HasColumnType("char(64)").IsRequired();
        builder.Property(m => m.Description).HasColumnName("description").HasColumnType("varchar(140)").HasMaxLength(140);

        builder.HasOne<Account>()
            .WithMany()
            .HasForeignKey(m => m.AccountId)
            .HasConstraintName("fk_movements_accounts")
            .OnDelete(DeleteBehavior.Restrict);

        // Identidade da intenção (H10): a mesma chave na mesma conta só entra uma vez.
        builder.HasIndex(m => new { m.AccountId, m.IdempotencyKey })
            .IsUnique()
            .HasDatabaseName(IdempotencyUniqueConstraintName);

        // Caminho da posição: filtra por conta e corte temporal.
        builder.HasIndex(m => new { m.AccountId, m.EffectiveAt })
            .HasDatabaseName("ix_movements_account_id_effective_at");
    }
}
