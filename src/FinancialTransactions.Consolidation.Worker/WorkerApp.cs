using FinancialTransactions.Consolidation;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace FinancialTransactions.Consolidation.Worker;

/// <summary>Composição dos dois modos do executável. Pública para que a fixture de teste monte o mesmo host, trocando só o que o teste precisa.</summary>
public static class WorkerApp
{
    public const string ReadyTag = "ready";
    public const string DefaultUrl = "http://127.0.0.1:5120";

    /// <summary>Modo serviço: host web mínimo (<c>/health/live</c>, <c>/health/ready</c>) e o único consumidor.</summary>
    public static WebApplication CreateService(string[] args, Action<WebApplicationBuilder>? configure = null)
    {
        var builder = WebApplication.CreateBuilder(args);

        // FT__ConnectionStrings__Consolidation entra na configuração como ConnectionStrings:Consolidation.
        builder.Configuration.AddEnvironmentVariables("FT__");
        if (string.IsNullOrWhiteSpace(builder.Configuration["urls"]))
        {
            builder.WebHost.UseUrls(DefaultUrl);
        }

        // O Worker tolera mais: Timeout=5;Command Timeout=15 (a API do consolidado é a que tem orçamento curto).
        builder.Services.AddConsolidationCore(connectTimeoutSeconds: 5, commandTimeoutSeconds: 15, applicationName: ConsolidationApplicationNames.Worker);
        builder.Services.AddConsolidationConsumer();
        builder.Services.AddHealthChecks().AddCheck<WorkerReadinessCheck>("consumer", failureStatus: HealthStatus.Unhealthy, tags: [ReadyTag]);

        configure?.Invoke(builder);

        var app = builder.Build();
        app.MapHealthChecks("/health/live", ConsolidationHealthMapping.LiveOptions()).AllowAnonymous();
        app.MapHealthChecks("/health/ready", ConsolidationHealthMapping.ReadyOptions(ReadyTag)).AllowAnonymous();

        // Diagnóstico desta instância (somente leitura, sem payload, token nem connection string); não é um endpoint de administração.
        app.MapGet("/diagnostics/instance", (ConsolidationInstance self, ConsolidationWorkerCounters counters, ConsolidationHealthState state) => Results.Json(new
        {
            instanceId = self.Id,
            startedAt = self.StartedAt,
            degradedReasons = Enum.GetValues<DegradedReason>().Where(state.Has).Select(r => r.ToString()).ToArray(),
            counters = counters.Snapshot(),
        })).AllowAnonymous();
        return app;
    }

    /// <summary>Modo <c>--backfill</c>: coordenador. Sem Kestrel e sem consumidor; devolve o código de saída.</summary>
    public static async Task<int> RunBackfillAsync(string[] args, Action<HostApplicationBuilder>? configure = null)
    {
        var environment = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")
            ?? Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT")
            ?? Environments.Production;
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { Args = args, EnvironmentName = environment });
        builder.Configuration.AddEnvironmentVariables("FT__");
        builder.Services.AddConsolidationCore(connectTimeoutSeconds: 5, commandTimeoutSeconds: 15, applicationName: ConsolidationApplicationNames.Coordinator);
        builder.Services.AddSingleton<BootstrapCoordinator>();
        configure?.Invoke(builder);

        using var host = builder.Build();
        try
        {
            // Sem hosted service, ValidateOnStart não roda: a validação é explícita e uma configuração inválida sai com código 2.
            _ = host.Services.GetRequiredService<IOptions<ConsolidationConnectionOptions>>().Value;
            using var cancellation = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;
                cancellation.Cancel();
            };
            return await host.Services.GetRequiredService<BootstrapCoordinator>().RunAsync(cancellation.Token).ConfigureAwait(false);
        }
        catch (OptionsValidationException ex)
        {
            await Console.Error.WriteLineAsync($"Configuração inválida: {string.Join(" ", ex.Failures)}").ConfigureAwait(false);
            return CoordinatorExitCodes.Unreachable;
        }
    }
}
