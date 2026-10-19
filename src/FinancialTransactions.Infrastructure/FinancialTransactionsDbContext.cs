using FinancialTransactions.Domain;
using FinancialTransactions.Infrastructure.Configurations;
using Microsoft.EntityFrameworkCore;

namespace FinancialTransactions.Infrastructure;

public sealed class FinancialTransactionsDbContext(DbContextOptions<FinancialTransactionsDbContext> options) : DbContext(options)
{
    public DbSet<Customer> Customers => Set<Customer>();
    public DbSet<Account> Accounts => Set<Account>();
    public DbSet<Movement> Movements => Set<Movement>();
    public DbSet<OutboxEvent> OutboxEvents => Set<OutboxEvent>();
    public DbSet<BootstrapCapture> BootstrapCaptures => Set<BootstrapCapture>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        ArgumentNullException.ThrowIfNull(configurationBuilder);
        // Pré-convenção: MovementAmount é escalar (conversão), nunca uma entidade descoberta por acidente.
        configurationBuilder.Properties<MovementAmount>().HaveConversion<MovementAmountConverter>().HavePrecision(18, 2);
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.ApplyConfiguration(new CustomerConfiguration());
        modelBuilder.ApplyConfiguration(new AccountConfiguration());
        modelBuilder.ApplyConfiguration(new MovementConfiguration());
        modelBuilder.ApplyConfiguration(new OutboxEventConfiguration());
        modelBuilder.ApplyConfiguration(new BootstrapCaptureConfiguration());
    }
}
