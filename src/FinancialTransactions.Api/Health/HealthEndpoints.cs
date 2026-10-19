using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace FinancialTransactions.Api.Health;

/// <summary>
/// Sondas de orquestração, fora do grupo autenticado. Anônimas por desenho: o orquestrador sonda sem credencial e a
/// resposta é só o estado, sem dependência, mensagem de exceção ou connection string.
/// </summary>
public static class HealthEndpoints
{
    public const string ReadyTag = "ready";

    public static IEndpointRouteBuilder MapHealthEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        // Liveness: nenhum check; responde 200 enquanto o processo atende e não toca o banco.
        app.MapHealthChecks("/health/live", new HealthCheckOptions
        {
            Predicate = _ => false,
            ResponseWriter = WriteStatusAsync,
        }).AllowAnonymous();

        app.MapHealthChecks("/health/ready", new HealthCheckOptions
        {
            Predicate = registration => registration.Tags.Contains(ReadyTag),
            ResponseWriter = WriteStatusAsync,
        }).AllowAnonymous();

        return app;
    }

    private static Task WriteStatusAsync(HttpContext context, HealthReport report)
    {
        context.Response.ContentType = "application/json";
        return context.Response.WriteAsync(report.Status == HealthStatus.Healthy ? "{\"status\":\"Healthy\"}" : "{\"status\":\"Unhealthy\"}");
    }
}
