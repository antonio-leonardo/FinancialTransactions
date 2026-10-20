using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FinancialTransactions.Consolidation;

/// <summary>Estados da reconstrução, na ordem em que avançam. <c>cancelled</c> é possível até a promoção.</summary>
public static class RebuildStates
{
    public const string Prepared = "prepared";
    public const string Captured = "captured";
    public const string Replayed = "replayed";
    public const string Reconciled = "reconciled";
    public const string Enabled = "enabled";
    public const string Promoted = "promoted";
    public const string Cancelled = "cancelled";

    public static readonly IReadOnlyList<string> All = [Prepared, Captured, Replayed, Reconciled, Enabled, Promoted, Cancelled];
}

/// <summary>
/// Operação de reconstrução registrada no destino NOVO (linha única). <see cref="ProjectionId"/> é a identidade da projeção: depois da
/// promoção a origem registra qual identidade é a ativa. Os campos <c>Origin*</c> são o descritor HISTÓRICO do bootstrap (não a contagem de R).
/// </summary>
public sealed class RebuildOperation
{
    public short Id { get; set; } = 1;
    public Guid ProjectionId { get; set; }
    public string OperationLabel { get; set; } = string.Empty;
    public string SourceDatabase { get; set; } = string.Empty;
    public string State { get; set; } = RebuildStates.Prepared;
    public Guid? OriginBootstrapId { get; set; }
    public long? OriginMemberCount { get; set; }
    public DateTimeOffset? OriginCapturedAt { get; set; }
    public string? CaptureSnapshot { get; set; }
    public long? CaptureCount { get; set; }
    public string? CaptureDigest { get; set; }
    public long PayloadDivergences { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? SealedAt { get; set; }
    public DateTimeOffset? ReplayCompletedAt { get; set; }
    public DateTimeOffset? ReconciledAt { get; set; }
    public DateTimeOffset? EnabledAt { get; set; }
    public DateTimeOffset? PromotedAt { get; set; }
    public DateTimeOffset? CancelledAt { get; set; }
}

/// <summary>Movimento canônico do conjunto R, copiado da origem na captura selada. <c>EventId</c> = id do movimento.</summary>
public sealed class RebuildCaptureItem
{
    public Guid EventId { get; set; }
    public Guid AccountId { get; set; }
    public Guid CustomerId { get; set; }
    public short Type { get; set; }
    public decimal Amount { get; set; }
    public DateTimeOffset EffectiveAt { get; set; }
    public long Sequence { get; set; }
    public bool PayloadDiverges { get; set; }
}

internal sealed class RebuildOperationConfiguration : IEntityTypeConfiguration<RebuildOperation>
{
    public void Configure(EntityTypeBuilder<RebuildOperation> builder)
    {
        builder.ToTable("rebuild_operation", table =>
        {
            table.HasCheckConstraint("ck_rebuild_operation_single_row", "id = 1");
            table.HasCheckConstraint("ck_rebuild_operation_state", "state IN ('prepared', 'captured', 'replayed', 'reconciled', 'enabled', 'promoted', 'cancelled')");
        });
        builder.HasKey(r => r.Id).HasName("pk_rebuild_operation");
        builder.Property(r => r.Id).HasColumnName("id").HasColumnType("smallint").ValueGeneratedNever();
        builder.Property(r => r.ProjectionId).HasColumnName("projection_id");
        builder.Property(r => r.OperationLabel).HasColumnName("operation_label").HasColumnType("text");
        builder.Property(r => r.SourceDatabase).HasColumnName("source_database").HasColumnType("text");
        builder.Property(r => r.State).HasColumnName("state").HasColumnType("text");
        builder.Property(r => r.OriginBootstrapId).HasColumnName("origin_bootstrap_id");
        builder.Property(r => r.OriginMemberCount).HasColumnName("origin_member_count").HasColumnType("bigint");
        builder.Property(r => r.OriginCapturedAt).HasColumnName("origin_captured_at").HasColumnType("timestamp with time zone");
        builder.Property(r => r.CaptureSnapshot).HasColumnName("capture_snapshot").HasColumnType("text");
        builder.Property(r => r.CaptureCount).HasColumnName("capture_count").HasColumnType("bigint");
        builder.Property(r => r.CaptureDigest).HasColumnName("capture_digest").HasColumnType("text");
        builder.Property(r => r.PayloadDivergences).HasColumnName("payload_divergences").HasColumnType("bigint");
        builder.Property(r => r.CreatedAt).HasColumnName("created_at").HasColumnType("timestamp with time zone");
        builder.Property(r => r.SealedAt).HasColumnName("sealed_at").HasColumnType("timestamp with time zone");
        builder.Property(r => r.ReplayCompletedAt).HasColumnName("replay_completed_at").HasColumnType("timestamp with time zone");
        builder.Property(r => r.ReconciledAt).HasColumnName("reconciled_at").HasColumnType("timestamp with time zone");
        builder.Property(r => r.EnabledAt).HasColumnName("enabled_at").HasColumnType("timestamp with time zone");
        builder.Property(r => r.PromotedAt).HasColumnName("promoted_at").HasColumnType("timestamp with time zone");
        builder.Property(r => r.CancelledAt).HasColumnName("cancelled_at").HasColumnType("timestamp with time zone");
    }
}

internal sealed class RebuildCaptureItemConfiguration : IEntityTypeConfiguration<RebuildCaptureItem>
{
    public void Configure(EntityTypeBuilder<RebuildCaptureItem> builder)
    {
        builder.ToTable("rebuild_capture_item");
        builder.HasKey(i => i.EventId).HasName("pk_rebuild_capture_item");
        builder.Property(i => i.EventId).HasColumnName("event_id").ValueGeneratedNever();
        builder.Property(i => i.AccountId).HasColumnName("account_id");
        builder.Property(i => i.CustomerId).HasColumnName("customer_id");
        builder.Property(i => i.Type).HasColumnName("type").HasColumnType("smallint");
        builder.Property(i => i.Amount).HasColumnName("amount").HasColumnType("numeric(18,2)");
        builder.Property(i => i.EffectiveAt).HasColumnName("effective_at").HasColumnType("timestamp with time zone");
        builder.Property(i => i.Sequence).HasColumnName("sequence").HasColumnType("bigint");
        builder.Property(i => i.PayloadDiverges).HasColumnName("payload_diverges");
        builder.HasIndex(i => i.Sequence).HasDatabaseName("ix_rebuild_capture_item_sequence");
    }
}
