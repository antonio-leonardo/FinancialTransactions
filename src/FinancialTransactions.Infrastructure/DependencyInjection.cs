using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FinancialTransactions.Infrastructure;

public static class DependencyInjection
{
    /// <summary>
    /// DbContext scoped com Npgsql e as consultas de posição. A connection string é resolvida por escopo, a partir do
    /// contêiner, para que configuração aplicada no Build do host (inclusive em testes) seja respeitada.
    /// Migrations não são aplicadas no startup.
    /// </summary>
    public static IServiceCollection AddFinancialTransactionsInfrastructure(this IServiceCollection services, Func<IServiceProvider, string> connectionStringFactory)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(connectionStringFactory);
        services.AddDbContext<FinancialTransactionsDbContext>((provider, options) => options.UseNpgsql(connectionStringFactory(provider)));
        services.AddScoped<PositionQueries>();
        return services;
    }
}
