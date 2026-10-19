using FinancialTransactions.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FinancialTransactions.Infrastructure.Configurations;

public sealed class BootstrapCaptureConfiguration : IEntityTypeConfiguration<BootstrapCapture>
{
    public const string TableName = "consolidation_bootstrap_capture";

    public void Configure(EntityTypeBuilder<BootstrapCapture> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        // Uma única linha possível: a captura é feita uma vez e não é regravada.
        builder.ToTable(TableName, table => table.HasCheckConstraint("ck_consolidation_bootstrap_capture_single_row", "id = 1"));
        builder.HasKey(c => c.Id).HasName("pk_consolidation_bootstrap_capture");

        builder.Property(c => c.Id).HasColumnName("id").HasColumnType("smallint").ValueGeneratedNever();
        builder.Property(c => c.BootstrapId).HasColumnName("bootstrap_id").IsRequired();
        builder.Property(c => c.MemberCount).HasColumnName("member_count").HasColumnType("bigint").IsRequired();
        builder.Property(c => c.CapturedAt).HasColumnName("captured_at").HasColumnType("timestamp with time zone").IsRequired();
        builder.Property(c => c.Snapshot).HasColumnName("snapshot").HasColumnType("text").IsRequired();
    }
}
