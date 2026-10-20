using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace FinancialTransactions.Consolidation;

public static class ConsolidationServiceCollectionExtensions
{
    /// <summary>
    /// Registra a base comum aos três processos: opções validadas na inicialização, fábrica de conexão de produção, contexto da projeção
    /// por escopo e os pontos de falha em suas versões inertes. O host de teste substitui o que precisa (fábrica, sink, pontos de falha).
    /// </summary>
    public static IServiceCollection AddConsolidationCore(this IServiceCollection services, int connectTimeoutSeconds, int commandTimeoutSeconds, string applicationName = "")
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddOptions<ConsolidationConnectionOptions>()
            .Configure<IConfiguration>((options, configuration) =>
            {
                options.Origin = configuration.GetConnectionString(ConsolidationConfiguration.OriginKey) ?? string.Empty;
                options.Projection = configuration.GetConnectionString(ConsolidationConfiguration.ProjectionKey) ?? string.Empty;
                options.ConnectTimeoutSeconds = connectTimeoutSeconds;
                options.CommandTimeoutSeconds = commandTimeoutSeconds;
                options.ApplicationName = applicationName;
            })
            .ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<ConsolidationConnectionOptions>, ConsolidationConnectionOptionsValidator>());

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IConsolidationConnectionFactory, ConfigurationConsolidationConnectionFactory>();
        services.TryAddSingleton<IConsolidationFailurePoints, NoConsolidationFailurePoints>();
        services.TryAddSingleton<IBootstrapFailurePoints, NoBootstrapFailurePoints>();
        services.TryAddSingleton<IConsolidationApiFailurePoints, NoConsolidationApiFailurePoints>();
        services.TryAddSingleton<IRebuildFailurePoints, NoRebuildFailurePoints>();
        services.TryAddSingleton<ProjectionVerifier>();
        services.TryAddSingleton<RebuildCoordinator>();
        services.TryAddSingleton<ConsolidationHealthState>();

        services.TryAddScoped<ConsolidationScopeRole>();
        services.AddDbContext<ConsolidationDbContext>((provider, builder) =>
        {
            var role = provider.GetRequiredService<ConsolidationScopeRole>().Role;
            builder.UseNpgsql(provider.GetRequiredService<IConsolidationConnectionFactory>().Open(role).Projection);
        });
        services.TryAddScoped<DailyConsolidationApplier>();
        return services;
    }

    /// <summary>Serviço (uma instância do consumidor; várias podem coexistir): fonte, sink de diagnóstico, reconciliador, saúde e o laço.</summary>
    public static IServiceCollection AddConsolidationConsumer(this IServiceCollection services, Action<ConsolidationConsumerOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        var options = services.AddOptions<ConsolidationConsumerOptions>().ValidateOnStart();
        if (configure is not null)
        {
            options.Configure(configure);
        }

        services.TryAddSingleton<IConsolidationDiagnostics, LoggingConsolidationDiagnostics>();
        services.TryAddSingleton<IConsolidationSource, OutboxConsolidationSource>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<ConsolidationConsumerOptions>, ConsolidationConsumerOptionsValidator>());
        services.TryAddSingleton<ConsolidationInstance>();
        services.TryAddSingleton<ConsolidationWorkerCounters>();
        services.TryAddSingleton<BootstrapReconciler>();
        services.TryAddSingleton<BootstrapPromoter>();
        services.AddHostedService<ConsolidationConsumer>();
        return services;
    }
}
