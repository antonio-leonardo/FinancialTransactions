using Npgsql;

namespace FinancialTransactions.Consolidation;

/// <summary>Contagens da Outbox para o recorte e o instante da leitura, ambos na origem.</summary>
public sealed record UnresolvedEvents(long Unresolved, long Failed, DateTimeOffset ObservedAt);

/// <summary>
/// Estado de atualização (origem): eventos das contas do recorte, de qualquer dia, ainda não confirmados (0) ou em quarentena (2).
/// <para>
/// <b>Semântica de <c>observedAt</c>:</b> é o <c>statement_timestamp()</c> da instrução, isto é, o instante em que o servidor recebeu o comando.
/// Em READ COMMITTED o snapshot da contagem é tomado <i>depois</i> do recebimento, então todo commit anterior a <c>observedAt</c> está contado;
/// um commit entre o recebimento e o snapshot pode aparecer na contagem (conservador). Um relógio avaliado <i>depois</i> da agregação
/// (<c>clock_timestamp()</c>) poderia anunciar um instante posterior ao snapshot e certificar um corte que a leitura não cobriu.
/// <c>observedAt</c> não é tempo de commit nem garante incluir lançamentos posteriores ao snapshot.
/// </para>
/// </summary>
public sealed class UnresolvedEventsQuery(IConsolidationConnectionFactory connections)
{
    private const string Sql = """
        SELECT COUNT(*) FILTER (WHERE status IN (0, 2)),
               COUNT(*) FILTER (WHERE status = 2),
               statement_timestamp()
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
public sealed record ProjectionSnapshot(string? Phase, IReadOnlyList<ProjectionAccountRow> Accounts, Guid? ProjectionId = null, string? RebuildState = null);

/// <summary>
/// Consulta única à projeção por recorte e dia: a fase do bootstrap, a linha do dia e a soma acumulada até o dia saem da <b>mesma</b> instrução SQL,
/// para que um commit do Worker entre duas leituras não produza um corpo incoerente. Conexão própria, aberta quando o método é chamado (depois
/// da leitura da origem), em READ COMMITTED no primário: enxerga todo commit anterior ao seu início.
/// </summary>
public sealed class ConsolidationQueries(IConsolidationConnectionFactory connections)
{
    private const string Sql = """
        SELECT (SELECT phase FROM consolidation_bootstrap WHERE id = 1) AS phase,
               (SELECT projection_id FROM rebuild_operation WHERE id = 1) AS projection_id,
               (SELECT state FROM rebuild_operation WHERE id = 1) AS rebuild_state,
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
        Guid? projectionId = null;
        string? rebuildState = null;
        var rows = new List<ProjectionAccountRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            phase = reader.IsDBNull(0) ? null : reader.GetString(0);
            projectionId = reader.IsDBNull(1) ? null : reader.GetGuid(1);
            rebuildState = reader.IsDBNull(2) ? null : reader.GetString(2);
            rows.Add(new ProjectionAccountRow(
                reader.GetGuid(3),
                reader.GetDecimal(4),
                reader.GetDecimal(5),
                reader.GetDecimal(6),
                reader.GetInt64(7),
                reader.GetDecimal(8),
                reader.IsDBNull(9) ? null : reader.GetInt64(9)));
        }

        return new ProjectionSnapshot(phase, rows, projectionId, rebuildState);
    }
}

/// <summary>Projeção ativa registrada na origem depois de uma promoção de reconstrução (nulo: nenhuma promoção, nada foi substituído).</summary>
public sealed class ProjectionRegistryReader(IConsolidationConnectionFactory connections)
{
    public async Task<Guid?> ReadActiveAsync(CancellationToken cancellationToken)
    {
        await using var connection = await BootstrapStore.OpenAsync(connections.Open(ConsolidationConnectionRole.Query).Origin, cancellationToken).ConfigureAwait(false);
        return await BootstrapStore.ReadRegistryAsync(connection, cancellationToken).ConfigureAwait(false);
    }
}
