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
            // Reserva: dono, token e vencimento andam juntos (todos nulos ou todos preenchidos).
            table.HasCheckConstraint(
                "ck_consolidation_outbox_lease_all_or_none",
                "(lease_owner IS NULL AND lease_token IS NULL AND lease_expires_at IS NULL) OR (lease_owner IS NOT NULL AND lease_token IS NOT NULL AND lease_expires_at IS NOT NULL)");
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
        builder.Property(e => e.LeaseOwner).HasColumnName("lease_owner").HasColumnType("text");
        builder.Property(e => e.LeaseToken).HasColumnName("lease_token");
        builder.Property(e => e.LeaseExpiresAt).HasColumnName("lease_expires_at").HasColumnType("timestamp with time zone");
        builder.Property(e => e.NextEligibleAt).HasColumnName("next_eligible_at").HasColumnType("timestamp with time zone");

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

/// <summary>Operações administrativas de reabertura da quarentena (uma linha por identificador de operação).</summary>
public sealed class OutboxAdminOperationConfiguration : IEntityTypeConfiguration<OutboxAdminOperation>
{
    public const string TableName = "consolidation_outbox_admin_operation";

    public void Configure(EntityTypeBuilder<OutboxAdminOperation> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.ToTable(TableName, table => table.HasCheckConstraint("ck_consolidation_outbox_admin_operation_count", "event_count >= 1"));
        builder.HasKey(o => o.OperationId).HasName("pk_consolidation_outbox_admin_operation");
        builder.Property(o => o.OperationId).HasColumnName("operation_id").HasColumnType("text").ValueGeneratedNever();
        builder.Property(o => o.SelectionDigest).HasColumnName("selection_digest").HasColumnType("text").IsRequired();
        builder.Property(o => o.Reason).HasColumnName("reason").HasColumnType("text").IsRequired();
        builder.Property(o => o.EventCount).HasColumnName("event_count").IsRequired();
        builder.Property(o => o.CreatedAt).HasColumnName("created_at").HasColumnType("timestamp with time zone").IsRequired();
    }
}

/// <summary>Trilha de cada evento reaberto, gravada na mesma transação da mudança de estado.</summary>
public sealed class OutboxAdminActionConfiguration : IEntityTypeConfiguration<OutboxAdminAction>
{
    public const string TableName = "consolidation_outbox_admin_log";

    public void Configure(EntityTypeBuilder<OutboxAdminAction> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.ToTable(TableName);
        builder.HasKey(a => a.Id).HasName("pk_consolidation_outbox_admin_log");
        builder.Property(a => a.Id).HasColumnName("id").HasColumnType("bigint").UseIdentityByDefaultColumn();
        builder.Property(a => a.OperationId).HasColumnName("operation_id").HasColumnType("text").IsRequired();
        builder.Property(a => a.EventId).HasColumnName("event_id").IsRequired();
        builder.Property(a => a.Sequence).HasColumnName("sequence").HasColumnType("bigint").IsRequired();
        builder.Property(a => a.PreviousStatus).HasColumnName("previous_status").HasColumnType("smallint").IsRequired();
        builder.Property(a => a.PreviousAttempts).HasColumnName("previous_attempts").IsRequired();
        builder.Property(a => a.PreviousError).HasColumnName("previous_error").HasColumnType("text");
        builder.Property(a => a.CreatedAt).HasColumnName("created_at").HasColumnType("timestamp with time zone").IsRequired();
        builder.HasOne<OutboxAdminOperation>().WithMany().HasForeignKey(a => a.OperationId).HasConstraintName("fk_consolidation_outbox_admin_log_operation");
        builder.HasIndex(a => new { a.OperationId, a.EventId }).IsUnique().HasDatabaseName("ux_consolidation_outbox_admin_log_operation_event");
        builder.HasIndex(a => a.EventId).HasDatabaseName("ix_consolidation_outbox_admin_log_event_id");
    }
}

/// <summary>Projeção ativa depois de uma promoção administrativa (linha única).</summary>
public sealed class ProjectionRegistryConfiguration : IEntityTypeConfiguration<ProjectionRegistry>
{
    public const string TableName = "consolidation_projection_registry";

    public void Configure(EntityTypeBuilder<ProjectionRegistry> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.ToTable(TableName, table => table.HasCheckConstraint("ck_consolidation_projection_registry_single_row", "id = 1"));
        builder.HasKey(r => r.Id).HasName("pk_consolidation_projection_registry");
        builder.Property(r => r.Id).HasColumnName("id").HasColumnType("smallint").ValueGeneratedNever();
        builder.Property(r => r.ActiveProjectionId).HasColumnName("active_projection_id").IsRequired();
        builder.Property(r => r.Generation).HasColumnName("generation").HasColumnType("bigint").IsRequired();
        builder.Property(r => r.PromotedAt).HasColumnName("promoted_at").HasColumnType("timestamp with time zone").IsRequired();
        builder.Property(r => r.OperationLabel).HasColumnName("operation_label").HasColumnType("text").IsRequired();
        builder.Property(r => r.PreviousDatabase).HasColumnName("previous_database").HasColumnType("text").IsRequired();
    }
}
