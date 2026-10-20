using FinancialTransactions.Auth;
using FinancialTransactions.Consolidation;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace FinancialTransactions.Consolidation.Api;

/// <summary>Composição da API do consolidado. Pública para que a fixture de teste monte o mesmo host trocando só o que o teste precisa.</summary>
public static class ConsolidationApiApp
{
    public const string ReadyTag = "ready";
    public const string DefaultUrl = "http://127.0.0.1:5110";

    /// <summary>Orçamento padrão de uma requisição: conexão e comando têm 1 s e o conjunto, 1,5 s (hipótese do projeto, ver docs).</summary>
    public static readonly TimeSpan DefaultRequestBudget = TimeSpan.FromMilliseconds(1500);

    public static WebApplication Create(string[] args, Action<WebApplicationBuilder>? configure = null)
    {
        var builder = WebApplication.CreateBuilder(args);

        // FT__Auth__SigningKey (e qualquer FT__Secao__Chave) entra na configuração sem o prefixo.
        builder.Configuration.AddEnvironmentVariables("FT__");
        if (string.IsNullOrWhiteSpace(builder.Configuration["urls"]))
        {
            builder.WebHost.UseUrls(DefaultUrl);
        }

        // Prazos próprios desta API (só aqui): Timeout=1;Command Timeout=1 nas duas strings.
        builder.Services.AddConsolidationCore(connectTimeoutSeconds: 1, commandTimeoutSeconds: 1, applicationName: ConsolidationApplicationNames.Api);
        builder.Services.AddFinancialTransactionsJwt();
        builder.Services.AddOptions<ConsolidationApiOptions>();
        builder.Services.AddSingleton<AccountOwnership>();
        builder.Services.AddSingleton<UnresolvedEventsQuery>();
        builder.Services.AddSingleton<ConsolidationQueries>();
        builder.Services.AddSingleton<ProjectionRegistryReader>();
        builder.Services.AddSingleton<DailyConsolidationService>();
        builder.Services.AddHealthChecks().AddCheck<ApiReadinessCheck>("consolidation", failureStatus: HealthStatus.Unhealthy, tags: [ReadyTag]);
        builder.Services.AddProblemDetails();

        configure?.Invoke(builder);

        var app = builder.Build();

        // Validação de startup: sem chave de pelo menos 32 bytes a API não sobe (H11).
        if (AuthDefaults.TryReadSigningKey(app.Configuration[AuthDefaults.SigningKeyConfigurationPath], out var signingKeyError) is null)
        {
            throw new InvalidOperationException(signingKeyError);
        }

        app.UseExceptionHandler(handler => handler.Run(async context =>
        {
            var exception = context.Features.Get<IExceptionHandlerFeature>()?.Error;
            if (exception is not null)
            {
                Log.UnhandledException(context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("ConsolidationApi"), exception, context.Request.Method, context.Request.Path.Value ?? string.Empty);
            }

            await Problems.Internal().ExecuteAsync(context).ConfigureAwait(false);
        }));
        app.UseAuthentication();
        app.UseAuthorization();

        app.MapHealthChecks("/health/live", ConsolidationHealthMapping.LiveOptions()).AllowAnonymous();
        app.MapHealthChecks("/health/ready", ConsolidationHealthMapping.ReadyOptions(ReadyTag)).AllowAnonymous();

        var api = app.MapGroup("/api/v1").RequireAuthorization();
        api.MapDailyConsolidationEndpoints();
        return app;
    }
}

/// <summary>Ajustes da API; o orçamento só é alterado nos testes (em produção vale o padrão de 1,5 s).</summary>
public sealed class ConsolidationApiOptions
{
    public TimeSpan RequestBudget { get; set; } = ConsolidationApiApp.DefaultRequestBudget;
}
