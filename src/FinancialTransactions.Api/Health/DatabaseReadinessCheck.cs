using FinancialTransactions.Api.Http;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;

namespace FinancialTransactions.Api.Health;

/// <summary>
/// Readiness: abre uma conexão com a connection string da aplicação e executa <c>SELECT 1</c> em até 3 s. O resultado
/// não carrega descrição nem exceção, para que nada da infraestrutura chegue à resposta.
/// </summary>
public sealed class DatabaseReadinessCheck(IConfiguration configuration, ILogger<DatabaseReadinessCheck> logger) : IHealthCheck
{
    public const string ConnectionStringName = "FinancialTransactions";

    public static readonly TimeSpan Deadline = TimeSpan.FromSeconds(3);

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(Deadline);

        try
        {
            await using var connection = new NpgsqlConnection(configuration.GetConnectionString(ConnectionStringName));
            await connection.OpenAsync(deadline.Token).ConfigureAwait(false);
            await using var command = new NpgsqlCommand("SELECT 1", connection);
            await command.ExecuteScalarAsync(deadline.Token).ConfigureAwait(false);
            return HealthCheckResult.Healthy();
        }
        catch (Exception ex) when (ex is NpgsqlException or TimeoutException or OperationCanceledException or InvalidOperationException or System.Net.Sockets.SocketException)
        {
            Log.ReadinessFailed(logger, ex.GetType().Name);
            return HealthCheckResult.Unhealthy();
        }
    }
}
