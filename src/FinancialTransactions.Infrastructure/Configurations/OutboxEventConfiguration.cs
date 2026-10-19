using FinancialTransactions.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FinancialTransactions.Infrastructure.Configurations;

public sealed class OutboxEventConfiguration : IEntityTypeConfiguration<OutboxEvent>
{
    public const string TableName = "consolidation_outbox";

    public void Configure(EntityTypeBuilder<OutboxEvent> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        // Sem CHECK sobre type e amount: um evento defeituoso precisa poder existir para ser reconhecido e posto em quarentena.
        builder.ToTable(TableName, table =>
        {
            table.HasCheckConstraint("ck_consolidation_outbox_status", "status IN (0, 1, 2)");
            table.HasCheckConstraint("ck_consolidation_outbox_origin", "origin IN (0, 1)");
            table.HasCheckConstraint("ck_consolidation_outbox_attempts", "attempts >= 0");
        });
        builder.HasKey(e => e.Sequence).HasName("pk_consolidation_outbox");

        builder.Property(e => e.Sequence).HasColumnName("sequence").HasColumnType("bigint").UseIdentityByDefaultColumn();
        builder.Property(e => e.EventId).HasColumnName("event_id").IsRequired();
        builder.Property(e => e.AccountId).HasColumnName("account_id").IsRequired();
        builder.Property(e => e.CustomerId).HasColumnName("customer_id").IsRequired();
        builder.Property(e => e.Type).HasColumnName("type").HasConversion<short>().HasColumnType("smallint").IsRequired();
        builder.Property(e => e.Amount).HasColumnName("amount").HasColumnType("numeric(18,2)").IsRequired();
        builder.Property(e => e.EffectiveAt).HasColumnName("effective_at").HasColumnType("timestamp with time zone").IsRequired();
        builder.Property(e => e.CreatedAt).HasColumnName("created_at").HasColumnType("timestamp with time zone").IsRequired();
        builder.Property(e => e.Origin).HasColumnName("origin").HasConversion<short>().HasColumnType("smallint").IsRequired();
        builder.Property(e => e.BootstrapId).HasColumnName("bootstrap_id");
        builder.Property(e => e.Status).HasColumnName("status").HasConversion<short>().HasColumnType("smallint").HasDefaultValue(OutboxStatus.Pending).IsRequired();
        builder.Property(e => e.Attempts).HasColumnName("attempts").HasDefaultValue(0).IsRequired();
        builder.Property(e => e.LastError).HasColumnName("last_error").HasColumnType("text");
        builder.Property(e => e.ProcessedAt).HasColumnName("processed_at").HasColumnType("timestamp with time zone");

        // Um evento por movimento (event_id = id do movimento). Sem FK para movements: o bootstrap casa por identidade
        // e a Outbox não deve impedir a limpeza de dados de teste.
        builder.HasIndex(e => e.EventId).IsUnique().HasDatabaseName("ux_consolidation_outbox_event_id");

        // Caminho do consumidor: pendentes em ordem de enfileiramento.
        builder.HasIndex(e => new { e.Status, e.Sequence }).HasDatabaseName("ix_consolidation_outbox_status_sequence");

        // Caminho da fase de carga e da reconciliação: membros do conjunto inicial por situação.
        builder.HasIndex(e => new { e.BootstrapId, e.Status }).HasDatabaseName("ix_consolidation_outbox_bootstrap_id_status");

        // Estado de atualização do consolidado: pendências e quarentena por conta do recorte.
        builder.HasIndex(e => new { e.AccountId, e.Status }).HasDatabaseName("ix_consolidation_outbox_account_id_status");
    }
}
