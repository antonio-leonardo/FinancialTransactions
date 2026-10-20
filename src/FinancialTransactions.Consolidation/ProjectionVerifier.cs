using System.Data;
using System.Globalization;
using Npgsql;

namespace FinancialTransactions.Consolidation;

/// <summary>Alcance da verificação financeira.</summary>
public enum VerifyMode
{
    /// <summary>Projeção viva: os recibos do destino (lidos num snapshot) definem os movimentos esperados. Não prova que tudo o que existe na origem já chegou.</summary>
    Live,

    /// <summary>Convergência: com produtor encerrado e nada em voo, TODOS os movimentos do conjunto declarado precisam ter recibo; sem pendente nem quarentena.</summary>
    Convergence,

    /// <summary>Reconstrução: o conjunto selado R (no destino novo) define o esperado, e os recibos precisam ser exatamente R.</summary>
    Rebuild,
}

/// <summary>Tipos fechados de divergência, para o JSON e para os testes causais.</summary>
public static class DeviationKinds
{
    public const string ReceiptWithoutMovement = "receipt_without_movement";
    public const string ReceiptAccountMismatch = "receipt_account_mismatch";
    public const string SequenceMismatch = "sequence_mismatch";
    public const string ValueMismatch = "value_mismatch";
    public const string ClosingMismatch = "closing_mismatch";
    public const string MissingAggregate = "missing_aggregate";
    public const string ExtraAggregate = "extra_aggregate";
    public const string MissingReceipt = "missing_receipt";
    public const string PendingInScope = "pending_in_scope";
    public const string WorkInFlight = "work_in_flight";
    public const string DescriptorMismatch = "descriptor_mismatch";
    public const string ReceiptNotInCapture = "receipt_not_in_capture";
    public const string CaptureItemWithoutReceipt = "capture_item_without_receipt";
    public const string CaptureDigestMismatch = "capture_digest_mismatch";
    public const string HistoricalMemberMissing = "historical_member_missing";
    public const string CaptureNotSealed = "capture_not_sealed";
}

public sealed record Deviation(string Kind, string Detail, Guid? AccountId = null, string? Day = null, Guid? EventId = null);

public sealed record VerificationCounts(
    long Receipts,
    long MovementsMatched,
    long AggregatesProjection,
    long AggregatesExpected,
    long PendingInScope,
    long QuarantinedInScope,
    long ValidLeases,
    long? CapturedItems);

public sealed record VerificationReport(
    string Mode,
    bool Consistent,
    string Conclusion,
    string Scope,
    DateTimeOffset ObservedAt,
    string? DestinationDatabase,
    string? DestinationPhase,
    Guid? DestinationBootstrapId,
    long? DestinationMemberCount,
    string? RebuildState,
    Guid? CaptureBootstrapId,
    long? CaptureMemberCount,
    VerificationCounts Counts,
    IReadOnlyDictionary<string, long> DeviationCountsByKind,
    IReadOnlyList<Deviation> Deviations,
    bool Truncated,
    string DayComputation);

/// <summary>
/// Verificação financeira SOMENTE LEITURA da projeção. O destino é lido num snapshot (REPEATABLE READ, READ ONLY) e a origem em outro, depois:
/// os recibos T definem os movimentos esperados e a referência vem dos movimentos imutáveis da origem, agregados em SQL com a zona nomeada
/// do PostgreSQL. Não usa o applier, <c>BusinessDay.Of</c> nem a API. Valores armazenados (inclusive <c>net</c>) são lidos como estão, sem
/// recomputar o lado obtido. Igualdade de soma global nunca basta: a comparação é por conta e dia, por identidade e por fechamento.
/// </summary>
public sealed class ProjectionVerifier(IConsolidationConnectionFactory connections)
{
    public const int MaxListedDeviations = 50;

    private sealed record Receipt(Guid EventId, long Sequence, Guid AccountId);

    private sealed record AggregateRow(Guid AccountId, DateOnly Day, long Count, decimal Credits, decimal Debits, decimal Net, long? MaxSequence);

    private sealed record MovementRow(Guid Id, Guid AccountId, long? Sequence);

    public async Task<VerificationReport> VerifyAsync(VerifyMode mode, CancellationToken cancellationToken)
    {
        var role = mode == VerifyMode.Rebuild ? ConsolidationConnectionRole.Rebuild : ConsolidationConnectionRole.Verify;
        var strings = connections.Open(role);
        var deviations = new List<Deviation>();

        // ---- 1. destino, num snapshot só de leitura ----
        List<Receipt> receipts;
        List<AggregateRow> projection;
        BootstrapStateRow? state;
        string? database;
        RebuildOperation? rebuild = null;
        List<Guid> captureIds = [];
        string? recomputedDigest = null;
        await using (var destination = await BootstrapStore.OpenAsync(strings.Projection, cancellationToken).ConfigureAwait(false))
        await using (var tx = await BeginReadOnlyAsync(destination, cancellationToken).ConfigureAwait(false))
        {
            database = await ScalarAsync<string>(destination, tx, "SELECT current_database()", cancellationToken).ConfigureAwait(false);
            receipts = await ReadReceiptsAsync(destination, tx, cancellationToken).ConfigureAwait(false);
            projection = await ReadProjectionAsync(destination, tx, cancellationToken).ConfigureAwait(false);
            state = await ReadStateAsync(destination, tx, cancellationToken).ConfigureAwait(false);
            rebuild = await ReadRebuildAsync(destination, tx, cancellationToken).ConfigureAwait(false);
            if (mode == VerifyMode.Rebuild)
            {
                captureIds = await ReadGuidsAsync(destination, tx, "SELECT event_id FROM rebuild_capture_item ORDER BY event_id", cancellationToken).ConfigureAwait(false);
                recomputedDigest = await ScalarAsync<string>(
                    destination,
                    tx,
                    "SELECT encode(sha256(convert_to(COALESCE(string_agg(event_id::text, ',' ORDER BY event_id), ''), 'UTF8')), 'hex') FROM rebuild_capture_item",
                    cancellationToken).ConfigureAwait(false);
            }
        }

        // ---- 2. origem, noutro snapshot só de leitura (depois do destino) ----
        List<MovementRow> found;
        List<AggregateRow> expected;
        CaptureRow? capture;
        long outboxSetCount;
        long pending = 0, quarantined = 0, validLeases;
        List<Guid> scopeWithoutReceipt = [];
        List<Guid> historicalMembersMissingFromR = [];
        DateTimeOffset observedAt;
        var expectedIds = mode switch
        {
            VerifyMode.Rebuild => captureIds,
            _ => receipts.Select(r => r.EventId).ToList(),
        };
        await using (var origin = await BootstrapStore.OpenAsync(strings.Origin, cancellationToken).ConfigureAwait(false))
        await using (var tx = await BeginReadOnlyAsync(origin, cancellationToken).ConfigureAwait(false))
        {
            observedAt = await ScalarAsync<DateTimeOffset>(origin, tx, "SELECT statement_timestamp()", cancellationToken).ConfigureAwait(false);
            capture = await ReadCaptureAsync(origin, tx, cancellationToken).ConfigureAwait(false);
            outboxSetCount = capture is null
                ? 0
                : await ScalarAsync<long>(origin, tx, "SELECT COUNT(*) FROM consolidation_outbox WHERE bootstrap_id = @id", cancellationToken, ("id", capture.BootstrapId)).ConfigureAwait(false);
            validLeases = await ScalarAsync<long>(origin, tx, "SELECT COUNT(*) FROM consolidation_outbox WHERE status = 0 AND lease_token IS NOT NULL AND lease_expires_at > statement_timestamp()", cancellationToken).ConfigureAwait(false);
            found = await ReadMovementsAsync(origin, tx, expectedIds, cancellationToken).ConfigureAwait(false);

            if (mode == VerifyMode.Convergence)
            {
                // Escopo declarado: todos os movimentos da origem. A referência agregada cobre todos eles, e não só os que têm recibo.
                expected = await ReadExpectedAggregatesAsync(origin, tx, null, cancellationToken).ConfigureAwait(false);
                var receiptSet = receipts.Select(r => r.EventId).ToHashSet();
                scopeWithoutReceipt = (await ReadGuidsAsync(origin, tx, "SELECT id FROM movements ORDER BY id", cancellationToken).ConfigureAwait(false))
                    .Where(id => !receiptSet.Contains(id))
                    .ToList();
                (pending, quarantined) = await ReadPendingAsync(origin, tx, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                expected = await ReadExpectedAggregatesAsync(origin, tx, expectedIds, cancellationToken).ConfigureAwait(false);
            }

            if (mode == VerifyMode.Rebuild && rebuild?.OriginBootstrapId is { } historicalId)
            {
                var captureSet = captureIds.ToHashSet();
                historicalMembersMissingFromR = (await ReadGuidsAsync(
                        origin,
                        tx,
                        "SELECT event_id FROM consolidation_outbox WHERE bootstrap_id = @id ORDER BY event_id",
                        cancellationToken,
                        ("id", historicalId)).ConfigureAwait(false))
                    .Where(id => !captureSet.Contains(id))
                    .ToList();
            }
        }

        // ---- 3. comparações ----
        var movementById = found.ToDictionary(m => m.Id);
        foreach (var receipt in receipts)
        {
            if (!movementById.TryGetValue(receipt.EventId, out var movement))
            {
                deviations.Add(new Deviation(DeviationKinds.ReceiptWithoutMovement, "recibo sem movimento correspondente na origem", receipt.AccountId, null, receipt.EventId));
                continue;
            }

            if (movement.AccountId != receipt.AccountId)
            {
                deviations.Add(new Deviation(DeviationKinds.ReceiptAccountMismatch, "a conta do recibo difere da conta do movimento", receipt.AccountId, null, receipt.EventId));
            }

            if (movement.Sequence is { } sequence && sequence != receipt.Sequence)
            {
                deviations.Add(new Deviation(DeviationKinds.SequenceMismatch, $"sequência do recibo {receipt.Sequence}, da Outbox {sequence}", receipt.AccountId, null, receipt.EventId));
            }
        }

        CompareAggregates(expected, projection, deviations);

        // Descritor: origem e destino concordam, e a cardinalidade real do conjunto identificado concorda com eles (inclusive em zero).
        if (state is not null)
        {
            if (capture is null)
            {
                deviations.Add(new Deviation(DeviationKinds.DescriptorMismatch, "o destino tem linha de bootstrap, mas a origem não tem registro de controle"));
            }
            else if (capture.BootstrapId != state.BootstrapId || capture.MemberCount != state.MemberCount)
            {
                deviations.Add(new Deviation(DeviationKinds.DescriptorMismatch, $"descritor diverge: origem {capture.BootstrapId}/{capture.MemberCount}, destino {state.BootstrapId}/{state.MemberCount}"));
            }
            else if (outboxSetCount != capture.MemberCount)
            {
                deviations.Add(new Deviation(DeviationKinds.DescriptorMismatch, $"descritor declara {capture.MemberCount} membros; o conjunto identificado na Outbox tem {outboxSetCount}"));
            }
        }

        if (mode == VerifyMode.Convergence)
        {
            foreach (var id in scopeWithoutReceipt)
            {
                deviations.Add(new Deviation(DeviationKinds.MissingReceipt, "movimento do escopo sem recibo no destino", null, null, id));
            }

            if (pending + quarantined > 0)
            {
                deviations.Add(new Deviation(DeviationKinds.PendingInScope, $"{pending} pendente(s) e {quarantined} em quarentena na Outbox: a convergência ainda não é verificável"));
            }

            if (validLeases > 0)
            {
                deviations.Add(new Deviation(DeviationKinds.WorkInFlight, $"{validLeases} reserva(s) válida(s): há trabalho em voo"));
            }
        }

        if (mode == VerifyMode.Rebuild)
        {
            if (rebuild is null || rebuild.State is RebuildStates.Prepared or RebuildStates.Cancelled)
            {
                deviations.Add(new Deviation(DeviationKinds.CaptureNotSealed, "o conjunto R ainda não foi selado neste destino"));
            }
            else
            {
                var captureSet = captureIds.ToHashSet();
                var receiptSet = receipts.Select(r => r.EventId).ToHashSet();
                foreach (var id in receiptSet.Where(id => !captureSet.Contains(id)))
                {
                    deviations.Add(new Deviation(DeviationKinds.ReceiptNotInCapture, "recibo fora do conjunto selado R", null, null, id));
                }

                foreach (var id in captureSet.Where(id => !receiptSet.Contains(id)))
                {
                    deviations.Add(new Deviation(DeviationKinds.CaptureItemWithoutReceipt, "item de R sem recibo (replay incompleto)", null, null, id));
                }

                if (rebuild.CaptureCount != captureIds.Count || !string.Equals(rebuild.CaptureDigest, recomputedDigest, StringComparison.Ordinal))
                {
                    deviations.Add(new Deviation(DeviationKinds.CaptureDigestMismatch, $"contagem/digest selados ({rebuild.CaptureCount}) diferem do conjunto atual ({captureIds.Count})"));
                }

                foreach (var id in historicalMembersMissingFromR)
                {
                    deviations.Add(new Deviation(DeviationKinds.HistoricalMemberMissing, "membro do conjunto histórico do bootstrap fora de R", null, null, id));
                }

                if (rebuild.OriginMemberCount is { } historicalCount && capture is not null && (capture.MemberCount != historicalCount || capture.BootstrapId != rebuild.OriginBootstrapId))
                {
                    deviations.Add(new Deviation(DeviationKinds.DescriptorMismatch, "o descritor histórico gravado na reconstrução difere do registro de controle da origem"));
                }
            }
        }

        var byKind = deviations.GroupBy(d => d.Kind).ToDictionary(g => g.Key, g => (long)g.Count());
        var consistent = deviations.Count == 0;
        var counts = new VerificationCounts(
            receipts.Count,
            found.Count,
            projection.Count,
            expected.Count,
            pending,
            quarantined,
            validLeases,
            mode == VerifyMode.Rebuild ? captureIds.Count : null);
        return new VerificationReport(
            mode.ToString().ToLowerInvariant(),
            consistent,
            Conclusion(mode, consistent),
            ScopeOf(mode),
            observedAt,
            database,
            state?.Phase,
            state?.BootstrapId,
            state?.MemberCount,
            rebuild?.State,
            capture?.BootstrapId,
            capture?.MemberCount,
            counts,
            byKind,
            deviations.Take(MaxListedDeviations).ToList(),
            deviations.Count > MaxListedDeviations,
            "PostgreSQL (effective_at AT TIME ZONE 'America/Sao_Paulo')::date; o .NET e o PostgreSQL divergem no dia de negócio apenas em 2000 a 2003 (ADR-12)");
    }

    private static string ScopeOf(VerifyMode mode) => mode switch
    {
        VerifyMode.Live => "movimentos correspondentes aos recibos do destino (snapshot do destino, depois snapshot da origem)",
        VerifyMode.Convergence => "todos os movimentos da origem contra os recibos do destino, sem pendente, quarentena nem trabalho em voo",
        _ => "conjunto R selado no destino novo contra os movimentos da origem e o descritor histórico do bootstrap",
    };

    private static string Conclusion(VerifyMode mode, bool consistent) => (mode, consistent) switch
    {
        (VerifyMode.Live, true) => "Coerente: a projeção observada bate com os movimentos imutáveis dos seus recibos. Não prova que todo evento novo da origem já chegou.",
        (VerifyMode.Convergence, true) => "Convergente: todos os movimentos do escopo estão refletidos, sem pendente, quarentena nem trabalho em voo.",
        (VerifyMode.Rebuild, true) => "Reconciliado: os recibos são exatamente o conjunto R selado, e valores, fechamentos e descritor histórico conferem com a origem.",
        _ => "DIVERGENTE: veja as divergências; nada foi corrigido.",
    };

    private static void CompareAggregates(List<AggregateRow> expected, List<AggregateRow> projection, List<Deviation> deviations)
    {
        var expectedMap = expected.ToDictionary(a => (a.AccountId, a.Day));
        var projectionMap = projection.ToDictionary(a => (a.AccountId, a.Day));
        foreach (var (key, e) in expectedMap.OrderBy(k => k.Key.AccountId).ThenBy(k => k.Key.Day))
        {
            var day = key.Day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            if (!projectionMap.TryGetValue(key, out var p))
            {
                deviations.Add(new Deviation(DeviationKinds.MissingAggregate, "agregado esperado ausente na projeção", key.AccountId, day));
                continue;
            }

            var fields = new List<string>();
            if (e.Count != p.Count)
            {
                fields.Add($"movement_count esperado {e.Count}, obtido {p.Count}");
            }

            if (e.Credits != p.Credits)
            {
                fields.Add($"credits esperado {e.Credits}, obtido {p.Credits}");
            }

            if (e.Debits != p.Debits)
            {
                fields.Add($"debits esperado {e.Debits}, obtido {p.Debits}");
            }

            if (e.Net != p.Net)
            {
                fields.Add($"net esperado {e.Net}, obtido {p.Net}");
            }

            if (e.MaxSequence is { } maxSequence && maxSequence != p.MaxSequence)
            {
                fields.Add($"max_applied_sequence esperado {maxSequence}, obtido {p.MaxSequence}");
            }

            if (fields.Count > 0)
            {
                deviations.Add(new Deviation(DeviationKinds.ValueMismatch, string.Join("; ", fields), key.AccountId, day));
            }
        }

        foreach (var (key, _) in projectionMap.Where(k => !expectedMap.ContainsKey(k.Key)).OrderBy(k => k.Key.AccountId).ThenBy(k => k.Key.Day))
        {
            deviations.Add(new Deviation(DeviationKinds.ExtraAggregate, "agregado na projeção sem movimento correspondente", key.AccountId, key.Day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)));
        }

        // Fechamento derivado: soma acumulada do net por conta, dos dois lados, usando o net ARMAZENADO na projeção.
        foreach (var account in expected.Select(a => a.AccountId).Union(projection.Select(a => a.AccountId)).Distinct().OrderBy(id => id))
        {
            decimal closeExpected = 0m, closeProjection = 0m;
            var days = expected.Where(a => a.AccountId == account).Select(a => a.Day).Union(projection.Where(a => a.AccountId == account).Select(a => a.Day)).Distinct().OrderBy(d => d);
            foreach (var day in days)
            {
                closeExpected += expectedMap.TryGetValue((account, day), out var e) ? e.Net : 0m;
                closeProjection += projectionMap.TryGetValue((account, day), out var p) ? p.Net : 0m;
                if (closeExpected != closeProjection)
                {
                    deviations.Add(new Deviation(DeviationKinds.ClosingMismatch, $"fechamento acumulado esperado {closeExpected}, obtido {closeProjection}", account, day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)));
                }
            }
        }
    }

    // ---------- leitura ----------

    private static async Task<NpgsqlTransaction> BeginReadOnlyAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        var tx = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken).ConfigureAwait(false);
        await using var readOnly = new NpgsqlCommand("SET TRANSACTION READ ONLY", connection, tx);
        await readOnly.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return tx;
    }

    private static async Task<T?> ScalarAsync<T>(NpgsqlConnection connection, NpgsqlTransaction tx, string sql, CancellationToken cancellationToken, params (string Name, object Value)[] parameters)
    {
        await using var command = new NpgsqlCommand(sql, connection, tx);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (result is null or DBNull)
        {
            return default;
        }

        if (typeof(T) == typeof(DateTimeOffset) && result is DateTime instant)
        {
            return (T)(object)new DateTimeOffset(DateTime.SpecifyKind(instant, DateTimeKind.Utc));
        }

        return result is T typed ? typed : (T)Convert.ChangeType(result, typeof(T), CultureInfo.InvariantCulture);
    }

    private static async Task<List<Guid>> ReadGuidsAsync(NpgsqlConnection connection, NpgsqlTransaction tx, string sql, CancellationToken cancellationToken, params (string Name, object Value)[] parameters)
    {
        await using var command = new NpgsqlCommand(sql, connection, tx);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        var ids = new List<Guid>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            ids.Add(reader.GetGuid(0));
        }

        return ids;
    }

    private static async Task<List<Receipt>> ReadReceiptsAsync(NpgsqlConnection connection, NpgsqlTransaction tx, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("SELECT event_id, sequence, account_id FROM processed_events ORDER BY event_id", connection, tx);
        var rows = new List<Receipt>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(new Receipt(reader.GetGuid(0), reader.GetInt64(1), reader.GetGuid(2)));
        }

        return rows;
    }

    private static async Task<List<AggregateRow>> ReadProjectionAsync(NpgsqlConnection connection, NpgsqlTransaction tx, CancellationToken cancellationToken)
    {
        // net e max_applied_sequence como estão armazenados: nada é recomputado para esconder uma divergência.
        await using var command = new NpgsqlCommand(
            "SELECT account_id, business_day, movement_count, credits, debits, net, max_applied_sequence FROM daily_account_consolidation ORDER BY account_id, business_day",
            connection,
            tx);
        var rows = new List<AggregateRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(new AggregateRow(reader.GetGuid(0), reader.GetFieldValue<DateOnly>(1), reader.GetInt64(2), reader.GetDecimal(3), reader.GetDecimal(4), reader.GetDecimal(5), reader.GetInt64(6)));
        }

        return rows;
    }

    private static async Task<BootstrapStateRow?> ReadStateAsync(NpgsqlConnection connection, NpgsqlTransaction tx, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("SELECT bootstrap_id, phase, member_count, captured_at, applied_at, reconciled_at FROM consolidation_bootstrap WHERE id = 1", connection, tx);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new BootstrapStateRow(
                reader.GetGuid(0),
                reader.GetString(1),
                reader.GetInt64(2),
                reader.GetFieldValue<DateTimeOffset>(3),
                reader.IsDBNull(4) ? null : reader.GetFieldValue<DateTimeOffset>(4),
                reader.IsDBNull(5) ? null : reader.GetFieldValue<DateTimeOffset>(5))
            : null;
    }

    private static async Task<RebuildOperation?> ReadRebuildAsync(NpgsqlConnection connection, NpgsqlTransaction tx, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "SELECT projection_id, operation_label, source_database, state, origin_bootstrap_id, origin_member_count, capture_count, capture_digest FROM rebuild_operation WHERE id = 1",
            connection,
            tx);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new RebuildOperation
            {
                ProjectionId = reader.GetGuid(0),
                OperationLabel = reader.GetString(1),
                SourceDatabase = reader.GetString(2),
                State = reader.GetString(3),
                OriginBootstrapId = reader.IsDBNull(4) ? null : reader.GetGuid(4),
                OriginMemberCount = reader.IsDBNull(5) ? null : reader.GetInt64(5),
                CaptureCount = reader.IsDBNull(6) ? null : reader.GetInt64(6),
                CaptureDigest = reader.IsDBNull(7) ? null : reader.GetString(7),
            }
            : null;
    }

    private static async Task<CaptureRow?> ReadCaptureAsync(NpgsqlConnection connection, NpgsqlTransaction tx, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("SELECT bootstrap_id, member_count, captured_at, snapshot FROM consolidation_bootstrap_capture WHERE id = 1", connection, tx);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new CaptureRow(reader.GetGuid(0), reader.GetInt64(1), reader.GetFieldValue<DateTimeOffset>(2), reader.GetString(3))
            : null;
    }

    private static async Task<List<MovementRow>> ReadMovementsAsync(NpgsqlConnection connection, NpgsqlTransaction tx, IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "SELECT m.id, m.account_id, o.sequence FROM movements m LEFT JOIN consolidation_outbox o ON o.event_id = m.id WHERE m.id = ANY(@ids)",
            connection,
            tx);
        command.Parameters.AddWithValue("ids", ids.ToArray());
        var rows = new List<MovementRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(new MovementRow(reader.GetGuid(0), reader.GetGuid(1), reader.IsDBNull(2) ? null : reader.GetInt64(2)));
        }

        return rows;
    }

    private static async Task<List<AggregateRow>> ReadExpectedAggregatesAsync(NpgsqlConnection connection, NpgsqlTransaction tx, IReadOnlyCollection<Guid>? ids, CancellationToken cancellationToken)
    {
        // Referência independente: movimentos imutáveis da origem, dia pela zona nomeada do próprio PostgreSQL, agregação em SQL.
        var sql = """
            SELECT m.account_id,
                   (m.effective_at AT TIME ZONE @zone)::date AS day,
                   COUNT(*),
                   COALESCE(SUM(CASE WHEN m.type = 1 THEN m.amount ELSE 0 END), 0),
                   COALESCE(SUM(CASE WHEN m.type = 2 THEN m.amount ELSE 0 END), 0),
                   MAX(o.sequence)
            FROM movements m
            LEFT JOIN consolidation_outbox o ON o.event_id = m.id
            """ + (ids is null ? string.Empty : "\nWHERE m.id = ANY(@ids)") + "\nGROUP BY 1, 2";
        await using var command = new NpgsqlCommand(sql, connection, tx);
        command.Parameters.AddWithValue("zone", BusinessDay.TimeZoneId);
        if (ids is not null)
        {
            command.Parameters.AddWithValue("ids", ids.ToArray());
        }

        var rows = new List<AggregateRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var credits = reader.GetDecimal(3);
            var debits = reader.GetDecimal(4);
            rows.Add(new AggregateRow(reader.GetGuid(0), reader.GetFieldValue<DateOnly>(1), reader.GetInt64(2), credits, debits, credits - debits, reader.IsDBNull(5) ? null : reader.GetInt64(5)));
        }

        return rows;
    }

    private static async Task<(long Pending, long Quarantined)> ReadPendingAsync(NpgsqlConnection connection, NpgsqlTransaction tx, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("SELECT COUNT(*) FILTER (WHERE status = 0), COUNT(*) FILTER (WHERE status = 2) FROM consolidation_outbox", connection, tx);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        return (reader.GetInt64(0), reader.GetInt64(1));
    }
}
