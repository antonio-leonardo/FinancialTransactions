using System.Data;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;

namespace FinancialTransactions.Consolidation;

/// <summary>Pontos de pausa e de falha da reconstrução, registrados só no host de teste; produção usa <see cref="NoRebuildFailurePoints"/>.</summary>
public interface IRebuildFailurePoints
{
    /// <summary>Snapshot da origem estabelecido, antes de copiar o conjunto R.</summary>
    ValueTask AfterSnapshotEstablishedAsync(CancellationToken cancellationToken);

    /// <summary>R copiado para a transação do destino, antes de selar a captura e confirmar.</summary>
    ValueTask AfterCaptureCopyBeforeSealAsync(CancellationToken cancellationToken);

    /// <summary>Captura selada e confirmada no destino, antes de devolver o resultado.</summary>
    ValueTask AfterSealCommitAsync(CancellationToken cancellationToken);

    /// <summary>Item de R aplicado e confirmado no destino. <paramref name="appliedInThisRun"/> conta os aplicados nesta execução.</summary>
    ValueTask AfterReplayItemAsync(long appliedInThisRun, CancellationToken cancellationToken);

    /// <summary>Ponto nomeado de sincronização do teste (ver <see cref="RebuildPoints"/>), imediatamente antes da primeira escrita do trecho.</summary>
    ValueTask ReachedAsync(string point, CancellationToken cancellationToken);
}

/// <summary>Nomes dos pontos de <see cref="IRebuildFailurePoints.ReachedAsync"/>.</summary>
public static class RebuildPoints
{
    /// <summary>Captura: pré-condições lidas, antes de preparar a operação.</summary>
    public const string CaptureBeforePrepare = "capture.before-prepare";

    /// <summary>Captura: operação preparada, antes de abrir o snapshot da origem.</summary>
    public const string CaptureBeforeSnapshot = "capture.before-snapshot";

    /// <summary>Cancelamento: decisão tomada, antes da atualização do estado.</summary>
    public const string CancelBeforeUpdate = "cancel.before-update";

    /// <summary>Promoção: registro da projeção ativa confirmado na origem, marca local ainda não gravada.</summary>
    public const string PromoteAfterRegistry = "promote.after-registry";
}

public sealed class NoRebuildFailurePoints : IRebuildFailurePoints
{
    public ValueTask AfterSnapshotEstablishedAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

    public ValueTask AfterCaptureCopyBeforeSealAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

    public ValueTask AfterSealCommitAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

    public ValueTask AfterReplayItemAsync(long appliedInThisRun, CancellationToken cancellationToken) => ValueTask.CompletedTask;

    public ValueTask ReachedAsync(string point, CancellationToken cancellationToken) => ValueTask.CompletedTask;
}

public sealed record RebuildRequest(string OperationLabel, string ConfirmTargetDatabase, string? ConfirmPreviousDatabase = null);

public sealed record RebuildOutcome(int ExitCode, object Body);

/// <summary>
/// Reconstrução da projeção em BANCO NOVO paralelo, em estágios retomáveis (capturar, reaplicar, reconciliar, habilitar, promover). O destino é o
/// que a connection string de projeção aponta; o banco antigo nunca é alterado nem apagado. A fonte de verdade são os movimentos imutáveis da
/// origem. Operação administrativa de laboratório: exige consumidores e APIs conhecidos parados e não oferece reconstrução online contínua,
/// troca sem indisponibilidade das consultas nem retorno transparente ao banco antigo.
/// </summary>
public sealed partial class RebuildCoordinator(
    IConsolidationConnectionFactory connections,
    ProjectionVerifier verifier,
    IRebuildFailurePoints failurePoints,
    IConsolidationFailurePoints applierFailurePoints,
    TimeProvider clock,
    ILogger<RebuildCoordinator> logger,
    long movementCeiling = RebuildCoordinator.DefaultMovementCeiling)
{
    /// <summary>
    /// Teto de movimentos desta versão, validado ANTES da cópia: a captura copia R numa única transação do destino, a partir de um snapshot
    /// longo da origem. A demonstração de referência é de 100.000 movimentos; acima do teto a captura é recusada, não truncada.
    /// </summary>
    public const long DefaultMovementCeiling = 250_000;

    private static readonly Regex LabelPattern = LabelRegex();

    private const string CaptureQuerySql = """
        SELECT m.id, m.account_id, a.customer_id, m.type, m.amount, m.effective_at, o.sequence,
               (o.sequence IS NOT NULL AND (o.type <> m.type OR o.amount <> m.amount OR o.effective_at <> m.effective_at OR o.account_id <> m.account_id)) AS diverges
        FROM movements m
        JOIN accounts a ON a.id = m.account_id
        LEFT JOIN consolidation_outbox o ON o.event_id = m.id
        ORDER BY m.id
        """;

    // ---------------------------------------------------------------- plano (dry-run)

    public async Task<RebuildOutcome> PlanAsync(RebuildRequest request, CancellationToken cancellationToken)
    {
        var refusal = Validate(request);
        if (refusal is not null)
        {
            return Refused(refusal);
        }

        try
        {
            var strings = connections.Open(ConsolidationConnectionRole.Rebuild);
            var refusals = new List<string>();
            var facts = await GatherAsync(strings, request, refusals, cancellationToken).ConfigureAwait(false);
            var op = await ReadOperationOrNullAsync(strings.Projection, cancellationToken).ConfigureAwait(false);
            return new RebuildOutcome(
                refusals.Count == 0 ? AdminExitCodes.Success : AdminExitCodes.Conflict,
                new
                {
                    stage = "plan",
                    mode = "dry-run",
                    operationLabel = request.OperationLabel,
                    sourceDatabase = new NpgsqlConnectionStringBuilder(strings.Origin).Database,
                    targetDatabase = new NpgsqlConnectionStringBuilder(strings.Projection).Database,
                    state = op?.State ?? "absent",
                    facts,
                    refusals,
                    note = "Dry-run: nada foi alterado. A reconstrução exige consumidores e APIs conhecidos parados; não é reconstrução online contínua.",
                });
        }
        catch (Exception ex) when (IsUnreachable(ex))
        {
            return Unreachable("plan", ex);
        }
    }

    public async Task<RebuildOutcome> StatusAsync(CancellationToken cancellationToken)
    {
        try
        {
            var strings = connections.Open(ConsolidationConnectionRole.Rebuild);
            await using var destination = await BootstrapStore.OpenAsync(strings.Projection, cancellationToken).ConfigureAwait(false);
            var op = await ReadOperationAsync(destination, cancellationToken).ConfigureAwait(false);
            var items = await ScalarAsync<long>(destination, "SELECT COUNT(*) FROM rebuild_capture_item", cancellationToken).ConfigureAwait(false);
            var receipts = await ScalarAsync<long>(destination, "SELECT COUNT(*) FROM processed_events", cancellationToken).ConfigureAwait(false);
            return new RebuildOutcome(
                AdminExitCodes.Success,
                new
                {
                    stage = "status",
                    state = op?.State ?? "absent",
                    operationLabel = op?.OperationLabel,
                    projectionId = op?.ProjectionId,
                    captureCount = op?.CaptureCount,
                    captureDigest = op?.CaptureDigest,
                    payloadDivergences = op?.PayloadDivergences,
                    capturedItems = items,
                    receipts,
                    pendingReplay = Math.Max(0, items - receipts),
                });
        }
        catch (Exception ex) when (IsUnreachable(ex))
        {
            return Unreachable("status", ex);
        }
    }

    // ---------------------------------------------------------------- captura

    public async Task<RebuildOutcome> CaptureAsync(RebuildRequest request, CancellationToken cancellationToken)
    {
        var refusal = Validate(request);
        if (refusal is not null)
        {
            return Refused(refusal);
        }

        try
        {
            var strings = connections.Open(ConsolidationConnectionRole.Rebuild);
            await using var lease = await AdminLease.TryAcquireAsync(strings.Projection, logger, cancellationToken).ConfigureAwait(false);
            if (lease is null)
            {
                return Busy("capture");
            }

            // Identidade, origem e estado só são lidos DEPOIS da exclusividade: quem perdeu a disputa não toca a operação alheia.
            var existing = await ReadOperationOrNullAsync(strings.Projection, cancellationToken).ConfigureAwait(false);
            if (existing is not null && !string.Equals(existing.OperationLabel, request.OperationLabel, StringComparison.Ordinal))
            {
                return Conflict("capture", $"o destino pertence a outra operação de reconstrução ({existing.OperationLabel})");
            }

            if (existing is not null && existing.State != RebuildStates.Prepared)
            {
                // Já selada: retoma exatamente o mesmo R. Nunca recaptura nem mistura duas capturas.
                return new RebuildOutcome(AdminExitCodes.Success, SealedBody("capture", existing, resumed: true));
            }

            if (existing is not null)
            {
                var originMismatch = await OriginMismatchAsync(strings.Origin, existing, cancellationToken).ConfigureAwait(false);
                if (originMismatch is not null)
                {
                    return Conflict("capture", originMismatch);
                }
            }

            var refusals = new List<string>();
            _ = await GatherAsync(strings, request, refusals, cancellationToken).ConfigureAwait(false);
            if (refusals.Count > 0)
            {
                return new RebuildOutcome(AdminExitCodes.Conflict, new { stage = "capture", state = existing?.State ?? "absent", refusals });
            }

            await failurePoints.ReachedAsync(RebuildPoints.CaptureBeforePrepare, cancellationToken).ConfigureAwait(false);
            var owner = await PrepareAsync(strings, request, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(owner, request.OperationLabel, StringComparison.Ordinal))
            {
                // Um INSERT ignorado por conflito não autoriza a capturar para outra identidade.
                return Conflict("capture", $"o destino pertence a outra operação de reconstrução ({owner})");
            }

            return await CaptureCoreAsync(strings, request, lease, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsUnreachable(ex))
        {
            return Unreachable("capture", ex);
        }
    }

    /// <summary>Prepara a operação (idempotente) e devolve o rótulo que de fato é dono do destino.</summary>
    private static async Task<string> PrepareAsync(ConsolidationConnectionStrings strings, RebuildRequest request, CancellationToken cancellationToken)
    {
        await using var destination = await BootstrapStore.OpenAsync(strings.Projection, cancellationToken).ConfigureAwait(false);
        await using var insert = new NpgsqlCommand(
            """
            INSERT INTO rebuild_operation (id, projection_id, operation_label, source_database, state, payload_divergences, created_at)
            VALUES (1, @projection, @label, @source, 'prepared', 0, statement_timestamp())
            ON CONFLICT (id) DO NOTHING
            """,
            destination);
        insert.Parameters.AddWithValue("projection", Guid.CreateVersion7());
        insert.Parameters.AddWithValue("label", request.OperationLabel);
        insert.Parameters.AddWithValue("source", new NpgsqlConnectionStringBuilder(strings.Origin).Database ?? string.Empty);
        await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return await ScalarAsync<string>(destination, "SELECT operation_label FROM rebuild_operation WHERE id = 1", cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("A operação de reconstrução sumiu depois da preparação.");
    }

    private async Task<RebuildOutcome> CaptureCoreAsync(ConsolidationConnectionStrings strings, RebuildRequest request, AdminLease lease, CancellationToken cancellationToken)
    {
        await failurePoints.ReachedAsync(RebuildPoints.CaptureBeforeSnapshot, cancellationToken).ConfigureAwait(false);
        await using var origin = await BootstrapStore.OpenAsync(strings.Origin, cancellationToken).ConfigureAwait(false);
        await using var originTx = await origin.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken).ConfigureAwait(false);
        await using (var readOnly = new NpgsqlCommand("SET TRANSACTION READ ONLY", origin, originTx))
        {
            await readOnly.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        // O primeiro comando de dados estabelece o snapshot.
        string snapshot;
        await using (var snapshotCommand = new NpgsqlCommand("SELECT pg_current_snapshot()::text", origin, originTx))
        {
            snapshot = (string)(await snapshotCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) ?? string.Empty);
        }

        // O teto é revalidado na MESMA visão que será enumerada (o preflight é só informativo): um lançamento confirmado entre os dois não passa.
        var inSnapshot = await ScalarAsync<long>(origin, "SELECT COUNT(*) FROM movements", cancellationToken, originTx).ConfigureAwait(false);
        if (inSnapshot > movementCeiling)
        {
            return Conflict("capture", $"{inSnapshot} movimentos na visão da captura excedem o teto declarado de {movementCeiling} desta versão; a captura não será truncada nem selada");
        }

        await failurePoints.AfterSnapshotEstablishedAsync(cancellationToken).ConfigureAwait(false);

        // Descritor histórico do bootstrap e sua cobertura por R, na MESMA visão da origem.
        var capture = await BootstrapStore.ReadCaptureAsync(origin, cancellationToken).ConfigureAwait(false);
        if (capture is null)
        {
            return Conflict("capture", "a origem não tem registro de controle do bootstrap; execute o coordenador (--backfill) antes de reconstruir");
        }

        long historicalMembers, historicalWithoutMovement;
        await using (var members = new NpgsqlCommand(
            """
            SELECT COUNT(*), COUNT(*) FILTER (WHERE NOT EXISTS (SELECT 1 FROM movements m WHERE m.id = o.event_id))
            FROM consolidation_outbox o WHERE o.bootstrap_id = @id
            """,
            origin,
            originTx))
        {
            members.Parameters.AddWithValue("id", capture.BootstrapId);
            await using var reader = await members.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            historicalMembers = reader.GetInt64(0);
            historicalWithoutMovement = reader.GetInt64(1);
        }

        if (historicalMembers != capture.MemberCount || historicalWithoutMovement > 0)
        {
            return Conflict("capture", $"o descritor histórico declara {capture.MemberCount} membros; a Outbox marca {historicalMembers} e {historicalWithoutMovement} deles não têm movimento");
        }

        await using var destination = await BootstrapStore.OpenAsync(strings.Projection, cancellationToken).ConfigureAwait(false);
        await using var destinationTx = await destination.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken).ConfigureAwait(false);
        string locked, lockedLabel;
        await using (var lockRow = new NpgsqlCommand("SELECT state, operation_label FROM rebuild_operation WHERE id = 1 FOR UPDATE", destination, destinationTx))
        await using (var lockReader = await lockRow.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!await lockReader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return Conflict("capture", "a operação de reconstrução sumiu antes da selagem");
            }

            locked = lockReader.GetString(0);
            lockedLabel = lockReader.GetString(1);
        }

        if (!string.Equals(lockedLabel, request.OperationLabel, StringComparison.Ordinal))
        {
            return Conflict("capture", $"o destino pertence a outra operação de reconstrução ({lockedLabel})");
        }

        if (locked != RebuildStates.Prepared)
        {
            return new RebuildOutcome(AdminExitCodes.Success, new { stage = "capture", state = locked, note = "outra execução selou a captura; nada a fazer" });
        }

        long missingOutbox = 0;
        await using (var writer = await destination.BeginBinaryImportAsync(
            "COPY rebuild_capture_item (event_id, account_id, customer_id, type, amount, effective_at, sequence, payload_diverges) FROM STDIN (FORMAT BINARY)",
            cancellationToken).ConfigureAwait(false))
        {
            await using var read = new NpgsqlCommand(CaptureQuerySql, origin, originTx);
            await using var reader = await read.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (reader.IsDBNull(6))
                {
                    missingOutbox++;
                    continue;
                }

                await writer.StartRowAsync(cancellationToken).ConfigureAwait(false);
                await writer.WriteAsync(reader.GetGuid(0), NpgsqlDbType.Uuid, cancellationToken).ConfigureAwait(false);
                await writer.WriteAsync(reader.GetGuid(1), NpgsqlDbType.Uuid, cancellationToken).ConfigureAwait(false);
                await writer.WriteAsync(reader.GetGuid(2), NpgsqlDbType.Uuid, cancellationToken).ConfigureAwait(false);
                await writer.WriteAsync(Convert.ToInt16(reader.GetValue(3), System.Globalization.CultureInfo.InvariantCulture), NpgsqlDbType.Smallint, cancellationToken).ConfigureAwait(false);
                await writer.WriteAsync(reader.GetDecimal(4), NpgsqlDbType.Numeric, cancellationToken).ConfigureAwait(false);
                await writer.WriteAsync(reader.GetFieldValue<DateTimeOffset>(5), NpgsqlDbType.TimestampTz, cancellationToken).ConfigureAwait(false);
                await writer.WriteAsync(reader.GetInt64(6), NpgsqlDbType.Bigint, cancellationToken).ConfigureAwait(false);
                await writer.WriteAsync(reader.GetBoolean(7), NpgsqlDbType.Boolean, cancellationToken).ConfigureAwait(false);
            }

            if (missingOutbox == 0)
            {
                await writer.CompleteAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        if (missingOutbox > 0)
        {
            await destinationTx.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return Conflict("capture", $"{missingOutbox} movimento(s) sem evento na Outbox; execute o coordenador (--backfill) e repita");
        }

        await failurePoints.AfterCaptureCopyBeforeSealAsync(cancellationToken).ConfigureAwait(false);

        // R e a marca "captura concluída" são confirmados na MESMA transação local do destino.
        long count, divergences;
        string digest;
        await using (var summary = new NpgsqlCommand(
            """
            SELECT COUNT(*), COUNT(*) FILTER (WHERE payload_diverges),
                   encode(sha256(convert_to(COALESCE(string_agg(event_id::text, ',' ORDER BY event_id), ''), 'UTF8')), 'hex')
            FROM rebuild_capture_item
            """,
            destination,
            destinationTx))
        {
            await using var reader = await summary.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            count = reader.GetInt64(0);
            divergences = reader.GetInt64(1);
            digest = reader.GetString(2);
        }

        await using (var seal = new NpgsqlCommand(
            """
            UPDATE rebuild_operation
            SET state = 'captured', capture_snapshot = @snapshot, capture_count = @count, capture_digest = @digest, payload_divergences = @divergences,
                origin_bootstrap_id = @bootstrap, origin_member_count = @members, origin_captured_at = @capturedAt, sealed_at = statement_timestamp()
            WHERE id = 1 AND state = 'prepared'
            """,
            destination,
            destinationTx))
        {
            seal.Parameters.AddWithValue("snapshot", snapshot);
            seal.Parameters.AddWithValue("count", count);
            seal.Parameters.AddWithValue("digest", digest);
            seal.Parameters.AddWithValue("divergences", divergences);
            seal.Parameters.AddWithValue("bootstrap", capture.BootstrapId);
            seal.Parameters.AddWithValue("members", capture.MemberCount);
            seal.Parameters.AddWithValue("capturedAt", capture.CapturedAt);
            if (await seal.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                await destinationTx.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return Conflict("capture", "a captura não estava mais no estado preparado");
            }
        }

        if (!await lease.IsStillHeldAsync(cancellationToken).ConfigureAwait(false))
        {
            await destinationTx.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return Conflict("capture", "a exclusividade administrativa foi perdida antes da selagem; nada foi selado");
        }

        try
        {
            await destinationTx.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Resultado do commit desconhecido: não se presume rollback. Relê a operação antes de decidir.
            LogCommitUnknown(logger, ex.GetType().Name);
            var reread = await ReadOperationOrNullAsync(strings.Projection, CancellationToken.None).ConfigureAwait(false);
            if (reread is null || reread.State == RebuildStates.Prepared)
            {
                return new RebuildOutcome(AdminExitCodes.CommitUnknown, new { stage = "capture", state = reread?.State ?? "absent", error = "commit_unknown", note = "Nada foi selado; repetir o comando recaptura em visão nova." });
            }
        }

        await failurePoints.AfterSealCommitAsync(cancellationToken).ConfigureAwait(false);
        var sealedOp = await ReadOperationOrNullAsync(strings.Projection, cancellationToken).ConfigureAwait(false);
        return new RebuildOutcome(AdminExitCodes.Success, SealedBody("capture", sealedOp ?? throw new InvalidOperationException("A operação de reconstrução sumiu depois da selagem."), resumed: false));
    }

    private static object SealedBody(string stage, RebuildOperation op, bool resumed) => new
    {
        stage,
        state = op.State,
        resumed,
        captureCount = op.CaptureCount,
        captureDigest = op.CaptureDigest,
        captureSnapshot = op.CaptureSnapshot,
        payloadDivergences = op.PayloadDivergences,
        historicalDescriptor = new { bootstrapId = op.OriginBootstrapId, memberCount = op.OriginMemberCount },
    };

    // ---------------------------------------------------------------- replay

    public async Task<RebuildOutcome> ReplayAsync(RebuildRequest request, CancellationToken cancellationToken)
    {
        var refusal = Validate(request);
        if (refusal is not null)
        {
            return Refused(refusal);
        }

        try
        {
            var strings = connections.Open(ConsolidationConnectionRole.Rebuild);
            await using var lease = await AdminLease.TryAcquireAsync(strings.Projection, logger, cancellationToken).ConfigureAwait(false);
            if (lease is null)
            {
                return Busy("replay");
            }

            var op = await ReadOperationOrNullAsync(strings.Projection, cancellationToken).ConfigureAwait(false);
            if (op is null || op.OperationLabel != request.OperationLabel)
            {
                return Conflict("replay", "não há reconstrução desta operação neste destino");
            }

            if (op.State is RebuildStates.Prepared or RebuildStates.Cancelled)
            {
                return Conflict("replay", $"a captura ainda não foi selada (estado {op.State})");
            }

            long applied = 0, alreadyApplied = 0;
            var options = new DbContextOptionsBuilder<ConsolidationDbContext>().UseNpgsql(strings.Projection).Options;
            while (true)
            {
                var batch = await NextBatchAsync(strings.Projection, 500, cancellationToken).ConfigureAwait(false);
                if (batch.Count == 0)
                {
                    break;
                }

                foreach (var item in batch)
                {
                    ApplyOutcome outcome;
                    try
                    {
                        await using var db = new ConsolidationDbContext(options);
                        outcome = await new DailyConsolidationApplier(db, applierFailurePoints, clock).ApplyAsync(item, cancellationToken).ConfigureAwait(false);
                    }
                    catch (InvalidEventException ex)
                    {
                        return Conflict("replay", $"o movimento canônico {item.EventId} é inválido para a projeção: {FailureClassifier.Sanitize(ex)}");
                    }

                    if (outcome == ApplyOutcome.Applied)
                    {
                        applied++;
                    }
                    else
                    {
                        alreadyApplied++;
                    }

                    await failurePoints.AfterReplayItemAsync(applied, cancellationToken).ConfigureAwait(false);
                }
            }

            await using (var destination = await BootstrapStore.OpenAsync(strings.Projection, cancellationToken).ConfigureAwait(false))
            await using (var done = new NpgsqlCommand(
                "UPDATE rebuild_operation SET state = 'replayed', replay_completed_at = statement_timestamp() WHERE id = 1 AND state = 'captured'",
                destination))
            {
                await done.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            var after = await ReadOperationOrNullAsync(strings.Projection, cancellationToken).ConfigureAwait(false);
            return new RebuildOutcome(AdminExitCodes.Success, new { stage = "replay", state = after?.State, appliedInThisRun = applied, alreadyApplied, note = "A Outbox da origem não foi tocada pelo replay." });
        }
        catch (Exception ex) when (IsUnreachable(ex))
        {
            return Unreachable("replay", ex);
        }
    }

    private static async Task<List<ConsolidationEvent>> NextBatchAsync(string projection, int size, CancellationToken cancellationToken)
    {
        await using var connection = await BootstrapStore.OpenAsync(projection, cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(
            """
            SELECT i.sequence, i.event_id, i.account_id, i.customer_id, i.type, i.amount, i.effective_at
            FROM rebuild_capture_item i
            WHERE NOT EXISTS (SELECT 1 FROM processed_events p WHERE p.event_id = i.event_id)
            ORDER BY i.sequence, i.event_id
            LIMIT @size
            """,
            connection);
        command.Parameters.AddWithValue("size", size);
        var rows = new List<ConsolidationEvent>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(new ConsolidationEvent(reader.GetInt64(0), reader.GetGuid(1), reader.GetGuid(2), reader.GetGuid(3), reader.GetInt16(4), reader.GetDecimal(5), reader.GetFieldValue<DateTimeOffset>(6), 0));
        }

        return rows;
    }

    // ---------------------------------------------------------------- reconciliar, habilitar

    public async Task<RebuildOutcome> ReconcileAsync(RebuildRequest request, CancellationToken cancellationToken)
    {
        var refusal = Validate(request);
        if (refusal is not null)
        {
            return Refused(refusal);
        }

        try
        {
            var strings = connections.Open(ConsolidationConnectionRole.Rebuild);
            await using var lease = await AdminLease.TryAcquireAsync(strings.Projection, logger, cancellationToken).ConfigureAwait(false);
            if (lease is null)
            {
                return Busy("reconcile");
            }

            var op = await ReadOperationOrNullAsync(strings.Projection, cancellationToken).ConfigureAwait(false);
            if (op is null || op.OperationLabel != request.OperationLabel)
            {
                return Conflict("reconcile", "não há reconstrução desta operação neste destino");
            }

            var originMismatch = await OriginMismatchAsync(strings.Origin, op, cancellationToken).ConfigureAwait(false);
            if (originMismatch is not null)
            {
                return Conflict("reconcile", originMismatch);
            }

            if (op.State is RebuildStates.Prepared or RebuildStates.Captured or RebuildStates.Cancelled)
            {
                return Conflict("reconcile", $"o replay ainda não terminou (estado {op.State})");
            }

            var report = await verifier.VerifyAsync(VerifyMode.Rebuild, cancellationToken).ConfigureAwait(false);
            if (!report.Consistent)
            {
                return new RebuildOutcome(AdminExitCodes.Conflict, new { stage = "reconcile", state = op.State, reconciled = false, report });
            }

            await using var destination = await BootstrapStore.OpenAsync(strings.Projection, cancellationToken).ConfigureAwait(false);
            await using var mark = new NpgsqlCommand(
                "UPDATE rebuild_operation SET state = 'reconciled', reconciled_at = statement_timestamp() WHERE id = 1 AND state = 'replayed'",
                destination);
            await mark.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            var after = await ReadOperationAsync(destination, cancellationToken).ConfigureAwait(false);
            return new RebuildOutcome(AdminExitCodes.Success, new { stage = "reconcile", state = after?.State, reconciled = true, report });
        }
        catch (Exception ex) when (IsUnreachable(ex))
        {
            return Unreachable("reconcile", ex);
        }
    }

    public async Task<RebuildOutcome> EnableAsync(RebuildRequest request, CancellationToken cancellationToken)
    {
        var refusal = Validate(request);
        if (refusal is not null)
        {
            return Refused(refusal);
        }

        try
        {
            var strings = connections.Open(ConsolidationConnectionRole.Rebuild);
            await using var lease = await AdminLease.TryAcquireAsync(strings.Projection, logger, cancellationToken).ConfigureAwait(false);
            if (lease is null)
            {
                return Busy("enable");
            }

            var op = await ReadOperationOrNullAsync(strings.Projection, cancellationToken).ConfigureAwait(false);
            if (op is null || op.OperationLabel != request.OperationLabel)
            {
                return Conflict("enable", "não há reconstrução desta operação neste destino");
            }

            var originMismatch = await OriginMismatchAsync(strings.Origin, op, cancellationToken).ConfigureAwait(false);
            if (originMismatch is not null)
            {
                return Conflict("enable", originMismatch);
            }

            if (op.State is RebuildStates.Enabled or RebuildStates.Promoted)
            {
                return new RebuildOutcome(AdminExitCodes.Success, new { stage = "enable", state = op.State, note = "já habilitado" });
            }

            if (op.State != RebuildStates.Reconciled)
            {
                return Conflict("enable", $"a habilitação exige o conjunto R reconciliado (estado {op.State})");
            }

            // Nunca habilita sobre uma divergência nova: reconcilia de novo imediatamente antes.
            var report = await verifier.VerifyAsync(VerifyMode.Rebuild, cancellationToken).ConfigureAwait(false);
            if (!report.Consistent)
            {
                return new RebuildOutcome(AdminExitCodes.Conflict, new { stage = "enable", state = op.State, reconciled = false, report });
            }

            await using var destination = await BootstrapStore.OpenAsync(strings.Projection, cancellationToken).ConfigureAwait(false);
            await using var tx = await destination.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

            // Descritor HISTÓRICO do bootstrap (não a contagem de R), com a proveniência da reconstrução na própria operação.
            await using (var bootstrap = new NpgsqlCommand(
                """
                INSERT INTO consolidation_bootstrap (id, bootstrap_id, phase, member_count, captured_at, applied_at, reconciled_at)
                SELECT 1, origin_bootstrap_id, 'live', origin_member_count, origin_captured_at, statement_timestamp(), statement_timestamp()
                FROM rebuild_operation WHERE id = 1 AND state = 'reconciled' AND origin_bootstrap_id IS NOT NULL
                ON CONFLICT (id) DO NOTHING
                """,
                destination,
                tx))
            {
                await bootstrap.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await using (var mark = new NpgsqlCommand(
                "UPDATE rebuild_operation SET state = 'enabled', enabled_at = statement_timestamp() WHERE id = 1 AND state = 'reconciled'",
                destination,
                tx))
            {
                await mark.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
            var after = await ReadOperationOrNullAsync(strings.Projection, cancellationToken).ConfigureAwait(false);
            return new RebuildOutcome(
                AdminExitCodes.Success,
                new { stage = "enable", state = after?.State, note = "Habilitado, mas ainda NÃO promovido: Workers e APIs não consomem nem servem este destino até a promoção." });
        }
        catch (Exception ex) when (IsUnreachable(ex))
        {
            return Unreachable("enable", ex);
        }
    }

    public async Task<RebuildOutcome> CancelAsync(RebuildRequest request, CancellationToken cancellationToken)
    {
        var refusal = Validate(request);
        if (refusal is not null)
        {
            return Refused(refusal);
        }

        try
        {
            var strings = connections.Open(ConsolidationConnectionRole.Rebuild);
            await using var lease = await AdminLease.TryAcquireAsync(strings.Projection, logger, cancellationToken).ConfigureAwait(false);
            if (lease is null)
            {
                return Busy("cancel");
            }

            var current = await ReadOperationOrNullAsync(strings.Projection, cancellationToken).ConfigureAwait(false);
            if (current is null || !string.Equals(current.OperationLabel, request.OperationLabel, StringComparison.Ordinal))
            {
                return Conflict("cancel", "não há reconstrução desta operação neste destino");
            }

            if (current.State == RebuildStates.Enabled)
            {
                var originMismatch = await OriginMismatchAsync(strings.Origin, current, cancellationToken).ConfigureAwait(false);
                if (originMismatch is not null)
                {
                    return Conflict("cancel", originMismatch);
                }

                // Só um destino habilitado pode já ter sido tornado ativo na origem. Sem ler o registro, não se afirma que a troca não ocorreu.
                var registry = await ReadRegistryAsync(strings.Origin, cancellationToken).ConfigureAwait(false);
                if (registry == current.ProjectionId)
                {
                    return new RebuildOutcome(
                        AdminExitCodes.Conflict,
                        new
                        {
                            stage = "cancel",
                            state = current.State,
                            cancelled = false,
                            reason = "a promoção desta operação já foi registrada na origem; cancelar não a desfaz. Repita o estágio promote para completar a marca local",
                        });
                }
            }

            if (!await lease.IsStillHeldAsync(cancellationToken).ConfigureAwait(false))
            {
                return Conflict("cancel", "a exclusividade administrativa foi perdida; nada foi cancelado");
            }

            await failurePoints.ReachedAsync(RebuildPoints.CancelBeforeUpdate, cancellationToken).ConfigureAwait(false);
            await using var destination = await BootstrapStore.OpenAsync(strings.Projection, cancellationToken).ConfigureAwait(false);
            await using var cancel = new NpgsqlCommand(
                "UPDATE rebuild_operation SET state = 'cancelled', cancelled_at = statement_timestamp() WHERE id = 1 AND operation_label = @label AND state IN ('prepared', 'captured', 'replayed', 'reconciled', 'enabled')",
                destination);
            cancel.Parameters.AddWithValue("label", request.OperationLabel);
            var changed = await cancel.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            var op = await ReadOperationAsync(destination, cancellationToken).ConfigureAwait(false);
            return new RebuildOutcome(
                changed == 1 && op?.State == RebuildStates.Cancelled ? AdminExitCodes.Success : AdminExitCodes.Conflict,
                new { stage = "cancel", state = op?.State ?? "absent", note = "O banco antigo e a origem não foram tocados; nada é apagado." });
        }
        catch (Exception ex) when (IsUnreachable(ex))
        {
            return Unreachable("cancel", ex);
        }
    }

    // ---------------------------------------------------------------- promoção

    public async Task<RebuildOutcome> PromoteAsync(RebuildRequest request, CancellationToken cancellationToken)
    {
        var refusal = Validate(request);
        if (refusal is not null)
        {
            return Refused(refusal);
        }

        if (string.IsNullOrWhiteSpace(request.ConfirmPreviousDatabase))
        {
            return Refused("a promoção exige a confirmação explícita do banco de projeção ANTERIOR (--confirm-previous-database)");
        }

        try
        {
            var strings = connections.Open(ConsolidationConnectionRole.Rebuild);
            await using var lease = await AdminLease.TryAcquireAsync(strings.Projection, logger, cancellationToken).ConfigureAwait(false);
            if (lease is null)
            {
                return Busy("promote");
            }

            var targetDatabase = new NpgsqlConnectionStringBuilder(strings.Projection).Database ?? string.Empty;
            if (string.Equals(request.ConfirmPreviousDatabase, targetDatabase, StringComparison.Ordinal))
            {
                return Refused("o banco anterior não pode ser o próprio destino da reconstrução");
            }

            var op = await ReadOperationOrNullAsync(strings.Projection, cancellationToken).ConfigureAwait(false);
            if (op is null || op.OperationLabel != request.OperationLabel)
            {
                return Conflict("promote", "não há reconstrução desta operação neste destino");
            }

            var originMismatch = await OriginMismatchAsync(strings.Origin, op, cancellationToken).ConfigureAwait(false);
            if (originMismatch is not null)
            {
                return Conflict("promote", originMismatch);
            }

            var registry = await ReadRegistryRowAsync(strings.Origin, cancellationToken).ConfigureAwait(false);
            var originDatabase = new NpgsqlConnectionStringBuilder(strings.Origin).Database ?? string.Empty;
            if (op.State == RebuildStates.Promoted && registry.ActiveId == op.ProjectionId)
            {
                return new RebuildOutcome(AdminExitCodes.Success, new { stage = "promote", state = op.State, note = "já promovido; repetição sem efeito" });
            }

            if (op.State == RebuildStates.Enabled && registry.ActiveId == op.ProjectionId)
            {
                // Promoção parcial: o registro na origem foi confirmado e a marca local não. Completa a MESMA operação, sem regravar o registro
                // e sem rever o que a promoção já decidiu; consumidores desta projeção ficam em espera até a marca (RebuildInProgress).
                return await MarkPromotedAsync(strings, op, request, originDatabase, resumed: true, lease, cancellationToken).ConfigureAwait(false);
            }

            var refusals = new List<string>();
            if (op.State != RebuildStates.Enabled)
            {
                refusals.Add($"a promoção exige o destino habilitado e reconciliado (estado {op.State})");
            }

            if (refusals.Count == 0)
            {
                var report = await verifier.VerifyAsync(VerifyMode.Rebuild, cancellationToken).ConfigureAwait(false);
                if (!report.Consistent)
                {
                    refusals.Add($"a reconciliação de R voltou a divergir ({report.DeviationCountsByKind.Count} tipo(s) de divergência)");
                }
            }

            // Controle comprovado sobre consumidores e APIs: nenhuma sessão de Worker ou API na origem, no destino ou no banco anterior, e nada em voo.
            var previous = new NpgsqlConnectionStringBuilder(strings.Projection) { Database = request.ConfirmPreviousDatabase }.ConnectionString;
            foreach (var (label, connectionString) in new[] { ("origem", strings.Origin), ("destino novo", strings.Projection), ("banco anterior", previous) })
            {
                var sessions = await ConsumerSessionsAsync(connectionString, cancellationToken).ConfigureAwait(false);
                if (sessions is null)
                {
                    refusals.Add($"não foi possível verificar as sessões em {label}");
                }
                else if (sessions.Count > 0)
                {
                    refusals.Add($"{sessions.Sum(s => s.Value)} sessão(ões) de Worker/API do consolidado conectadas em {label} ({string.Join(", ", sessions.Select(s => s.Key))}); pare e retire do tráfego antes de promover");
                }
            }

            await using (var origin = await BootstrapStore.OpenAsync(strings.Origin, cancellationToken).ConfigureAwait(false))
            {
                var leases = await ScalarAsync<long>(origin, "SELECT COUNT(*) FROM consolidation_outbox WHERE status = 0 AND lease_token IS NOT NULL AND lease_expires_at > statement_timestamp()", cancellationToken).ConfigureAwait(false);
                if (leases > 0)
                {
                    refusals.Add($"{leases} reserva(s) válida(s) na origem: há trabalho em voo");
                }

                // Todo evento já confirmado na origem precisa estar coberto por R.
                var confirmed = new List<Guid>();
                await using var query = new NpgsqlCommand("SELECT event_id FROM consolidation_outbox WHERE status = 1", origin);
                await using var reader = await query.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    confirmed.Add(reader.GetGuid(0));
                }

                await reader.DisposeAsync().ConfigureAwait(false);
                await using var destination = await BootstrapStore.OpenAsync(strings.Projection, cancellationToken).ConfigureAwait(false);
                await using var uncovered = new NpgsqlCommand(
                    "SELECT COUNT(*) FROM unnest(@ids) AS c(event_id) WHERE NOT EXISTS (SELECT 1 FROM rebuild_capture_item i WHERE i.event_id = c.event_id)",
                    destination);
                uncovered.Parameters.AddWithValue("ids", confirmed.ToArray());
                var missing = (long)(await uncovered.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) ?? 0L);
                if (missing > 0)
                {
                    refusals.Add($"{missing} evento(s) já confirmado(s) na origem não estão em R: um consumidor confirmou trabalho depois da captura");
                }
            }

            if (refusals.Count > 0)
            {
                return new RebuildOutcome(AdminExitCodes.Conflict, new { stage = "promote", state = op.State, promoted = false, refusals, note = "Nada foi alterado." });
            }

            if (!await lease.IsStillHeldAsync(cancellationToken).ConfigureAwait(false))
            {
                return Conflict("promote", "a exclusividade administrativa foi perdida; nada foi alterado");
            }

            // Registro na origem primeiro, condicionado à geração lida: nunca sobrescreve em silêncio um registro que mudou desde a leitura.
            await using (var origin = await BootstrapStore.OpenAsync(strings.Origin, cancellationToken).ConfigureAwait(false))
            await using (var write = new NpgsqlCommand(
                registry.ActiveId is null
                    ? """
                      INSERT INTO consolidation_projection_registry (id, active_projection_id, generation, promoted_at, operation_label, previous_database)
                      VALUES (1, @pid, 1, statement_timestamp(), @label, @previous)
                      ON CONFLICT (id) DO NOTHING
                      """
                    : """
                      UPDATE consolidation_projection_registry
                      SET active_projection_id = @pid, generation = generation + 1, promoted_at = statement_timestamp(),
                          operation_label = @label, previous_database = @previous
                      WHERE id = 1 AND generation = @expected AND active_projection_id <> @pid
                      """,
                origin))
            {
                write.Parameters.AddWithValue("pid", op.ProjectionId);
                write.Parameters.AddWithValue("label", request.OperationLabel);
                write.Parameters.AddWithValue("previous", request.ConfirmPreviousDatabase);
                write.Parameters.AddWithValue("expected", registry.Generation);
                if (await write.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                {
                    var reread = await ReadRegistryRowAsync(strings.Origin, cancellationToken).ConfigureAwait(false);
                    if (reread.ActiveId != op.ProjectionId)
                    {
                        return new RebuildOutcome(
                            AdminExitCodes.Conflict,
                            new { stage = "promote", state = op.State, promoted = false, reason = "o registro da projeção ativa mudou desde a leitura; nada foi sobrescrito", note = "A origem não foi alterada por esta tentativa." });
                    }
                }
            }

            await failurePoints.ReachedAsync(RebuildPoints.PromoteAfterRegistry, cancellationToken).ConfigureAwait(false);
            return await MarkPromotedAsync(strings, op, request, originDatabase, resumed: false, lease, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsUnreachable(ex))
        {
            return Unreachable("promote", ex);
        }
    }

    /// <summary>Marca local da promoção. Só devolve sucesso depois de conferir a linha afetada, o estado resultante e o registro da origem.</summary>
    private static async Task<RebuildOutcome> MarkPromotedAsync(
        ConsolidationConnectionStrings strings,
        RebuildOperation op,
        RebuildRequest request,
        string originDatabase,
        bool resumed,
        AdminLease lease,
        CancellationToken cancellationToken)
    {
        if (!await lease.IsStillHeldAsync(cancellationToken).ConfigureAwait(false))
        {
            return PartialPromotion(op.State, "a exclusividade administrativa foi perdida antes da marca local");
        }

        int changed;
        await using (var destination = await BootstrapStore.OpenAsync(strings.Projection, cancellationToken).ConfigureAwait(false))
        await using (var mark = new NpgsqlCommand(
            "UPDATE rebuild_operation SET state = 'promoted', promoted_at = statement_timestamp() WHERE id = 1 AND state = 'enabled' AND projection_id = @pid",
            destination))
        {
            mark.Parameters.AddWithValue("pid", op.ProjectionId);
            changed = await mark.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        // Zero linhas não comprova a transição: relê o destino e a origem antes de anunciar qualquer sucesso.
        var after = await ReadOperationOrNullAsync(strings.Projection, cancellationToken).ConfigureAwait(false);
        var registry = await ReadRegistryAsync(strings.Origin, cancellationToken).ConfigureAwait(false);
        if (after?.State != RebuildStates.Promoted || registry != op.ProjectionId)
        {
            return PartialPromotion(after?.State ?? op.State, $"a marca local não foi confirmada (linhas afetadas: {changed}; estado atual: {after?.State ?? "ausente"})");
        }

        return new RebuildOutcome(
            AdminExitCodes.Success,
            new
            {
                stage = "promote",
                state = RebuildStates.Promoted,
                promoted = true,
                resumed,
                activeProjectionId = op.ProjectionId,
                originDatabase,
                previousDatabase = request.ConfirmPreviousDatabase,
                note = "Registrado na origem. Suba as APIs e os Workers novos apontando SÓ para o banco reconstruído. Voltar ao banco antigo trocando a connection string NÃO é um retorno seguro: o registro da origem bloqueia o consumo e a leitura de uma projeção que não é a ativa.",
            });
    }

    private static RebuildOutcome PartialPromotion(string state, string reason) =>
        new(
            AdminExitCodes.Conflict,
            new
            {
                stage = "promote",
                state,
                promoted = false,
                partial = true,
                reason,
                note = "O registro da projeção ativa já foi confirmado na origem. Não se presume rollback: repita o estágio promote da MESMA operação ou investigue o estado do destino.",
            });

    // ---------------------------------------------------------------- precondições comuns

    private sealed record Facts(
        long Movements,
        long MovementCeiling,
        long Pending,
        long Quarantined,
        long ValidLeases,
        bool OriginHasCaptureRecord,
        bool TargetEmptyOrOwned,
        IReadOnlyDictionary<string, long> OriginSessions,
        IReadOnlyDictionary<string, long> TargetSessions);

    private async Task<object> GatherAsync(ConsolidationConnectionStrings strings, RebuildRequest request, List<string> refusals, CancellationToken cancellationToken)
    {
        var originBuilder = new NpgsqlConnectionStringBuilder(strings.Origin);
        var targetBuilder = new NpgsqlConnectionStringBuilder(strings.Projection);
        if (string.Equals(originBuilder.Host, targetBuilder.Host, StringComparison.OrdinalIgnoreCase) && originBuilder.Port == targetBuilder.Port
            && string.Equals(originBuilder.Database, targetBuilder.Database, StringComparison.Ordinal))
        {
            refusals.Add("a origem não pode ser o destino da reconstrução");
        }

        long movements, pending, quarantined, leases;
        bool hasCaptureRecord;
        await using (var origin = await BootstrapStore.OpenAsync(strings.Origin, cancellationToken).ConfigureAwait(false))
        {
            movements = await ScalarAsync<long>(origin, "SELECT COUNT(*) FROM movements", cancellationToken).ConfigureAwait(false);
            pending = await ScalarAsync<long>(origin, "SELECT COUNT(*) FROM consolidation_outbox WHERE status = 0", cancellationToken).ConfigureAwait(false);
            quarantined = await ScalarAsync<long>(origin, "SELECT COUNT(*) FROM consolidation_outbox WHERE status = 2", cancellationToken).ConfigureAwait(false);
            leases = await ScalarAsync<long>(origin, "SELECT COUNT(*) FROM consolidation_outbox WHERE status = 0 AND lease_token IS NOT NULL AND lease_expires_at > statement_timestamp()", cancellationToken).ConfigureAwait(false);
            hasCaptureRecord = await ScalarAsync<bool>(origin, "SELECT EXISTS (SELECT 1 FROM consolidation_bootstrap_capture WHERE id = 1)", cancellationToken).ConfigureAwait(false);
        }

        if (movements > movementCeiling)
        {
            refusals.Add($"{movements} movimentos excedem o teto declarado de {movementCeiling} desta versão; a captura não será truncada");
        }

        if (leases > 0)
        {
            refusals.Add($"{leases} reserva(s) válida(s) na origem: pare os consumidores e espere as reservas vencerem antes da captura");
        }

        if (!hasCaptureRecord)
        {
            refusals.Add("a origem não tem registro de controle do bootstrap; execute o coordenador (--backfill) antes de reconstruir");
        }

        var originSessions = await ConsumerSessionsAsync(strings.Origin, cancellationToken).ConfigureAwait(false);
        var targetSessions = await ConsumerSessionsAsync(strings.Projection, cancellationToken).ConfigureAwait(false);
        foreach (var (label, sessions) in new[] { ("origem", originSessions), ("destino", targetSessions) })
        {
            if (sessions is null)
            {
                refusals.Add($"não foi possível verificar as sessões de consumidores em {label}");
            }
            else if (sessions.Count > 0)
            {
                refusals.Add($"{sessions.Sum(s => s.Value)} sessão(ões) de Worker/API do consolidado conectadas em {label}; a captura exige os consumidores conhecidos parados");
            }
        }

        bool targetOk;
        await using (var destination = await BootstrapStore.OpenAsync(strings.Projection, cancellationToken).ConfigureAwait(false))
        {
            var schema = await ScalarAsync<bool>(destination, "SELECT to_regclass('public.rebuild_operation') IS NOT NULL", cancellationToken).ConfigureAwait(false);
            if (!schema)
            {
                refusals.Add("o destino não está migrado para a reconstrução (migration AddRebuildRecovery ausente)");
                targetOk = false;
            }
            else
            {
                var op = await ReadOperationAsync(destination, cancellationToken).ConfigureAwait(false);
                var data = await ScalarAsync<long>(destination, "SELECT (SELECT COUNT(*) FROM daily_account_consolidation) + (SELECT COUNT(*) FROM consolidation_bootstrap)", cancellationToken).ConfigureAwait(false);
                var receipts = await ScalarAsync<long>(destination, "SELECT COUNT(*) FROM processed_events", cancellationToken).ConfigureAwait(false);
                if (op is null && data + receipts > 0)
                {
                    refusals.Add("o destino já contém dados de uma projeção ativa; a reconstrução exige um banco novo, migrado e vazio");
                    targetOk = false;
                }
                else
                {
                    targetOk = true;
                }
            }
        }

        return new Facts(
            movements,
            movementCeiling,
            pending,
            quarantined,
            leases,
            hasCaptureRecord,
            targetOk,
            originSessions ?? new Dictionary<string, long>(),
            targetSessions ?? new Dictionary<string, long>());
    }

    /// <summary>Sessões de Worker ou API do consolidado conectadas ao banco da connection string (visão de <c>pg_stat_activity</c>). Nulo: não foi possível verificar.</summary>
    private static async Task<Dictionary<string, long>?> ConsumerSessionsAsync(string connectionString, CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = await BootstrapStore.OpenAsync(connectionString, cancellationToken).ConfigureAwait(false);
            await using var command = new NpgsqlCommand(
                """
                SELECT application_name, COUNT(*) FROM pg_stat_activity
                WHERE datname = current_database() AND pid <> pg_backend_pid()
                  AND (application_name LIKE @worker OR application_name LIKE @api)
                GROUP BY application_name
                """,
                connection);
            command.Parameters.AddWithValue("worker", ConsolidationApplicationNames.Worker + "%");
            command.Parameters.AddWithValue("api", ConsolidationApplicationNames.Api + "%");
            var result = new Dictionary<string, long>(StringComparer.Ordinal);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                result[reader.GetString(0)] = reader.GetInt64(1);
            }

            return result;
        }
        catch (Exception ex) when (IsUnreachable(ex))
        {
            return null;
        }
    }

    private string? Validate(RebuildRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!LabelPattern.IsMatch(request.OperationLabel))
        {
            return "identificador de operação inválido (3 a 64 caracteres: letras, dígitos, '.', '_' ou '-')";
        }

        if (string.IsNullOrWhiteSpace(request.ConfirmTargetDatabase))
        {
            return "o banco de destino precisa ser confirmado explicitamente (--confirm-target-database)";
        }

        // Alvo errado: o destino confirmado precisa ser exatamente o banco de projeção configurado.
        return string.Equals(
            new NpgsqlConnectionStringBuilder(connections.Open(ConsolidationConnectionRole.Rebuild).Projection).Database,
            request.ConfirmTargetDatabase,
            StringComparison.Ordinal)
            ? null
            : "o destino confirmado não corresponde ao banco de projeção configurado";
    }

    // ---------------------------------------------------------------- acesso

    /// <summary>
    /// Vínculo entre a origem configurada e a origem DESTA operação, conferido sob a exclusividade e antes de usar a origem como autoridade.
    /// Identidade validada: o nome efetivo do banco (<c>current_database()</c>) igual ao <c>source_database</c> persistido e, depois da selagem,
    /// o identificador histórico do bootstrap (<c>bootstrap_id</c> do registro de controle) igual ao persistido. O nome não é global entre servidores
    /// (instâncias homônimas); o identificador histórico reduz, mas não elimina, esse limite. Nada de credencial é lido nem gravado.
    /// Devolve o motivo da divergência, ou nulo se coincide; origem ilegível lança a exceção do Npgsql (resultado desconhecido, nunca "ausente").
    /// </summary>
    private static async Task<string?> OriginMismatchAsync(string origin, RebuildOperation op, CancellationToken cancellationToken)
    {
        await using var connection = await BootstrapStore.OpenAsync(origin, cancellationToken).ConfigureAwait(false);
        var actual = await ScalarAsync<string>(connection, "SELECT current_database()", cancellationToken).ConfigureAwait(false) ?? string.Empty;
        if (!string.Equals(actual, op.SourceDatabase, StringComparison.Ordinal))
        {
            return $"a origem configurada ({actual}) não é a origem desta operação ({op.SourceDatabase}); nada foi alterado";
        }

        if (op.OriginBootstrapId is { } expected)
        {
            var capture = await BootstrapStore.ReadCaptureAsync(connection, cancellationToken).ConfigureAwait(false);
            if (capture is null || capture.BootstrapId != expected)
            {
                return "o registro de controle do bootstrap da origem configurada não é o desta operação; nada foi alterado";
            }
        }

        return null;
    }

    private static RebuildOutcome Refused(string reason) => new(AdminExitCodes.Refused, new { state = "refused", reason });

    private static RebuildOutcome Conflict(string stage, string reason) => new(AdminExitCodes.Conflict, new { stage, state = "conflict", reason });

    private static RebuildOutcome Busy(string stage) =>
        Conflict(stage, "outra operação administrativa de reconstrução está em andamento neste destino (exclusividade por alvo); nada foi alterado. Aguarde o término e repita");

    /// <summary>
    /// Exclusividade administrativa por destino ENTRE PROCESSOS: lock consultivo de sessão no próprio banco de destino, numa conexão dedicada mantida
    /// durante todo o comando. Não é lock em memória e não toca linhas financeiras nem a Outbox. Quem não adquiriu (recebeu "ocupado") nunca
    /// libera; a sessão que perde a conexão deixa de ser dona (<see cref="IsStillHeldAsync"/>) e o PostgreSQL libera o lock ao encerrá-la.
    /// </summary>
    private sealed class AdminLease : IAsyncDisposable
    {
        private const string Key = "hashtext('ft_rebuild_admin')";
        private readonly NpgsqlConnection _connection;
        private readonly ILogger _logger;
        private bool _held = true;

        private AdminLease(NpgsqlConnection connection, ILogger logger)
        {
            _connection = connection;
            _logger = logger;
        }

        public static async Task<AdminLease?> TryAcquireAsync(string projection, ILogger logger, CancellationToken cancellationToken)
        {
            var connection = await BootstrapStore.OpenAsync(projection, cancellationToken).ConfigureAwait(false);
            try
            {
                await using var command = new NpgsqlCommand($"SELECT pg_try_advisory_lock({Key})", connection);
                if ((bool)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) ?? false))
                {
                    return new AdminLease(connection, logger);
                }
            }
            catch
            {
                await connection.DisposeAsync().ConfigureAwait(false);
                throw;
            }

            await connection.DisposeAsync().ConfigureAwait(false);
            return null;
        }

        /// <summary>Confirma, na própria sessão dona, que o lock continua concedido. Conexão perdida lança exceção do Npgsql (tratada como destino inalcançável).</summary>
        public async Task<bool> IsStillHeldAsync(CancellationToken cancellationToken)
        {
            if (!_held)
            {
                return false;
            }

            await using var command = new NpgsqlCommand("SELECT COUNT(*) FROM pg_locks WHERE locktype = 'advisory' AND pid = pg_backend_pid() AND granted", _connection);
            _held = (long)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) ?? 0L) == 1;
            return _held;
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                if (_held)
                {
                    await using var command = new NpgsqlCommand($"SELECT pg_advisory_unlock({Key})", _connection);
                    if (!(bool)(await command.ExecuteScalarAsync(CancellationToken.None).ConfigureAwait(false) ?? false))
                    {
                        LogUnlockNotOwner(_logger);
                    }
                }
            }
            catch (NpgsqlException)
            {
                // A conexão caiu: o PostgreSQL já liberou o lock ao encerrar a sessão.
            }
            finally
            {
                await _connection.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private static RebuildOutcome Unreachable(string stage, Exception ex) =>
        new(AdminExitCodes.Unreachable, new { stage, state = "unknown", error = FailureClassifier.Sanitize(ex) });

    private static bool IsUnreachable(Exception ex) =>
        ex is NpgsqlException or TimeoutException or System.Net.Sockets.SocketException;

    private static async Task<T?> ScalarAsync<T>(NpgsqlConnection connection, string sql, CancellationToken cancellationToken, NpgsqlTransaction? tx = null)
    {
        await using var command = new NpgsqlCommand(sql, connection, tx);
        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (result is null or DBNull)
        {
            return default;
        }

        return result is T typed ? typed : (T)Convert.ChangeType(result, typeof(T), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task<RebuildOperation?> ReadOperationAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT projection_id, operation_label, source_database, state, origin_bootstrap_id, origin_member_count, origin_captured_at,
                   capture_snapshot, capture_count, capture_digest, payload_divergences
            FROM rebuild_operation WHERE id = 1
            """,
            connection);
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
                OriginCapturedAt = reader.IsDBNull(6) ? null : reader.GetFieldValue<DateTimeOffset>(6),
                CaptureSnapshot = reader.IsDBNull(7) ? null : reader.GetString(7),
                CaptureCount = reader.IsDBNull(8) ? null : reader.GetInt64(8),
                CaptureDigest = reader.IsDBNull(9) ? null : reader.GetString(9),
                PayloadDivergences = reader.GetInt64(10),
            }
            : null;
    }

    private static async Task<RebuildOperation?> ReadOperationOrNullAsync(string projection, CancellationToken cancellationToken)
    {
        await using var connection = await BootstrapStore.OpenAsync(projection, cancellationToken).ConfigureAwait(false);
        var schema = await ScalarAsync<bool>(connection, "SELECT to_regclass('public.rebuild_operation') IS NOT NULL", cancellationToken).ConfigureAwait(false);
        return schema ? await ReadOperationAsync(connection, cancellationToken).ConfigureAwait(false) : null;
    }

    private static async Task<Guid?> ReadRegistryAsync(string origin, CancellationToken cancellationToken) =>
        (await ReadRegistryRowAsync(origin, cancellationToken).ConfigureAwait(false)).ActiveId;

    private readonly record struct RegistryRow(Guid? ActiveId, long Generation);

    private static async Task<RegistryRow> ReadRegistryRowAsync(string origin, CancellationToken cancellationToken)
    {
        await using var connection = await BootstrapStore.OpenAsync(origin, cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand("SELECT active_projection_id, generation FROM consolidation_projection_registry WHERE id = 1", connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? new RegistryRow(reader.GetGuid(0), reader.GetInt64(1)) : new RegistryRow(null, 0);
    }

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]{2,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex LabelRegex();

    [LoggerMessage(EventId = 5201, Level = LogLevel.Warning, Message = "pg_advisory_unlock da exclusividade da reconstrução devolveu falso: a sessão não era dona do lock.")]
    private static partial void LogUnlockNotOwner(ILogger logger);

    [LoggerMessage(EventId = 5200, Level = LogLevel.Warning, Message = "Resultado do commit da captura da reconstrução desconhecido ({FailureType}); a operação será relida.")]
    private static partial void LogCommitUnknown(ILogger logger, string failureType);
}
