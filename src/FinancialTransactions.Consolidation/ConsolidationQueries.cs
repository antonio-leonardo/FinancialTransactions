using Npgsql;

namespace FinancialTransactions.Consolidation;

/// <summary>Contagens da Outbox para o recorte e o instante da leitura, ambos na origem.</summary>
public sealed record UnresolvedEvents(long Unresolved, long Failed, DateTimeOffset ObservedAt);

/// <summary>Estado de atualização (origem): eventos das contas do recorte, de qualquer dia, ainda não confirmados (0) ou em quarentena (2).</summary>
public sealed class UnresolvedEventsQuery(IConsolidationConnectionFactory connections)
{
    private const string Sql = """
        SELECT COUNT(*) FILTER (WHERE status IN (0, 2)),
               COUNT(*) FILTER (WHERE status = 2),
               clock_timestamp()
        FROM consolidation_outbox
        WHERE account_id = ANY(@ids)
        """;

    public async Task<UnresolvedEvents> ReadAsync(IReadOnlyCollection<Guid> accountIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(accountIds);
        await using var connection = await BootstrapStore.OpenAsync(connections.Open(ConsolidationConnectionRole.Query).Origin, cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(Sql, connection);
        command.Parameters.AddWithValue("ids", accountIds.ToArray());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        return new UnresolvedEvents(reader.GetInt64(0), reader.GetInt64(1), reader.GetFieldValue<DateTimeOffset>(2));
    }
}

/// <summary>Linha financeira de uma conta num dia: valores do dia e fechamento acumulado até o dia, lidos juntos.</summary>
public sealed record ProjectionAccountRow(
    Guid AccountId,
    decimal Credits,
    decimal Debits,
    decimal Net,
    long MovementCount,
    decimal ClosingBalance,
    long? MaxAppliedSequence);

/// <summary>Resultado da consulta única: fase do bootstrap (nula se não há linha) e uma linha por conta pedida.</summary>
public sealed record ProjectionSnapshot(string? Phase, IReadOnlyList<ProjectionAccountRow> Accounts);

/// <summary>
/// Consulta única à projeção por recorte e dia: a fase do bootstrap, a linha do dia e a soma acumulada até o dia saem da <b>mesma</b> instrução SQL,
/// para que um commit do Worker entre duas leituras não produza um corpo incoerente. Conexão própria, aberta quando o método é chamado (depois
/// da leitura da origem), em READ COMMITTED no primário: enxerga todo commit anterior ao seu início.
/// </summary>
public sealed class ConsolidationQueries(IConsolidationConnectionFactory connections)
{
    private const string Sql = """
        SELECT (SELECT phase FROM consolidation_bootstrap WHERE id = 1) AS phase,
               a.account_id,
               COALESCE(d.credits, 0),
               COALESCE(d.debits, 0),
               COALESCE(d.net, 0),
               COALESCE(d.movement_count, 0),
               COALESCE(c.closing, 0),
               m.max_seq
        FROM unnest(@ids) AS a(account_id)
        LEFT JOIN daily_account_consolidation d ON d.account_id = a.account_id AND d.business_day = @day
        LEFT JOIN (SELECT account_id, SUM(net) AS closing
                   FROM daily_account_consolidation
                   WHERE account_id = ANY(@ids) AND business_day <= @day
                   GROUP BY account_id) c ON c.account_id = a.account_id
        LEFT JOIN (SELECT account_id, MAX(max_applied_sequence) AS max_seq
                   FROM daily_account_consolidation
                   WHERE account_id = ANY(@ids)
                   GROUP BY account_id) m ON m.account_id = a.account_id
        """;

    public async Task<ProjectionSnapshot> ReadAsync(IReadOnlyCollection<Guid> accountIds, DateOnly day, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(accountIds);
        await using var connection = await BootstrapStore.OpenAsync(connections.Open(ConsolidationConnectionRole.Query).Projection, cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(Sql, connection);
        command.Parameters.AddWithValue("ids", accountIds.ToArray());
        command.Parameters.AddWithValue("day", day);

        string? phase = null;
        var rows = new List<ProjectionAccountRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            phase = reader.IsDBNull(0) ? null : reader.GetString(0);
            rows.Add(new ProjectionAccountRow(
                reader.GetGuid(1),
                reader.GetDecimal(2),
                reader.GetDecimal(3),
                reader.GetDecimal(4),
                reader.GetInt64(5),
                reader.GetDecimal(6),
                reader.IsDBNull(7) ? null : reader.GetInt64(7)));
        }

        return new ProjectionSnapshot(phase, rows);
    }
}
