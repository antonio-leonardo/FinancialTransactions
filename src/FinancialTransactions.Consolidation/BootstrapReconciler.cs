using System.Data;
using Npgsql;

namespace FinancialTransactions.Consolidation;

/// <summary>Agregado de uma conta em um dia de negócio, dos dois lados da reconciliação.</summary>
public sealed record DayAggregate(Guid AccountId, DateOnly Day, long Count, decimal Credits, decimal Debits)
{
    public decimal Net => Credits - Debits;
}

public sealed record ReconciliationResult(bool Equal, string? FirstDeviation);

/// <summary>Comparação estrita, em memória, do conjunto inicial: contagem, créditos, débitos, líquido e conjunto de ids.</summary>
public static class ReconciliationComparer
{
    public static ReconciliationResult Compare(
        IEnumerable<DayAggregate> expected,
        IEnumerable<DayAggregate> actual,
        IReadOnlyCollection<Guid> expectedIds,
        IReadOnlyCollection<Guid> processedIds)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(actual);
        ArgumentNullException.ThrowIfNull(expectedIds);
        ArgumentNullException.ThrowIfNull(processedIds);

        var left = expected.OrderBy(a => a.AccountId).ThenBy(a => a.Day).ToList();
        var right = actual.OrderBy(a => a.AccountId).ThenBy(a => a.Day).ToList();

        var length = Math.Max(left.Count, right.Count);
        for (var i = 0; i < length; i++)
        {
            var e = i < left.Count ? left[i] : null;
            var a = i < right.Count ? right[i] : null;
            if (e is null || a is null || e.AccountId != a.AccountId || e.Day != a.Day)
            {
                return Differ($"conta/dia: esperado {Describe(e)}, obtido {Describe(a)}");
            }

            if (e.Count != a.Count || e.Credits != a.Credits || e.Debits != a.Debits || e.Net != a.Net)
            {
                return Differ($"conta {e.AccountId} dia {e.Day:yyyy-MM-dd}: esperado {Describe(e)}, obtido {Describe(a)}");
            }
        }

        var expectedSet = expectedIds.ToHashSet();
        var processedSet = processedIds.ToHashSet();
        if (expectedSet.Count != expectedIds.Count)
        {
            return Differ("ids duplicados no conjunto esperado");
        }

        var missing = expectedSet.Count(id => !processedSet.Contains(id));
        var extra = processedSet.Count(id => !expectedSet.Contains(id));
        return missing == 0 && extra == 0
            ? new ReconciliationResult(true, null)
            : Differ($"ids: {missing} esperados sem marcador na projeção, {extra} marcadores fora do conjunto");
    }

    /// <summary>
    /// Os descritores (origem e destino) e o conjunto realmente identificado precisam concordar, inclusive em zero: bootstrap_id pedido,
    /// bootstrap_id e member_count dos dois lados, cardinalidade dos ids, linhas da Outbox do conjunto, soma de movement_count esperada e
    /// obtida. Contagem igual não comprova identidade — por isso <see cref="Compare(IEnumerable{DayAggregate}, IEnumerable{DayAggregate}, IReadOnlyCollection{Guid}, IReadOnlyCollection{Guid})"/>
    /// ainda compara ids e valores. Devolve nulo se tudo concorda.
    /// </summary>
    public static ReconciliationResult? VerifyDescriptor(
        Guid requestedId,
        CaptureRow? capture,
        BootstrapStateRow? state,
        IReadOnlyCollection<Guid> expectedIds,
        long outboxSetCount,
        IReadOnlyCollection<DayAggregate> expected,
        IReadOnlyCollection<DayAggregate> actual,
        IReadOnlyCollection<Guid> processedIds)
    {
        ArgumentNullException.ThrowIfNull(expectedIds);
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(actual);
        ArgumentNullException.ThrowIfNull(processedIds);

        if (capture is null)
        {
            return Differ("registro de controle da origem ausente");
        }

        if (state is null)
        {
            return Differ("linha de bootstrap ausente na projeção");
        }

        if (capture.BootstrapId != requestedId || state.BootstrapId != requestedId)
        {
            return Differ("bootstrap_id do conjunto, da origem e da projeção não coincidem");
        }

        if (capture.MemberCount != state.MemberCount)
        {
            return Differ($"member_count diverge: origem {capture.MemberCount}, projeção {state.MemberCount}");
        }

        if (expectedIds.Count != capture.MemberCount)
        {
            return Differ($"descritor declara {capture.MemberCount} membros; o conjunto identificado na origem tem {expectedIds.Count}");
        }

        if (outboxSetCount != capture.MemberCount)
        {
            return Differ($"descritor declara {capture.MemberCount} membros; a Outbox marca {outboxSetCount}");
        }

        var expectedMovements = expected.Sum(a => a.Count);
        if (expectedMovements != expectedIds.Count)
        {
            return Differ($"soma de movement_count esperada ({expectedMovements}) difere do número de ids ({expectedIds.Count})");
        }

        var appliedMovements = actual.Sum(a => a.Count);
        return appliedMovements != processedIds.Count
            ? Differ($"soma de movement_count na projeção ({appliedMovements}) difere dos marcadores ({processedIds.Count})")
            : null;
    }

    private static ReconciliationResult Differ(string deviation) => new(false, deviation);

    private static string Describe(DayAggregate? a) =>
        a is null
            ? "ausente"
            : $"[conta {a.AccountId}, dia {a.Day:yyyy-MM-dd}, n={a.Count}, créditos={a.Credits}, débitos={a.Debits}, líquido={a.Net}]";
}

/// <summary>
/// Reconcilia o conjunto inicial (membros por identidade, <c>bootstrap_id = @id</c>) entre origem e projeção. A origem agrega os movimentos
/// por conta e dia de negócio com a zona nomeada do próprio PostgreSQL, uma segunda opinião independente da conversão feita no .NET.
/// </summary>
public sealed class BootstrapReconciler(IConsolidationConnectionFactory connections)
{
    private const string OriginAggregateSql = """
        SELECT m.account_id,
               (m.effective_at AT TIME ZONE @zone)::date AS day,
               COUNT(*),
               COALESCE(SUM(CASE WHEN m.type = 1 THEN m.amount ELSE 0 END), 0),
               COALESCE(SUM(CASE WHEN m.type = 2 THEN m.amount ELSE 0 END), 0)
        FROM movements m
        JOIN consolidation_outbox o ON o.event_id = m.id
        WHERE o.bootstrap_id = @id
        GROUP BY 1, 2
        """;

    private const string OriginIdsSql = """
        SELECT m.id FROM movements m JOIN consolidation_outbox o ON o.event_id = m.id WHERE o.bootstrap_id = @id
        """;

    private const string OutboxSetCountSql = "SELECT COUNT(*) FROM consolidation_outbox WHERE bootstrap_id = @id";

    public async Task<ReconciliationResult> ReconcileAsync(Guid bootstrapId, CancellationToken cancellationToken)
    {
        var strings = connections.Open(ConsolidationConnectionRole.Reconcile);

        List<DayAggregate> expected;
        List<Guid> expectedIds;
        CaptureRow? capture;
        long outboxSetCount;
        await using (var origin = await BootstrapStore.OpenAsync(strings.Origin, cancellationToken).ConfigureAwait(false))
        await using (var transaction = await origin.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken).ConfigureAwait(false))
        {
            expected = await ReadAggregatesAsync(origin, transaction, OriginAggregateSql, bootstrapId, cancellationToken).ConfigureAwait(false);
            expectedIds = await ReadIdsAsync(origin, transaction, OriginIdsSql, "id", bootstrapId, cancellationToken).ConfigureAwait(false);
            capture = await BootstrapStore.ReadCaptureAsync(origin, cancellationToken).ConfigureAwait(false);
            await using var count = new NpgsqlCommand(OutboxSetCountSql, origin, transaction);
            count.Parameters.AddWithValue("id", bootstrapId);
            outboxSetCount = (long)(await count.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) ?? 0L);
        }

        List<DayAggregate> actual;
        List<Guid> processedIds;
        BootstrapStateRow? state;
        await using (var projection = await BootstrapStore.OpenAsync(strings.Projection, cancellationToken).ConfigureAwait(false))
        await using (var transaction = await projection.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken).ConfigureAwait(false))
        {
            actual = await ReadProjectionAggregatesAsync(projection, transaction, cancellationToken).ConfigureAwait(false);
            processedIds = await ReadIdsAsync(projection, transaction, "SELECT event_id FROM processed_events", null, null, cancellationToken).ConfigureAwait(false);
            state = await BootstrapStore.ReadStateAsync(projection, cancellationToken).ConfigureAwait(false);
        }

        return ReconciliationComparer.VerifyDescriptor(bootstrapId, capture, state, expectedIds, outboxSetCount, expected, actual, processedIds)
            ?? ReconciliationComparer.Compare(expected, actual, expectedIds, processedIds);
    }

    private static async Task<List<DayAggregate>> ReadAggregatesAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, string sql, Guid bootstrapId, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("id", bootstrapId);
        command.Parameters.AddWithValue("zone", BusinessDay.TimeZoneId);
        var rows = new List<DayAggregate>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(new DayAggregate(reader.GetGuid(0), reader.GetFieldValue<DateOnly>(1), reader.GetInt64(2), reader.GetDecimal(3), reader.GetDecimal(4)));
        }

        return rows;
    }

    private static async Task<List<DayAggregate>> ReadProjectionAggregatesAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "SELECT account_id, business_day, movement_count, credits, debits FROM daily_account_consolidation", connection, transaction);
        var rows = new List<DayAggregate>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(new DayAggregate(reader.GetGuid(0), reader.GetFieldValue<DateOnly>(1), reader.GetInt64(2), reader.GetDecimal(3), reader.GetDecimal(4)));
        }

        return rows;
    }

    private static async Task<List<Guid>> ReadIdsAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, string sql, string? parameterName, Guid? parameterValue, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        if (parameterName is not null && parameterValue is { } value)
        {
            command.Parameters.AddWithValue(parameterName, value);
        }

        var ids = new List<Guid>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            ids.Add(reader.GetGuid(0));
        }

        return ids;
    }
}
