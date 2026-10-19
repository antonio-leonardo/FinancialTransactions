using System.Data;
using Npgsql;

namespace FinancialTransactions.Consolidation;

/// <summary>Contagem de eventos de um conjunto que ainda não estão confirmados.</summary>
public sealed record SetPendingCounts(long Pending, long Failed);

/// <summary>Fonte do trabalho durável: a Outbox da origem. Consumidor único e sequencial nesta fatia.</summary>
public interface IConsolidationSource
{
    /// <summary>Eventos pendentes em ordem de sequência; com <paramref name="bootstrapId"/>, só os membros do conjunto inicial.</summary>
    Task<IReadOnlyList<ConsolidationEvent>> ReadPendingBatchAsync(int size, Guid? bootstrapId, CancellationToken cancellationToken);

    /// <summary>Confirma o evento depois do commit da projeção. Só sai de pendente.</summary>
    Task MarkProcessedAsync(long sequence, CancellationToken cancellationToken);

    /// <summary>Conta uma tentativa que falhou por defeito do evento; ao atingir <paramref name="maxAttempts"/> o evento vai à quarentena.</summary>
    Task MarkFailedAsync(long sequence, string sanitizedError, int maxAttempts, CancellationToken cancellationToken);

    /// <summary>Pendentes (0) e em quarentena (2) dentro do conjunto inicial.</summary>
    Task<SetPendingCounts> CountSetPendingAsync(Guid bootstrapId, CancellationToken cancellationToken);
}

public sealed class OutboxConsolidationSource(IConsolidationConnectionFactory connections) : IConsolidationSource
{
    // A leitura trava as linhas com SKIP LOCKED só dentro de uma transação curta, que termina antes de qualquer acesso à projeção.
    private const string ReadSql = """
        SELECT sequence, event_id, account_id, customer_id, type, amount, effective_at, attempts
        FROM consolidation_outbox
        WHERE status = 0 AND (@bootstrap_id::uuid IS NULL OR bootstrap_id = @bootstrap_id)
        ORDER BY sequence
        FOR UPDATE SKIP LOCKED
        LIMIT @size
        """;

    public async Task<IReadOnlyList<ConsolidationEvent>> ReadPendingBatchAsync(int size, Guid? bootstrapId, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(size, 1);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(ReadSql, connection, transaction);
        command.Parameters.Add(new NpgsqlParameter("bootstrap_id", NpgsqlTypes.NpgsqlDbType.Uuid) { Value = (object?)bootstrapId ?? DBNull.Value });
        command.Parameters.AddWithValue("size", size);

        var events = new List<ConsolidationEvent>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                events.Add(new ConsolidationEvent(
                    reader.GetInt64(0),
                    reader.GetGuid(1),
                    reader.GetGuid(2),
                    reader.GetGuid(3),
                    reader.GetInt16(4),
                    reader.GetDecimal(5),
                    reader.GetFieldValue<DateTimeOffset>(6),
                    reader.GetInt32(7)));
            }
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return events;
    }

    public async Task MarkProcessedAsync(long sequence, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(
            "UPDATE consolidation_outbox SET status = 1, processed_at = now() WHERE sequence = @sequence AND status = 0", connection);
        command.Parameters.AddWithValue("sequence", sequence);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task MarkFailedAsync(long sequence, string sanitizedError, int maxAttempts, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sanitizedError);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(
            """
            UPDATE consolidation_outbox
            SET attempts = attempts + 1,
                last_error = @error,
                status = CASE WHEN attempts + 1 >= @max THEN 2 ELSE status END
            WHERE sequence = @sequence AND status = 0
            """,
            connection);
        command.Parameters.AddWithValue("sequence", sequence);
        command.Parameters.AddWithValue("error", sanitizedError);
        command.Parameters.AddWithValue("max", maxAttempts);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
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
