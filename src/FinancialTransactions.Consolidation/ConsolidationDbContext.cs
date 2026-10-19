using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Npgsql;

namespace FinancialTransactions.Consolidation;

/// <summary>Agregado por conta e dia de negócio (projeção). Escrito só pelo applier, por UPSERT atômico.</summary>
public sealed class DailyAccountConsolidation
{
    public Guid AccountId { get; set; }
    public DateOnly BusinessDay { get; set; }
    public decimal Credits { get; set; }
    public decimal Debits { get; set; }
    public decimal Net { get; set; }
    public long MovementCount { get; set; }
    public long MaxAppliedSequence { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>Marcador de idempotência: um evento já presente aqui nunca é somado de novo.</summary>
public sealed class ProcessedEvent
{
    public Guid EventId { get; set; }
    public long Sequence { get; set; }
    public Guid AccountId { get; set; }
    public DateTimeOffset ProcessedAt { get; set; }
}

/// <summary>Fase do conjunto inicial no destino (linha única). Só o serviço promove loading para live.</summary>
public sealed class ConsolidationBootstrapState
{
    public const string Loading = "loading";
    public const string Live = "live";

    public short Id { get; set; } = 1;
    public Guid BootstrapId { get; set; }
    public string Phase { get; set; } = Loading;
    public long MemberCount { get; set; }
    public DateTimeOffset CapturedAt { get; set; }
    public DateTimeOffset? AppliedAt { get; set; }
    public DateTimeOffset? ReconciledAt { get; set; }
}

public sealed class ConsolidationDbContext(DbContextOptions<ConsolidationDbContext> options) : DbContext(options)
{
    /// <summary>Constraints cuja violação é atribuível aos dados do evento (lista fechada, ver <see cref="FailureClassifier"/>).</summary>
    public const string CreditsNonNegative = "ck_daily_account_consolidation_credits";
    public const string DebitsNonNegative = "ck_daily_account_consolidation_debits";
    public const string MovementCountNonNegative = "ck_daily_account_consolidation_movement_count";
    public const string NetIsCreditsMinusDebits = "ck_daily_account_consolidation_net";

    public DbSet<DailyAccountConsolidation> DailyAccountConsolidations => Set<DailyAccountConsolidation>();
    public DbSet<ProcessedEvent> ProcessedEvents => Set<ProcessedEvent>();
    public DbSet<ConsolidationBootstrapState> BootstrapStates => Set<ConsolidationBootstrapState>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.ApplyConfiguration(new DailyConfiguration());
        modelBuilder.ApplyConfiguration(new ProcessedEventConfiguration());
        modelBuilder.ApplyConfiguration(new BootstrapStateConfiguration());
    }

    private sealed class DailyConfiguration : IEntityTypeConfiguration<DailyAccountConsolidation>
    {
        public void Configure(EntityTypeBuilder<DailyAccountConsolidation> builder)
        {
            builder.ToTable("daily_account_consolidation", table =>
            {
                table.HasCheckConstraint(CreditsNonNegative, "credits >= 0");
                table.HasCheckConstraint(DebitsNonNegative, "debits >= 0");
                table.HasCheckConstraint(MovementCountNonNegative, "movement_count >= 0");
                table.HasCheckConstraint(NetIsCreditsMinusDebits, "net = credits - debits");
            });
            builder.HasKey(d => new { d.AccountId, d.BusinessDay }).HasName("pk_daily_account_consolidation");
            builder.Property(d => d.AccountId).HasColumnName("account_id");
            builder.Property(d => d.BusinessDay).HasColumnName("business_day").HasColumnType("date");
            builder.Property(d => d.Credits).HasColumnName("credits").HasColumnType("numeric(24,2)");
            builder.Property(d => d.Debits).HasColumnName("debits").HasColumnType("numeric(24,2)");
            builder.Property(d => d.Net).HasColumnName("net").HasColumnType("numeric(24,2)");
            builder.Property(d => d.MovementCount).HasColumnName("movement_count").HasColumnType("bigint");
            builder.Property(d => d.MaxAppliedSequence).HasColumnName("max_applied_sequence").HasColumnType("bigint");
            builder.Property(d => d.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamp with time zone");
        }
    }

    private sealed class ProcessedEventConfiguration : IEntityTypeConfiguration<ProcessedEvent>
    {
        public void Configure(EntityTypeBuilder<ProcessedEvent> builder)
        {
            builder.ToTable("processed_events");
            builder.HasKey(e => e.EventId).HasName("pk_processed_events");
            builder.Property(e => e.EventId).HasColumnName("event_id").ValueGeneratedNever();
            builder.Property(e => e.Sequence).HasColumnName("sequence").HasColumnType("bigint");
            builder.Property(e => e.AccountId).HasColumnName("account_id");
            builder.Property(e => e.ProcessedAt).HasColumnName("processed_at").HasColumnType("timestamp with time zone");
            builder.HasIndex(e => e.AccountId).HasDatabaseName("ix_processed_events_account_id");
        }
    }

    private sealed class BootstrapStateConfiguration : IEntityTypeConfiguration<ConsolidationBootstrapState>
    {
        public void Configure(EntityTypeBuilder<ConsolidationBootstrapState> builder)
        {
            builder.ToTable("consolidation_bootstrap", table =>
            {
                table.HasCheckConstraint("ck_consolidation_bootstrap_single_row", "id = 1");
                table.HasCheckConstraint("ck_consolidation_bootstrap_phase", "phase IN ('loading', 'live')");
            });
            builder.HasKey(b => b.Id).HasName("pk_consolidation_bootstrap");
            builder.Property(b => b.Id).HasColumnName("id").HasColumnType("smallint").ValueGeneratedNever();
            builder.Property(b => b.BootstrapId).HasColumnName("bootstrap_id");
            builder.Property(b => b.Phase).HasColumnName("phase").HasColumnType("text");
            builder.Property(b => b.MemberCount).HasColumnName("member_count").HasColumnType("bigint");
            builder.Property(b => b.CapturedAt).HasColumnName("captured_at").HasColumnType("timestamp with time zone");
            builder.Property(b => b.AppliedAt).HasColumnName("applied_at").HasColumnType("timestamp with time zone");
            builder.Property(b => b.ReconciledAt).HasColumnName("reconciled_at").HasColumnType("timestamp with time zone");
        }
    }
}

/// <summary>Usada só pelo <c>dotnet ef</c>; a aplicação resolve a connection string pela fábrica por papel.</summary>
public sealed class ConsolidationDbContextDesignTimeFactory : IDesignTimeDbContextFactory<ConsolidationDbContext>
{
    private const string DevelopmentDefault = "Host=127.0.0.1;Port=15433;Database=financialtransactions_consolidation;Username=ft;Password=ft_local_dev_only";

    public ConsolidationDbContext CreateDbContext(string[] args)
    {
        var connection = Environment.GetEnvironmentVariable("FT__ConnectionStrings__Consolidation") ?? DevelopmentDefault;
        var options = new DbContextOptionsBuilder<ConsolidationDbContext>()
            .UseNpgsql(new NpgsqlConnectionStringBuilder(connection).ConnectionString)
            .Options;
        return new ConsolidationDbContext(options);
    }
}
