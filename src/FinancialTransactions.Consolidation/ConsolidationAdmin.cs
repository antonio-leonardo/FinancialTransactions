using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Npgsql;

namespace FinancialTransactions.Consolidation;

/// <summary>Códigos de saída dos comandos administrativos do Worker (<c>--status</c>, <c>--list-quarantine</c>, <c>--requeue</c>).</summary>
public static class AdminExitCodes
{
    /// <summary>Concluído (ou, em dry-run, plano elegível; ou repetição de operação já aplicada).</summary>
    public const int Success = 0;

    /// <summary>Banco inalcançável ou configuração inválida. Nenhuma contagem é inventada.</summary>
    public const int Unreachable = 2;

    /// <summary>Argumentos inválidos ou alvo não confirmado: recusado antes de qualquer acesso que altere dados.</summary>
    public const int Refused = 3;

    /// <summary>Seleção incompatível com o plano (evento fora da quarentena, ausente, digest diferente) ou identificador de operação com outra seleção.</summary>
    public const int Conflict = 4;

    /// <summary>Resultado do commit desconhecido: a repetição com o mesmo identificador decide pelo registro persistido.</summary>
    public const int CommitUnknown = 5;
}

/// <summary>Categorias fechadas de motivo para reabrir um evento em quarentena. Não existe edição de payload.</summary>
public static class RequeueReasons
{
    public const string CauseCorrected = "cause_corrected";
    public const string DependencyRestored = "dependency_restored";
    public const string FalsePositiveReviewed = "false_positive_reviewed";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        CauseCorrected,
        DependencyRestored,
        FalsePositiveReviewed,
    };
}

public static class AdminJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static void Write(TextWriter output, object value)
    {
        ArgumentNullException.ThrowIfNull(output);
        output.WriteLine(JsonSerializer.Serialize(value, Options));
    }
}

/// <summary>Estado operacional somente leitura, em JSON. Cada lado é lido de forma independente: dependência inalcançável é "unknown", nunca zero.</summary>
public sealed class ConsolidationStatusReader(IConsolidationConnectionFactory connections)
{
    private const string OriginSql = """
        SELECT COUNT(*) FILTER (WHERE status = 0),
               COUNT(*) FILTER (WHERE status = 0 AND lease_token IS NOT NULL AND lease_expires_at > statement_timestamp()),
               COUNT(*) FILTER (WHERE status = 0 AND lease_token IS NOT NULL AND lease_expires_at <= statement_timestamp()),
               COUNT(*) FILTER (WHERE status = 2),
               COUNT(*),
               EXTRACT(EPOCH FROM (statement_timestamp() - MIN(created_at) FILTER (WHERE status = 0))),
               statement_timestamp(),
               pg_total_relation_size('consolidation_outbox'),
               (SELECT COUNT(*) FROM consolidation_outbox_admin_log)
        FROM consolidation_outbox
        """;

    private const string ProjectionSql = """
        SELECT (SELECT phase FROM consolidation_bootstrap WHERE id = 1),
               (SELECT bootstrap_id FROM consolidation_bootstrap WHERE id = 1),
               (SELECT member_count FROM consolidation_bootstrap WHERE id = 1),
               (SELECT COUNT(*) FROM processed_events),
               pg_total_relation_size('processed_events'),
               current_database(),
               statement_timestamp()
        """;

    public async Task<(object Report, int ExitCode)> ReadAsync(CancellationToken cancellationToken)
    {
        var strings = connections.Open(ConsolidationConnectionRole.Health);
        var origin = await ReadOriginAsync(strings.Origin, cancellationToken).ConfigureAwait(false);
        var projection = await ReadProjectionAsync(strings.Projection, cancellationToken).ConfigureAwait(false);
        var exit = origin.State == "ok" && projection.State == "ok" ? AdminExitCodes.Success : AdminExitCodes.Unreachable;
        return (new { observedAt = DateTimeOffset.UtcNow, origin, projection }, exit);
    }

    private static async Task<OriginStatus> ReadOriginAsync(string connectionString, CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = await BootstrapStore.OpenAsync(connectionString, cancellationToken).ConfigureAwait(false);
            await using var command = new NpgsqlCommand(OriginSql, connection);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            var oldest = reader.IsDBNull(5) ? (double?)null : Math.Round(Convert.ToDouble(reader.GetValue(5), CultureInfo.InvariantCulture), 3);
            return new OriginStatus(
                "ok",
                null,
                reader.GetInt64(0),
                reader.GetInt64(1),
                reader.GetInt64(2),
                reader.GetInt64(3),
                reader.GetInt64(4),
                oldest,
                reader.GetFieldValue<DateTimeOffset>(6),
                reader.GetInt64(7),
                reader.GetInt64(8));
        }
        catch (Exception ex) when (ex is NpgsqlException or TimeoutException or InvalidOperationException or OperationCanceledException)
        {
            return OriginStatus.Unknown(FailureClassifier.Sanitize(ex));
        }
    }

    private static async Task<ProjectionStatus> ReadProjectionAsync(string connectionString, CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = await BootstrapStore.OpenAsync(connectionString, cancellationToken).ConfigureAwait(false);
            await using var command = new NpgsqlCommand(ProjectionSql, connection);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            return new ProjectionStatus(
                "ok",
                null,
                reader.IsDBNull(0) ? null : reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetGuid(1),
                reader.IsDBNull(2) ? null : reader.GetInt64(2),
                reader.GetInt64(3),
                reader.GetInt64(4),
                reader.GetString(5),
                reader.GetFieldValue<DateTimeOffset>(6));
        }
        catch (Exception ex) when (ex is NpgsqlException or TimeoutException or InvalidOperationException or OperationCanceledException)
        {
            return ProjectionStatus.Unknown(FailureClassifier.Sanitize(ex));
        }
    }

    /// <summary>Lado da origem. <c>pending</c> inclui os reservados; <c>reservedValid</c> e <c>expiredEligible</c> são subconjuntos de <c>pending</c>.</summary>
    public sealed record OriginStatus(
        string State,
        string? Error,
        long? Pending,
        long? ReservedValid,
        long? ExpiredEligible,
        long? Quarantined,
        long? OutboxRows,
        double? OldestPendingAgeSeconds,
        DateTimeOffset? ObservedAt,
        long? OutboxBytes,
        long? AdminReopenings)
    {
        public static OriginStatus Unknown(string error) => new("unknown", error, null, null, null, null, null, null, null, null, null);
    }

    public sealed record ProjectionStatus(
        string State,
        string? Error,
        string? Phase,
        Guid? BootstrapId,
        long? MemberCount,
        long? ProcessedEvents,
        long? ProcessedEventsBytes,
        string? Database,
        DateTimeOffset? ObservedAt)
    {
        public static ProjectionStatus Unknown(string error) => new("unknown", error, null, null, null, null, null, null, null);
    }
}

/// <summary>Argumentos de um comando administrativo. Sem valores sensíveis: connection strings vêm só do ambiente.</summary>
public sealed record RequeueRequest(
    string OperationId,
    IReadOnlyList<Guid> EventIds,
    string Reason,
    bool Apply,
    string? PlanDigest,
    string ConfirmDatabase);

/// <summary>Leitura da quarentena e reabertura restrita, com dry-run por padrão, plano com digest, trilha persistida e repetição idempotente.</summary>
public sealed partial class QuarantineAdmin(IConsolidationConnectionFactory connections)
{
    public const int MaxEventsPerOperation = 500;

    private static readonly Regex OperationIdPattern = OperationIdRegex();

    private const string SelectSql = """
        -- xmin: versão da linha na origem. Cada UPDATE (inclusive uma nova passagem pela quarentena) gera outra; o plano fica preso ao que foi revisado.
        SELECT sequence, event_id, status, attempts, last_error, xmin::text
        FROM consolidation_outbox
        WHERE event_id = ANY(@ids)
        ORDER BY event_id
        """;

    private const string SelectForUpdateSql = SelectSql + "\nFOR UPDATE";

    public async Task<int> ListAsync(int limit, TextWriter output, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(output);
        if (limit is < 1 or > 1000)
        {
            return Refuse(output, "limit fora de 1..1000");
        }

        try
        {
            await using var connection = await BootstrapStore.OpenAsync(connections.Open(ConsolidationConnectionRole.Health).Origin, cancellationToken).ConfigureAwait(false);
            await using var command = new NpgsqlCommand(
                "SELECT sequence, event_id, account_id, attempts, last_error, created_at FROM consolidation_outbox WHERE status = 2 ORDER BY sequence LIMIT @limit",
                connection);
            command.Parameters.AddWithValue("limit", limit);
            var items = new List<object>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                items.Add(new
                {
                    sequence = reader.GetInt64(0),
                    eventId = reader.GetGuid(1),
                    accountId = reader.GetGuid(2),
                    attempts = reader.GetInt32(3),
                    lastError = reader.IsDBNull(4) ? null : reader.GetString(4),
                    createdAt = reader.GetFieldValue<DateTimeOffset>(5),
                });
            }

            AdminJson.Write(output, new { quarantined = items.Count, limit, items });
            return AdminExitCodes.Success;
        }
        catch (Exception ex) when (IsUnreachable(ex))
        {
            AdminJson.Write(output, new { state = "unknown", error = FailureClassifier.Sanitize(ex) });
            return AdminExitCodes.Unreachable;
        }
    }

    public async Task<int> RequeueAsync(RequeueRequest request, TextWriter output, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(output);

        var refusal = Validate(request);
        if (refusal is not null)
        {
            return Refuse(output, refusal);
        }

        var ids = request.EventIds.Distinct().Order().ToArray();
        var selectionDigest = Digest(string.Join('\n', ids.Select(id => id.ToString("D", CultureInfo.InvariantCulture))));
        var origin = connections.Open(ConsolidationConnectionRole.Health).Origin;
        var database = new NpgsqlConnectionStringBuilder(origin).Database;
        if (!string.Equals(request.ConfirmDatabase, database, StringComparison.Ordinal))
        {
            return Refuse(output, "o alvo confirmado não corresponde ao banco de origem configurado");
        }

        try
        {
            return request.Apply
                ? await ApplyAsync(request, ids, selectionDigest, origin, output, cancellationToken).ConfigureAwait(false)
                : await PlanAsync(request, ids, selectionDigest, origin, output, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsUnreachable(ex))
        {
            AdminJson.Write(output, new { operationId = request.OperationId, state = "unknown", error = FailureClassifier.Sanitize(ex) });
            return AdminExitCodes.Unreachable;
        }
    }

    private static async Task<int> PlanAsync(
        RequeueRequest request, Guid[] ids, string selectionDigest, string origin, TextWriter output, CancellationToken cancellationToken)
    {
        await using var connection = await BootstrapStore.OpenAsync(origin, cancellationToken).ConfigureAwait(false);
        var existing = await ReadOperationAsync(connection, null, request.OperationId, cancellationToken).ConfigureAwait(false);
        var rows = await ReadSelectionAsync(connection, null, SelectSql, ids, cancellationToken).ConfigureAwait(false);
        var problems = Problems(ids, rows);
        var planDigest = PlanDigest(rows);

        if (existing is not null)
        {
            var same = existing.SelectionDigest == selectionDigest;
            AdminJson.Write(output, new
            {
                operationId = request.OperationId,
                mode = "dry-run",
                state = same ? "already_applied" : "conflict",
                note = same ? "A operação já foi aplicada com esta seleção; repetir não reenfileira." : "O identificador já foi usado com outra seleção.",
            });
            return same ? AdminExitCodes.Success : AdminExitCodes.Conflict;
        }

        AdminJson.Write(output, new
        {
            operationId = request.OperationId,
            mode = "dry-run",
            database = new NpgsqlConnectionStringBuilder(origin).Database,
            reason = request.Reason,
            selected = ids.Length,
            eligible = problems.Count == 0,
            problems,
            planDigest,
            items = rows.Select(r => new { r.Sequence, r.EventId, r.Status, r.Attempts, r.LastError }),
        });
        return problems.Count == 0 ? AdminExitCodes.Success : AdminExitCodes.Conflict;
    }

    private static async Task<int> ApplyAsync(
        RequeueRequest request, Guid[] ids, string selectionDigest, string origin, TextWriter output, CancellationToken cancellationToken)
    {
        await using var connection = await BootstrapStore.OpenAsync(origin, cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        // A linha da operação é inserida primeiro: dois comandos com o mesmo identificador se serializam na chave primária,
        // e o segundo encontra o registro do primeiro em vez de repetir o efeito.
        await using (var insert = new NpgsqlCommand(
            """
            INSERT INTO consolidation_outbox_admin_operation (operation_id, selection_digest, reason, event_count, created_at)
            VALUES (@op, @digest, @reason, @count, statement_timestamp())
            ON CONFLICT (operation_id) DO NOTHING
            """,
            connection,
            transaction))
        {
            insert.Parameters.AddWithValue("op", request.OperationId);
            insert.Parameters.AddWithValue("digest", selectionDigest);
            insert.Parameters.AddWithValue("reason", request.Reason);
            insert.Parameters.AddWithValue("count", ids.Length);
            if (await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 0)
            {
                var existing = await ReadOperationAsync(connection, transaction, request.OperationId, cancellationToken).ConfigureAwait(false);
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                var same = existing?.SelectionDigest == selectionDigest;
                AdminJson.Write(output, new
                {
                    operationId = request.OperationId,
                    mode = "apply",
                    state = same ? "already_applied" : "conflict",
                    requeued = same ? existing?.EventCount ?? 0 : 0,
                    note = same ? "Resultado registrado anteriormente; nenhum evento foi reenfileirado novamente." : "O identificador já foi usado com outra seleção; nada foi alterado.",
                });
                return same ? AdminExitCodes.Success : AdminExitCodes.Conflict;
            }
        }

        var rows = await ReadSelectionAsync(connection, transaction, SelectForUpdateSql, ids, cancellationToken).ConfigureAwait(false);
        var problems = Problems(ids, rows);
        if (problems.Count == 0 && !string.Equals(PlanDigest(rows), request.PlanDigest, StringComparison.Ordinal))
        {
            problems.Add("o estado atual da seleção difere do plano (digest diferente); gere um novo dry-run");
        }

        if (problems.Count > 0)
        {
            // Desfaz a linha da operação também: seleção incompatível não consome o identificador nem amplia o conjunto.
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            AdminJson.Write(output, new { operationId = request.OperationId, mode = "apply", state = "aborted", problems });
            return AdminExitCodes.Conflict;
        }

        // Trilha e mudança de estado na mesma transação: o estado anterior (status, tentativas, erro) fica registrado antes de o contador voltar a zero.
        await using (var trail = new NpgsqlCommand(
            """
            INSERT INTO consolidation_outbox_admin_log (operation_id, event_id, sequence, previous_status, previous_attempts, previous_error, created_at)
            SELECT @op, event_id, sequence, status, attempts, last_error, statement_timestamp()
            FROM consolidation_outbox
            WHERE event_id = ANY(@ids) AND status = 2
            """,
            connection,
            transaction))
        {
            trail.Parameters.AddWithValue("op", request.OperationId);
            trail.Parameters.AddWithValue("ids", ids);
            await trail.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        int updated;
        await using (var requeue = new NpgsqlCommand(
            """
            UPDATE consolidation_outbox
            SET status = 0, attempts = 0, last_error = NULL,
                lease_owner = NULL, lease_token = NULL, lease_expires_at = NULL, next_eligible_at = NULL
            WHERE event_id = ANY(@ids) AND status = 2
            """,
            connection,
            transaction))
        {
            requeue.Parameters.AddWithValue("ids", ids);
            updated = await requeue.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        if (updated != ids.Length)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            AdminJson.Write(output, new { operationId = request.OperationId, mode = "apply", state = "aborted", problems = new List<string> { "a quantidade reaberta difere da seleção" } });
            return AdminExitCodes.Conflict;
        }

        try
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsUnreachable(ex))
        {
            AdminJson.Write(output, new { operationId = request.OperationId, mode = "apply", state = "commit_unknown", error = FailureClassifier.Sanitize(ex) });
            return AdminExitCodes.CommitUnknown;
        }

        AdminJson.Write(output, new { operationId = request.OperationId, mode = "apply", state = "applied", requeued = updated, reason = request.Reason });
        return AdminExitCodes.Success;
    }

    private static string? Validate(RequeueRequest request)
    {
        if (!OperationIdPattern.IsMatch(request.OperationId))
        {
            return "identificador de operação inválido (3 a 64 caracteres: letras, dígitos, '.', '_' ou '-')";
        }

        if (request.EventIds.Count == 0)
        {
            return "nenhum evento selecionado: a lista finita de event_id é obrigatória";
        }

        if (request.EventIds.Count > MaxEventsPerOperation || request.EventIds.Any(id => id == Guid.Empty))
        {
            return $"seleção inválida (1 a {MaxEventsPerOperation} identificadores não vazios)";
        }

        if (!RequeueReasons.All.Contains(request.Reason))
        {
            return "motivo fora da lista fechada (cause_corrected, dependency_restored, false_positive_reviewed)";
        }

        if (string.IsNullOrWhiteSpace(request.ConfirmDatabase))
        {
            return "o banco de origem alvo precisa ser confirmado explicitamente";
        }

        if (request.Apply && string.IsNullOrWhiteSpace(request.PlanDigest))
        {
            return "aplicar exige o digest do plano obtido em um dry-run da mesma seleção";
        }

        return null;
    }

    private static List<string> Problems(Guid[] ids, IReadOnlyList<SelectedRow> rows)
    {
        var problems = new List<string>();
        var found = rows.Select(r => r.EventId).ToHashSet();
        var missing = ids.Count(id => !found.Contains(id));
        if (missing > 0)
        {
            problems.Add($"{missing} evento(s) selecionado(s) não existem na Outbox");
        }

        var outside = rows.Count(r => r.Status != 2);
        if (outside > 0)
        {
            problems.Add($"{outside} evento(s) selecionado(s) não estão em quarentena");
        }

        return problems;
    }

    private static string PlanDigest(IReadOnlyList<SelectedRow> rows) =>
        Digest(string.Join('\n', rows.OrderBy(r => r.EventId).Select(r => FormattableString.Invariant($"{r.EventId:D}|{r.Sequence}|{r.Status}|{r.Attempts}|{r.Revision}"))));

    private static string Digest(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private static async Task<List<SelectedRow>> ReadSelectionAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, string sql, Guid[] ids, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("ids", ids);
        var rows = new List<SelectedRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(new SelectedRow(reader.GetInt64(0), reader.GetGuid(1), reader.GetInt16(2), reader.GetInt32(3), reader.IsDBNull(4) ? null : reader.GetString(4), reader.GetString(5)));
        }

        return rows;
    }

    private static async Task<OperationRow?> ReadOperationAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, string operationId, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "SELECT selection_digest, event_count FROM consolidation_outbox_admin_operation WHERE operation_id = @op", connection, transaction);
        command.Parameters.AddWithValue("op", operationId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? new OperationRow(reader.GetString(0), reader.GetInt32(1)) : null;
    }

    private static int Refuse(TextWriter output, string reason)
    {
        AdminJson.Write(output, new { state = "refused", reason });
        return AdminExitCodes.Refused;
    }

    private static bool IsUnreachable(Exception ex) =>
        ex is NpgsqlException or TimeoutException or InvalidOperationException or OperationCanceledException or System.Net.Sockets.SocketException;

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]{2,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex OperationIdRegex();

    private sealed record SelectedRow(long Sequence, Guid EventId, short Status, int Attempts, string? LastError, string Revision);

    private sealed record OperationRow(string SelectionDigest, int EventCount);
}
