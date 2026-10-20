using Npgsql;

namespace FinancialTransactions.Consolidation;

/// <summary>Contagem de eventos de um conjunto que ainda não estão confirmados.</summary>
public sealed record SetPendingCounts(long Pending, long Failed);

/// <summary>Evento adquirido: o evento, o token desta aquisição (a posse é do token, não do nome da instância) e o vencimento na origem.</summary>
public sealed record LeasedEvent(ConsolidationEvent Event, Guid Token, DateTimeOffset ExpiresAt);

/// <summary>Resultado de registrar uma falha atribuível ao evento.</summary>
public enum FailureRecording
{
    /// <summary>A posse não era mais da aquisição (vencida, trocada ou evento já fora de pendente): nada foi alterado.</summary>
    LeaseLost,

    /// <summary>Tentativa contada; o evento voltou a pendente, elegível depois do adiamento.</summary>
    Counted,

    /// <summary>Esta tentativa esgotou o orçamento: o evento foi para a quarentena.</summary>
    Quarantined,
}

/// <summary>
/// Fonte do trabalho durável: a Outbox da origem, com reserva persistida. A aquisição é atômica na origem e não é segurada durante
/// o acesso à projeção; as confirmações são condicionais ao evento pendente, ao token da aquisição e à reserva ainda válida.
/// </summary>
public interface IConsolidationSource
{
    /// <summary>
    /// Reserva o próximo evento elegível (pendente, sem reserva válida de outra instância, fora de adiamento; com <paramref name="bootstrapId"/>,
    /// só membros do conjunto inicial). Devolve nulo se não há nenhum. O vencimento usa o relógio da origem.
    /// </summary>
    Task<LeasedEvent?> AcquireAsync(string owner, Guid? bootstrapId, TimeSpan leaseDuration, CancellationToken cancellationToken);

    /// <summary>Confirma o evento depois do commit da projeção. Falso: a posse se perdeu e nada foi alterado (releitura e diagnóstico, nunca sobrescrita).</summary>
    Task<bool> ConfirmAsync(long sequence, Guid token, CancellationToken cancellationToken);

    /// <summary>Conta uma tentativa que falhou por defeito do evento e libera a reserva; ao atingir <paramref name="maxAttempts"/> o evento vai à quarentena.</summary>
    Task<FailureRecording> RecordFailureAsync(long sequence, Guid token, string sanitizedError, int maxAttempts, TimeSpan retryDelay, CancellationToken cancellationToken);

    /// <summary>Libera uma reserva própria sem contar tentativa (falha de infraestrutura, encerramento), com adiamento opcional. Falso: a posse já não era do token.</summary>
    Task<bool> ReleaseAsync(long sequence, Guid token, TimeSpan retryDelay, CancellationToken cancellationToken);

    /// <summary>Pendentes (0, reservados ou não) e em quarentena (2) dentro do conjunto inicial.</summary>
    Task<SetPendingCounts> CountSetPendingAsync(Guid bootstrapId, CancellationToken cancellationToken);
}

public sealed class OutboxConsolidationSource(IConsolidationConnectionFactory connections) : IConsolidationSource
{
    // Uma única instrução na origem: FOR UPDATE SKIP LOCKED seleciona sem esperar concorrentes e o UPDATE grava a reserva antes do commit.
    // Instâncias distintas nunca recebem a mesma posse. A própria instância (no máximo um evento em voo) retoma uma reserva sua que ficou
    // para trás, por exemplo depois de uma confirmação sem resposta. Vencimento e elegibilidade usam o relógio da origem.
    private const string AcquireSql = """
        WITH candidate AS MATERIALIZED (
            SELECT sequence
            FROM consolidation_outbox
            WHERE status = 0
              AND (lease_token IS NULL OR lease_expires_at <= statement_timestamp() OR lease_owner = @owner)
              AND (next_eligible_at IS NULL OR next_eligible_at <= statement_timestamp())
              AND (@bootstrap_id::uuid IS NULL OR bootstrap_id = @bootstrap_id)
            ORDER BY sequence
            FOR UPDATE SKIP LOCKED
            LIMIT 1)
        UPDATE consolidation_outbox o
        SET lease_owner = @owner,
            lease_token = @token,
            lease_expires_at = statement_timestamp() + make_interval(secs => @lease_seconds)
        FROM candidate c
        WHERE o.sequence = c.sequence
        RETURNING o.sequence, o.event_id, o.account_id, o.customer_id, o.type, o.amount, o.effective_at, o.attempts, o.lease_expires_at
        """;

    private const string ConfirmSql = """
        UPDATE consolidation_outbox
        SET status = 1,
            processed_at = statement_timestamp(),
            lease_owner = NULL, lease_token = NULL, lease_expires_at = NULL, next_eligible_at = NULL
        WHERE sequence = @sequence AND status = 0 AND lease_token = @token AND lease_expires_at > statement_timestamp()
        """;

    private const string FailSql = """
        UPDATE consolidation_outbox
        SET attempts = attempts + 1,
            last_error = @error,
            status = CASE WHEN attempts + 1 >= @max THEN 2 ELSE 0 END,
            lease_owner = NULL, lease_token = NULL, lease_expires_at = NULL,
            next_eligible_at = CASE WHEN attempts + 1 >= @max THEN NULL ELSE statement_timestamp() + make_interval(secs => @delay_seconds) END
        WHERE sequence = @sequence AND status = 0 AND lease_token = @token AND lease_expires_at > statement_timestamp()
        RETURNING status
        """;

    private const string ReleaseSql = """
        UPDATE consolidation_outbox
        SET lease_owner = NULL, lease_token = NULL, lease_expires_at = NULL,
            next_eligible_at = CASE WHEN @delay_seconds > 0 THEN statement_timestamp() + make_interval(secs => @delay_seconds) ELSE NULL END
        WHERE sequence = @sequence AND status = 0 AND lease_token = @token
        """;

    public async Task<LeasedEvent?> AcquireAsync(string owner, Guid? bootstrapId, TimeSpan leaseDuration, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(leaseDuration, TimeSpan.Zero);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(AcquireSql, connection);
        command.Parameters.AddWithValue("owner", owner);
        var token = Guid.CreateVersion7();
        command.Parameters.AddWithValue("token", token);
        command.Parameters.AddWithValue("lease_seconds", leaseDuration.TotalSeconds);
        command.Parameters.Add(new NpgsqlParameter("bootstrap_id", NpgsqlTypes.NpgsqlDbType.Uuid) { Value = (object?)bootstrapId ?? DBNull.Value });

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var consolidationEvent = new ConsolidationEvent(
            reader.GetInt64(0),
            reader.GetGuid(1),
            reader.GetGuid(2),
            reader.GetGuid(3),
            reader.GetInt16(4),
            reader.GetDecimal(5),
            reader.GetFieldValue<DateTimeOffset>(6),
            reader.GetInt32(7));
        return new LeasedEvent(consolidationEvent, token, reader.GetFieldValue<DateTimeOffset>(8));
    }

    public async Task<bool> ConfirmAsync(long sequence, Guid token, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(ConfirmSql, connection);
        command.Parameters.AddWithValue("sequence", sequence);
        command.Parameters.AddWithValue("token", token);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    public async Task<FailureRecording> RecordFailureAsync(
        long sequence, Guid token, string sanitizedError, int maxAttempts, TimeSpan retryDelay, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sanitizedError);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(FailSql, connection);
        command.Parameters.AddWithValue("sequence", sequence);
        command.Parameters.AddWithValue("token", token);
        command.Parameters.AddWithValue("error", sanitizedError);
        command.Parameters.AddWithValue("max", maxAttempts);
        command.Parameters.AddWithValue("delay_seconds", retryDelay.TotalSeconds);
        var status = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return status switch
        {
            null => FailureRecording.LeaseLost,
            (short)2 => FailureRecording.Quarantined,
            _ => FailureRecording.Counted,
        };
    }

    public async Task<bool> ReleaseAsync(long sequence, Guid token, TimeSpan retryDelay, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(ReleaseSql, connection);
        command.Parameters.AddWithValue("sequence", sequence);
        command.Parameters.AddWithValue("token", token);
        command.Parameters.AddWithValue("delay_seconds", retryDelay.TotalSeconds);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    public async Task<SetPendingCounts> CountSetPendingAsync(Guid bootstrapId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(
            "SELECT COUNT(*) FILTER (WHERE status = 0), COUNT(*) FILTER (WHERE status = 2) FROM consolidation_outbox WHERE bootstrap_id = @id",
            connection);
        command.Parameters.AddWithValue("id", bootstrapId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        return new SetPendingCounts(reader.GetInt64(0), reader.GetInt64(1));
    }

    private async Task<NpgsqlConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new NpgsqlConnection(connections.Open(ConsolidationConnectionRole.Consumer).Origin);
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
}
