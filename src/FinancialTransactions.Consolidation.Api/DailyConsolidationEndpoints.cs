using FinancialTransactions.Auth;
using FinancialTransactions.Consolidation;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace FinancialTransactions.Consolidation.Api;

public static class DailyConsolidationEndpoints
{
    public static RouteGroupBuilder MapDailyConsolidationEndpoints(this RouteGroupBuilder group)
    {
        ArgumentNullException.ThrowIfNull(group);

        group.MapGet("/accounts/{accountId:guid}/consolidation/daily", (HttpContext http, Guid accountId, string? day, DailyConsolidationService service) =>
            service.GetAccountAsync(http, accountId, CustomerScope.Require(http.User), day)).RequireAuthorization();
        group.MapGet("/customers/{customerId:guid}/consolidation/daily", (HttpContext http, Guid customerId, string? day, DailyConsolidationService service) =>
            service.GetCustomerAsync(http, customerId, CustomerScope.Require(http.User), day)).RequireAuthorization();
        return group;
    }
}

/// <summary>Readiness da API: Unhealthy se origem ou projeção não respondem ou se o bootstrap ainda não chegou a live.</summary>
public sealed class ApiReadinessCheck(IConsolidationConnectionFactory connections) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var strings = connections.Open(ConsolidationConnectionRole.Health);
        if (!await DatabasePing.TryAsync(strings.Origin, cancellationToken).ConfigureAwait(false)
            || !await DatabasePing.TryAsync(strings.Projection, cancellationToken).ConfigureAwait(false))
        {
            return HealthCheckResult.Unhealthy();
        }

        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(DatabasePing.Deadline);
            await using var projection = await BootstrapStore.OpenAsync(strings.Projection, deadline.Token).ConfigureAwait(false);
            var state = await BootstrapStore.ReadStateAsync(projection, deadline.Token).ConfigureAwait(false);
            return state?.Phase == ConsolidationBootstrapState.Live ? HealthCheckResult.Healthy() : HealthCheckResult.Unhealthy();
        }
        catch (Exception ex) when (ex is Npgsql.NpgsqlException or TimeoutException or OperationCanceledException or InvalidOperationException)
        {
            return HealthCheckResult.Unhealthy();
        }
    }
}
