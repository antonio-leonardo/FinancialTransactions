using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;

namespace FinancialTransactions.Consolidation;

public enum DegradedReason
{
    /// <summary>Sem linha de bootstrap na projeção: o serviço aguarda, sem consumir.</summary>
    WaitingForBootstrap,

    /// <summary>A linha de controle da origem não confere com a da projeção (ou não existe).</summary>
    ControlMismatch,

    /// <summary>A projeção tem dados mas não tem a linha de bootstrap.</summary>
    ProjectionIncoherent,

    /// <summary>A reconciliação do conjunto inicial divergiu ou há quarentena no conjunto.</summary>
    ReconciliationFailed,

    /// <summary>Última falha do consumidor de classe ambiente, ainda não desarmada.</summary>
    EnvironmentFailure,
}

/// <summary>
/// Estado de degradação do consumidor (singleton). Cada motivo tem o seu desarme: ambiente no primeiro ciclo concluído com sucesso
/// depois da falha; reconciliação ao chegar a live; espera ao encontrar a linha válida.
/// </summary>
public sealed class ConsolidationHealthState
{
    private int _reasons;

    public bool IsDegraded => Volatile.Read(ref _reasons) != 0;

    public bool Has(DegradedReason reason) => (Volatile.Read(ref _reasons) & Bit(reason)) != 0;

    public void Set(DegradedReason reason) => Interlocked.Or(ref _reasons, Bit(reason));

    public void Clear(DegradedReason reason) => Interlocked.And(ref _reasons, ~Bit(reason));

    /// <summary>Um ciclo do consumidor terminou sem exceção: desarma a falha de ambiente.</summary>
    public void OnCycleCompleted() => Clear(DegradedReason.EnvironmentFailure);

    private static int Bit(DegradedReason reason) => 1 << (int)reason;
}

/// <summary>Mapeamento explícito de saúde para HTTP e corpo: <c>Degraded</c> responde 503 (o padrão do ASP.NET seria 200).</summary>
public static class ConsolidationHealthMapping
{
    public static int StatusCodeFor(HealthStatus status) => status == HealthStatus.Healthy ? StatusCodes.Status200OK : StatusCodes.Status503ServiceUnavailable;

    public static string BodyFor(HealthStatus status) => $"{{\"status\":\"{status}\"}}";

    public static HealthCheckOptions ReadyOptions(string readyTag) => new()
    {
        Predicate = registration => registration.Tags.Contains(readyTag),
        ResultStatusCodes =
        {
            [HealthStatus.Healthy] = StatusCodeFor(HealthStatus.Healthy),
            [HealthStatus.Degraded] = StatusCodeFor(HealthStatus.Degraded),
            [HealthStatus.Unhealthy] = StatusCodeFor(HealthStatus.Unhealthy),
        },
        ResponseWriter = WriteAsync,
    };

    public static HealthCheckOptions LiveOptions() => new()
    {
        Predicate = _ => false,
        ResponseWriter = WriteAsync,
    };

    private static Task WriteAsync(HttpContext context, HealthReport report)
    {
        context.Response.ContentType = "application/json";
        return context.Response.WriteAsync(BodyFor(report.Status));
    }
}

/// <summary>SELECT 1 na origem e na projeção (papel Health, prazo de 3 s): qualquer falha é <c>Unhealthy</c>.</summary>
public static class DatabasePing
{
    public static readonly TimeSpan Deadline = TimeSpan.FromSeconds(3);

    public static async Task<bool> TryAsync(string connectionString, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(Deadline);
        try
        {
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync(deadline.Token).ConfigureAwait(false);
            await using var command = new NpgsqlCommand("SELECT 1", connection);
            await command.ExecuteScalarAsync(deadline.Token).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is NpgsqlException or TimeoutException or OperationCanceledException or InvalidOperationException or System.Net.Sockets.SocketException)
        {
            return false;
        }
    }
}

/// <summary>Readiness do Worker: Unhealthy se uma conexão falha, Degraded se o consumidor está degradado, senão Healthy.</summary>
public sealed class WorkerReadinessCheck(IConsolidationConnectionFactory connections, ConsolidationHealthState state) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var strings = connections.Open(ConsolidationConnectionRole.Health);
        var origin = await DatabasePing.TryAsync(strings.Origin, cancellationToken).ConfigureAwait(false);
        var projection = await DatabasePing.TryAsync(strings.Projection, cancellationToken).ConfigureAwait(false);
        if (!origin || !projection)
        {
            return HealthCheckResult.Unhealthy();
        }

        return state.IsDegraded ? HealthCheckResult.Degraded() : HealthCheckResult.Healthy();
    }
}
