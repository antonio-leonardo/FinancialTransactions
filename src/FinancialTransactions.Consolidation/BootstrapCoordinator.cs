using System.Data;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace FinancialTransactions.Consolidation;

/// <summary>Códigos de saída do coordenador. Nenhum provoca escrita adicional.</summary>
public static class CoordinatorExitCodes
{
    /// <summary>Nada a fazer ou ativação feita.</summary>
    public const int Success = 0;

    /// <summary>Origem ou projeção inalcançável (inclusive na ativação) ou configuração inválida.</summary>
    public const int Unreachable = 2;

    /// <summary>Outro coordenador mantém o lock consultivo.</summary>
    public const int LockBusy = 3;

    /// <summary>Descritores divergentes ou projeção incoerente: diagnóstico, nada alterado.</summary>
    public const int Diverged = 4;

    /// <summary>Resultado do commit da captura desconhecido: nada mais é feito; a reexecução decide pelo registro de controle.</summary>
    public const int CommitUnknown = 5;
}

public enum CoordinatorDecision
{
    /// <summary>Sem captura e sem destino: capturar.</summary>
    Capture,

    /// <summary>Captura confirmada e destino ausente: ativar com os valores da origem.</summary>
    Activate,

    /// <summary>Os dois lados concordam: concluído ou em andamento, nada a alterar.</summary>
    Done,

    /// <summary>Descritores divergentes (ou destino sem captura): diagnóstico, nada alterado.</summary>
    Diverged,
}

public static class CoordinatorDecisionTable
{
    /// <summary>Decisão do passo 2, pelos dois lados e sem reenumerar: a origem manda, o destino só confere.</summary>
    public static CoordinatorDecision Decide(BootstrapDescriptor? capture, BootstrapDescriptor? destination)
    {
        return (capture, destination) switch
        {
            (null, null) => CoordinatorDecision.Capture,
            ({ }, null) => CoordinatorDecision.Activate,
            (null, { }) => CoordinatorDecision.Diverged,
            ({ } c, { } d) => c == d ? CoordinatorDecision.Done : CoordinatorDecision.Diverged,
        };
    }
}

/// <summary>
/// Coordenador do bootstrap (modo <c>--backfill</c>). Nunca consome e nunca espera consumo; nunca faz UPDATE em <c>consolidation_bootstrap</c>.
/// A captura (origem) e a ativação (destino) ficam em bancos diferentes: cada etapa é repetível a partir do registro de controle da origem.
/// </summary>
public sealed partial class BootstrapCoordinator(
    IConsolidationConnectionFactory connections,
    IBootstrapFailurePoints failurePoints,
    ILogger<BootstrapCoordinator> logger)
{
    private const string LockSql = "SELECT pg_try_advisory_lock(hashtext('ft_backfill'))";
    private const string UnlockSql = "SELECT pg_advisory_unlock(hashtext('ft_backfill'))";

    private const string InsertMissingEventsSql = """
        INSERT INTO consolidation_outbox (event_id, account_id, customer_id, type, amount, effective_at, created_at, origin, bootstrap_id, status, attempts)
        SELECT m.id, m.account_id, a.customer_id, m.type, m.amount, m.effective_at, now(), 1, NULL, 0, 0
        FROM movements m
        JOIN accounts a ON a.id = m.account_id
        WHERE NOT EXISTS (SELECT 1 FROM consolidation_outbox o WHERE o.event_id = m.id)
        ORDER BY m.registered_at, m.id
        ON CONFLICT (event_id) DO NOTHING
        """;

    // Pertença por identidade: todas as linhas da Outbox cujo movimento é visível no snapshot, inclusive os eventos online anteriores à captura.
    private const string MarkMembersSql = """
        UPDATE consolidation_outbox o
        SET bootstrap_id = @id
        FROM movements m
        WHERE m.id = o.event_id AND o.bootstrap_id IS NULL
        """;

    public async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        NpgsqlConnection? lockSession = null;
        try
        {
            // Sessão própria e sem pool: fechar a conexão encerra a sessão e libera o lock mesmo se o unlock falhar.
            var lockString = new NpgsqlConnectionStringBuilder(connections.Open(ConsolidationConnectionRole.Bootstrap).Origin) { Pooling = false }.ConnectionString;
            lockSession = await BootstrapStore.OpenAsync(lockString, cancellationToken).ConfigureAwait(false);
            await using (var tryLock = new NpgsqlCommand(LockSql, lockSession))
            {
                if (!(bool)(await tryLock.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) ?? false))
                {
                    LogLockBusy(logger);
                    return CoordinatorExitCodes.LockBusy;
                }
            }

            return await RunLockedAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogUnreachable(logger, FailureClassifier.Sanitize(ex));
            return CoordinatorExitCodes.Unreachable;
        }
        finally
        {
            if (lockSession is not null)
            {
                await ReleaseAsync(lockSession).ConfigureAwait(false);
            }
        }
    }

    private async Task<int> RunLockedAsync(CancellationToken cancellationToken)
    {
        CaptureRow? capture;
        BootstrapStateRow? destination;
        var strings = connections.Open(ConsolidationConnectionRole.Bootstrap);
        await using (var origin = await BootstrapStore.OpenAsync(strings.Origin, cancellationToken).ConfigureAwait(false))
        await using (var projection = await BootstrapStore.OpenAsync(strings.Projection, cancellationToken).ConfigureAwait(false))
        {
            capture = await BootstrapStore.ReadCaptureAsync(origin, cancellationToken).ConfigureAwait(false);
            destination = await BootstrapStore.ReadStateAsync(projection, cancellationToken).ConfigureAwait(false);
        }

        var decision = CoordinatorDecisionTable.Decide(capture?.Descriptor, destination?.Descriptor);
        switch (decision)
        {
            case CoordinatorDecision.Diverged:
                LogDiverged(logger, Describe(capture?.Descriptor), Describe(destination?.Descriptor));
                return CoordinatorExitCodes.Diverged;

            case CoordinatorDecision.Done:
                // A pausa de teste fica antes de qualquer efeito, para provar que uma reexecução que leu "loading" não altera nada
                // quando o serviço avança para live enquanto ela espera.
                await failurePoints.BeforeActivationAsync(cancellationToken).ConfigureAwait(false);
                if (destination is not null)
                {
                    LogDone(logger, destination.Phase == ConsolidationBootstrapState.Live ? "concluído" : "em andamento", destination.BootstrapId, destination.MemberCount);
                }

                return CoordinatorExitCodes.Success;

            case CoordinatorDecision.Capture:
                var captured = await CaptureAsync(cancellationToken).ConfigureAwait(false);
                if (captured.ExitCode is { } exitCode)
                {
                    return exitCode;
                }

                capture = captured.Row;
                await failurePoints.AfterCaptureCommitBeforeActivationAsync(cancellationToken).ConfigureAwait(false);
                break;
        }

        if (capture is null)
        {
            // Inalcançável: Capture e Activate sempre têm a linha de controle. Mantido como defesa, sem alterar nada.
            LogDiverged(logger, Describe(null), Describe(destination?.Descriptor));
            return CoordinatorExitCodes.Diverged;
        }

        return await ActivateAsync(capture, cancellationToken).ConfigureAwait(false);
    }

    private async Task<(int? ExitCode, CaptureRow? Row)> CaptureAsync(CancellationToken cancellationToken)
    {
        await using var origin = await BootstrapStore.OpenAsync(connections.Open(ConsolidationConnectionRole.Bootstrap).Origin, cancellationToken).ConfigureAwait(false);
        var bootstrapId = Guid.CreateVersion7();

        await using var transaction = await origin.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken).ConfigureAwait(false);

        // O primeiro comando é o que estabelece o snapshot; BeginTransaction sozinho não o estabelece.
        string snapshot;
        await using (var snapshotCommand = new NpgsqlCommand("SELECT pg_current_snapshot()::text", origin, transaction))
        {
            var scalar = await snapshotCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            snapshot = scalar as string ?? throw new InvalidOperationException("pg_current_snapshot() não devolveu texto.");
        }

        await failurePoints.AfterSnapshotEstablishedAsync(cancellationToken).ConfigureAwait(false);

        await using (var insertMissing = new NpgsqlCommand(InsertMissingEventsSql, origin, transaction))
        {
            await insertMissing.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        long members;
        await using (var mark = new NpgsqlCommand(MarkMembersSql, origin, transaction))
        {
            mark.Parameters.AddWithValue("id", bootstrapId);
            members = await mark.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        var capturedAt = DateTimeOffset.UtcNow;
        await using (var control = new NpgsqlCommand(
            "INSERT INTO consolidation_bootstrap_capture (id, bootstrap_id, member_count, captured_at, snapshot) VALUES (1, @id, @count, @at, @snapshot)",
            origin,
            transaction))
        {
            control.Parameters.AddWithValue("id", bootstrapId);
            control.Parameters.AddWithValue("count", members);
            control.Parameters.AddWithValue("at", capturedAt);
            control.Parameters.AddWithValue("snapshot", snapshot);
            await control.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        try
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Resultado do commit desconhecido: não se presume rollback. A reexecução consulta o registro de controle.
            LogCommitUnknown(logger, FailureClassifier.Sanitize(ex));
            return (CoordinatorExitCodes.CommitUnknown, null);
        }

        LogCaptured(logger, bootstrapId, members);
        return (null, new CaptureRow(bootstrapId, members, capturedAt, snapshot));
    }

    private async Task<int> ActivateAsync(CaptureRow capture, CancellationToken cancellationToken)
    {
        // Conexão nova, resolvida agora: a etapa é repetível e o destino pode ter ficado inalcançável desde a captura.
        await failurePoints.BeforeActivationAsync(cancellationToken).ConfigureAwait(false);
        await using var projection = await BootstrapStore.OpenAsync(connections.Open(ConsolidationConnectionRole.Bootstrap).Projection, cancellationToken).ConfigureAwait(false);
        var state = await BootstrapStore.ActivateAsync(projection, capture, cancellationToken).ConfigureAwait(false);
        if (state is not null && state.Descriptor == capture.Descriptor)
        {
            LogActivated(logger, state.Phase, state.BootstrapId, state.MemberCount);
            return CoordinatorExitCodes.Success;
        }

        LogDiverged(logger, Describe(capture.Descriptor), Describe(state?.Descriptor));
        return CoordinatorExitCodes.Diverged;
    }

    private async Task ReleaseAsync(NpgsqlConnection lockSession)
    {
        try
        {
            if (lockSession.State == ConnectionState.Open)
            {
                await using var unlock = new NpgsqlCommand(UnlockSql, lockSession);
                await unlock.ExecuteScalarAsync(CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is NpgsqlException or InvalidOperationException)
        {
            // A sessão é fechada logo abaixo (sem pool), o que também libera o lock.
            LogUnlockFailed(logger, ex.GetType().Name);
        }
        finally
        {
            await lockSession.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static string Describe(BootstrapDescriptor? descriptor) =>
        descriptor is null ? "ausente" : $"bootstrap_id={descriptor.BootstrapId} member_count={descriptor.MemberCount}";

    [LoggerMessage(EventId = 5000, Level = LogLevel.Warning, Message = "Bootstrap já em execução; nada foi tocado.")]
    private static partial void LogLockBusy(ILogger logger);

    [LoggerMessage(EventId = 5001, Level = LogLevel.Error, Message = "Origem ou projeção inalcançável, ou configuração inválida ({Failure}); nada mais foi feito.")]
    private static partial void LogUnreachable(ILogger logger, string failure);

    [LoggerMessage(EventId = 5002, Level = LogLevel.Error, Message = "Descritores divergentes; nada foi alterado. Origem: {Capture}. Destino: {Destination}.")]
    private static partial void LogDiverged(ILogger logger, string capture, string destination);

    [LoggerMessage(EventId = 5003, Level = LogLevel.Information, Message = "Bootstrap {State} (bootstrap_id {BootstrapId}, {MemberCount} membros); nada foi alterado.")]
    private static partial void LogDone(ILogger logger, string state, Guid bootstrapId, long memberCount);

    [LoggerMessage(EventId = 5004, Level = LogLevel.Information, Message = "Captura confirmada: bootstrap_id {BootstrapId}, {MemberCount} membros.")]
    private static partial void LogCaptured(ILogger logger, Guid bootstrapId, long memberCount);

    [LoggerMessage(EventId = 5005, Level = LogLevel.Error, Message = "Resultado do commit da captura desconhecido ({Failure}); a reexecução decide pelo registro de controle.")]
    private static partial void LogCommitUnknown(ILogger logger, string failure);

    [LoggerMessage(EventId = 5006, Level = LogLevel.Information, Message = "Ativação concluída ou já existente (fase {Phase}, bootstrap_id {BootstrapId}, {MemberCount} membros).")]
    private static partial void LogActivated(ILogger logger, string phase, Guid bootstrapId, long memberCount);

    [LoggerMessage(EventId = 5007, Level = LogLevel.Warning, Message = "Falha ao liberar o lock consultivo ({FailureType}); a sessão será encerrada.")]
    private static partial void LogUnlockFailed(ILogger logger, string failureType);
}
