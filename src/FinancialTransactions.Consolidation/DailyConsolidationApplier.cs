using FinancialTransactions.Domain;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace FinancialTransactions.Consolidation;

/// <summary>
/// Aplica um evento na projeção em <b>uma</b> transação: marcador em <c>processed_events</c> e UPSERT atômico do agregado do dia.
/// Evento já presente em <c>processed_events</c> não soma nada. O marcador e a soma vivem ou morrem juntos.
/// </summary>
public sealed class DailyConsolidationApplier(ConsolidationDbContext db, IConsolidationFailurePoints failurePoints, TimeProvider clock)
{
    private const string MarkerSql = """
        INSERT INTO processed_events (event_id, sequence, account_id, processed_at)
        VALUES (@event_id, @sequence, @account_id, @now)
        ON CONFLICT (event_id) DO NOTHING
        """;

    // Sem ler-somar-gravar: o banco soma sob o lock da linha do conflito.
    private const string UpsertSql = """
        INSERT INTO daily_account_consolidation AS d
            (account_id, business_day, credits, debits, net, movement_count, max_applied_sequence, updated_at)
        VALUES (@account_id, @day, @credits, @debits, @net, 1, @sequence, @now)
        ON CONFLICT (account_id, business_day) DO UPDATE SET
            credits = d.credits + EXCLUDED.credits,
            debits = d.debits + EXCLUDED.debits,
            net = d.net + EXCLUDED.net,
            movement_count = d.movement_count + 1,
            max_applied_sequence = GREATEST(d.max_applied_sequence, EXCLUDED.max_applied_sequence),
            updated_at = EXCLUDED.updated_at
        """;

    public async Task<ApplyOutcome> ApplyAsync(ConsolidationEvent consolidationEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(consolidationEvent);
        var (credit, debit) = Validate(consolidationEvent);
        var day = BusinessDay.Of(consolidationEvent.EffectiveAt);
        var now = clock.GetUtcNow();

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        var inserted = await db.Database.ExecuteSqlRawAsync(
            MarkerSql,
            [
                new NpgsqlParameter("event_id", consolidationEvent.EventId),
                new NpgsqlParameter("sequence", consolidationEvent.Sequence),
                new NpgsqlParameter("account_id", consolidationEvent.AccountId),
                new NpgsqlParameter("now", now),
            ],
            cancellationToken).ConfigureAwait(false);

        if (inserted == 0)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return ApplyOutcome.AlreadyApplied;
        }

        await failurePoints.InsideProjectionTransactionAsync(cancellationToken).ConfigureAwait(false);

        await db.Database.ExecuteSqlRawAsync(
            UpsertSql,
            [
                new NpgsqlParameter("account_id", consolidationEvent.AccountId),
                new NpgsqlParameter("day", day),
                new NpgsqlParameter("credits", credit),
                new NpgsqlParameter("debits", debit),
                new NpgsqlParameter("net", credit - debit),
                new NpgsqlParameter("sequence", consolidationEvent.Sequence),
                new NpgsqlParameter("now", now),
            ],
            cancellationToken).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return ApplyOutcome.Applied;
    }

    /// <summary>Valida o conteúdo do evento e separa crédito de débito. Defeito aqui é do evento, nunca do ambiente.</summary>
    private static (decimal Credit, decimal Debit) Validate(ConsolidationEvent e)
    {
        if (e.Type is not (1 or 2))
        {
            throw new InvalidEventException($"type inválido ({e.Type}).");
        }

        if (e.Amount <= 0m || e.Amount > MovementAmount.Maximum || decimal.Round(e.Amount, 2) != e.Amount)
        {
            throw new InvalidEventException("amount fora do domínio (positivo, até o teto, duas casas).");
        }

        return e.Type == (short)MovementType.Credit ? (e.Amount, 0m) : (0m, e.Amount);
    }
}
