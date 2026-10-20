using System.Data;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace FinancialTransactions.Consolidation;

public enum PromotionStatus
{
    /// <summary>Reconciliado e promovido nesta chamada.</summary>
    Promoted,

    /// <summary>Outra instância já concluiu a promoção: nada foi comparado nem alterado.</summary>
    AlreadyLive,

    /// <summary>Outra instância mantém a exclusividade de reconciliação deste destino.</summary>
    Busy,

    /// <summary>A linha de bootstrap não é a esperada (ausente ou de outra captura): nada foi comparado nem alterado.</summary>
    StateMismatch,

    /// <summary>A reconciliação divergiu: a projeção permanece em loading.</summary>
    Diverged,

    /// <summary>A reconciliação passou, mas a transição condicional não afetou linha: o estado será relido.</summary>
    NoRows,
}

public sealed record PromotionResult(PromotionStatus Status, string? Deviation = null);

/// <summary>
/// Reconcilia e promove loading para live com exclusividade <b>local ao destino</b> (lock consultivo no banco da projeção, em sessão própria):
/// com várias instâncias, quem obtém a exclusividade relê a fase e só então compara. Uma instância que chega depois da promoção encontra
/// <c>live</c> e retorna "já concluído"; nunca compara a projeção viva com o conjunto inicial, que é menor. A transição é condicional
/// (<c>phase = 'loading'</c>) e não existe caminho inverso.
/// </summary>
public sealed partial class BootstrapPromoter(
    IConsolidationConnectionFactory connections,
    BootstrapReconciler reconciler,
    TimeProvider clock,
    ILogger<BootstrapPromoter> logger)
{
    private const string LockSql = "SELECT pg_try_advisory_lock(hashtext('ft_bootstrap_reconcile'))";
    private const string UnlockSql = "SELECT pg_advisory_unlock(hashtext('ft_bootstrap_reconcile'))";

    public async Task<PromotionResult> TryPromoteAsync(Guid bootstrapId, CancellationToken cancellationToken)
    {
        // Sessão própria e sem pool: fechar a conexão encerra a sessão e libera o lock mesmo se o unlock falhar.
        var lockString = new NpgsqlConnectionStringBuilder(connections.Open(ConsolidationConnectionRole.Reconcile).Projection) { Pooling = false }.ConnectionString;
        await using var session = await BootstrapStore.OpenAsync(lockString, cancellationToken).ConfigureAwait(false);
        var acquired = false;
        try
        {
            await using (var tryLock = new NpgsqlCommand(LockSql, session))
            {
                acquired = (bool)(await tryLock.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) ?? false);
            }

            if (!acquired)
            {
                return new PromotionResult(PromotionStatus.Busy);
            }

            // Releitura da fase DEPOIS de adquirir a exclusividade.
            var state = await BootstrapStore.ReadStateAsync(session, cancellationToken).ConfigureAwait(false);
            if (state is null || state.BootstrapId != bootstrapId)
            {
                return new PromotionResult(PromotionStatus.StateMismatch);
            }

            if (state.Phase == ConsolidationBootstrapState.Live)
            {
                return new PromotionResult(PromotionStatus.AlreadyLive);
            }

            var result = await reconciler.ReconcileAsync(bootstrapId, cancellationToken).ConfigureAwait(false);
            if (!result.Equal)
            {
                return new PromotionResult(PromotionStatus.Diverged, result.FirstDeviation ?? "divergência");
            }

            await using var promote = new NpgsqlCommand(
                """
                UPDATE consolidation_bootstrap
                SET phase = 'live', applied_at = @at, reconciled_at = @at
                WHERE id = 1 AND bootstrap_id = @id AND phase = 'loading'
                """,
                session);
            promote.Parameters.AddWithValue("at", clock.GetUtcNow());
            promote.Parameters.AddWithValue("id", bootstrapId);
            return await promote.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1
                ? new PromotionResult(PromotionStatus.Promoted)
                : new PromotionResult(PromotionStatus.NoRows);
        }
        finally
        {
            await ReleaseAsync(session, acquired).ConfigureAwait(false);
        }
    }

    /// <summary>Só quem adquiriu o lock o libera; a sessão é descartada em seguida de qualquer forma.</summary>
    private async Task ReleaseAsync(NpgsqlConnection session, bool acquired)
    {
        if (!acquired || session.State != ConnectionState.Open)
        {
            return;
        }

        try
        {
            await using var unlock = new NpgsqlCommand(UnlockSql, session);
            await unlock.ExecuteScalarAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is NpgsqlException or InvalidOperationException)
        {
            LogUnlockFailed(logger, ex.GetType().Name);
        }
    }

    [LoggerMessage(EventId = 5100, Level = LogLevel.Warning, Message = "Falha ao liberar o lock de reconciliação ({FailureType}); a sessão será encerrada.")]
    private static partial void LogUnlockFailed(ILogger logger, string failureType);
}
