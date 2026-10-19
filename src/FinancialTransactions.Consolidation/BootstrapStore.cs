using Npgsql;

namespace FinancialTransactions.Consolidation;

/// <summary>Identidade de uma captura: o que a origem e o destino precisam concordar.</summary>
public sealed record BootstrapDescriptor(Guid BootstrapId, long MemberCount);

/// <summary>Registro de controle da captura, na origem (<c>consolidation_bootstrap_capture</c>).</summary>
public sealed record CaptureRow(Guid BootstrapId, long MemberCount, DateTimeOffset CapturedAt, string Snapshot)
{
    public BootstrapDescriptor Descriptor => new(BootstrapId, MemberCount);
}

/// <summary>Linha de bootstrap no destino (<c>consolidation_bootstrap</c>).</summary>
public sealed record BootstrapStateRow(Guid BootstrapId, string Phase, long MemberCount, DateTimeOffset CapturedAt, DateTimeOffset? AppliedAt, DateTimeOffset? ReconciledAt)
{
    public BootstrapDescriptor Descriptor => new(BootstrapId, MemberCount);
}

/// <summary>Acesso às duas tabelas de controle do bootstrap. O código do projeto nunca apaga nem regrava o registro da origem.</summary>
public static class BootstrapStore
{
    public static async Task<CaptureRow?> ReadCaptureAsync(NpgsqlConnection origin, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(origin);
        await using var command = new NpgsqlCommand(
            "SELECT bootstrap_id, member_count, captured_at, snapshot FROM consolidation_bootstrap_capture WHERE id = 1", origin);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new CaptureRow(reader.GetGuid(0), reader.GetInt64(1), reader.GetFieldValue<DateTimeOffset>(2), reader.GetString(3))
            : null;
    }

    public static async Task<BootstrapStateRow?> ReadStateAsync(NpgsqlConnection projection, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(projection);
        await using var command = new NpgsqlCommand(
            "SELECT bootstrap_id, phase, member_count, captured_at, applied_at, reconciled_at FROM consolidation_bootstrap WHERE id = 1", projection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new BootstrapStateRow(
            reader.GetGuid(0),
            reader.GetString(1),
            reader.GetInt64(2),
            reader.GetFieldValue<DateTimeOffset>(3),
            reader.IsDBNull(4) ? null : reader.GetFieldValue<DateTimeOffset>(4),
            reader.IsDBNull(5) ? null : reader.GetFieldValue<DateTimeOffset>(5));
    }

    /// <summary>Verdadeiro se a projeção tem agregados ou marcadores: sem linha de bootstrap isso é incoerência.</summary>
    public static async Task<bool> ProjectionHasDataAsync(NpgsqlConnection projection, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(projection);
        await using var command = new NpgsqlCommand(
            "SELECT EXISTS (SELECT 1 FROM daily_account_consolidation) OR EXISTS (SELECT 1 FROM processed_events)", projection);
        return (bool)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) ?? false);
    }

    /// <summary>Ativação idempotente: insere em phase loading se ainda não há linha. Nunca atualiza. Devolve a linha que ficou.</summary>
    public static async Task<BootstrapStateRow?> ActivateAsync(NpgsqlConnection projection, CaptureRow capture, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(projection);
        ArgumentNullException.ThrowIfNull(capture);
        await using (var insert = new NpgsqlCommand(
            """
            INSERT INTO consolidation_bootstrap (id, bootstrap_id, phase, member_count, captured_at, applied_at, reconciled_at)
            VALUES (1, @bootstrap_id, 'loading', @member_count, @captured_at, NULL, NULL)
            ON CONFLICT (id) DO NOTHING
            """,
            projection))
        {
            insert.Parameters.AddWithValue("bootstrap_id", capture.BootstrapId);
            insert.Parameters.AddWithValue("member_count", capture.MemberCount);
            insert.Parameters.AddWithValue("captured_at", capture.CapturedAt);
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        return await ReadStateAsync(projection, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<NpgsqlConnection> OpenAsync(string connectionString, CancellationToken cancellationToken)
    {
        var connection = new NpgsqlConnection(connectionString);
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
