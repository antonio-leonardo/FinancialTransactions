using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace FinancialTransactions.Consolidation;

public sealed class ConsolidationConsumerOptions
{
    public int BatchSize { get; set; } = 50;

    /// <summary>Espera quando não há pendentes.</summary>
    public TimeSpan IdleDelay { get; set; } = TimeSpan.FromMilliseconds(500);

    /// <summary>Espera quando o serviço aguarda um bootstrap válido.</summary>
    public TimeSpan WaitDelay { get; set; } = TimeSpan.FromSeconds(2);

    public TimeSpan BackoffInitial { get; set; } = TimeSpan.FromSeconds(1);

    public TimeSpan BackoffMax { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Orçamento de uma tentativa; estourá-lo é falha transitória (não é o token do host).</summary>
    public TimeSpan AttemptBudget { get; set; } = TimeSpan.FromSeconds(30);

    public int MaxEventAttempts { get; set; } = 5;

    /// <summary>Intervalo mínimo entre dois avisos "aguardando bootstrap".</summary>
    public TimeSpan WaitWarningInterval { get; set; } = TimeSpan.FromMinutes(1);
}

/// <summary>Espera exponencial com teto; reinicia depois do primeiro sucesso.</summary>
public sealed class Backoff
{
    private readonly TimeSpan _initial;
    private readonly TimeSpan _max;
    private TimeSpan _next;

    public Backoff(TimeSpan initial, TimeSpan max)
    {
        _initial = initial;
        _max = max;
        _next = initial;
    }

    public TimeSpan Next()
    {
        var current = _next;
        _next = TimeSpan.FromTicks(Math.Min(_next.Ticks * 2, _max.Ticks));
        return current;
    }

    public void Reset() => _next = _initial;
}

/// <summary>
/// Consumidor único e sequencial. Cada ciclo lê a fase do bootstrap na projeção, confere o registro de controle da origem e então consome
/// (somente o conjunto inicial em <c>loading</c>, a fila inteira em <c>live</c>). Cada tentativa usa escopo e <c>DbContext</c> novos. Toda
/// exceção de um ciclo é capturada aqui: o laço nunca propaga ao host e nunca gira sem espera.
/// </summary>
public sealed partial class ConsolidationConsumer(
    IServiceScopeFactory scopes,
    IConsolidationConnectionFactory connections,
    IConsolidationSource source,
    BootstrapReconciler reconciler,
    ConsolidationHealthState health,
    IConsolidationDiagnostics diagnostics,
    IConsolidationFailurePoints failurePoints,
    IOptions<ConsolidationConsumerOptions> options,
    TimeProvider clock,
    ILogger<ConsolidationConsumer> logger) : BackgroundService
{
    private enum CycleOutcome
    {
        Worked,
        Idle,
        Waiting,
    }

    private readonly ConsolidationConsumerOptions _options = options.Value;
    private DateTimeOffset _lastWaitWarning = DateTimeOffset.MinValue;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var backoff = new Backoff(_options.BackoffInitial, _options.BackoffMax);
        while (!stoppingToken.IsCancellationRequested)
        {
            TimeSpan delay;
            try
            {
                using var budget = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                budget.CancelAfter(_options.AttemptBudget);
                var outcome = await RunCycleAsync(budget.Token, stoppingToken).ConfigureAwait(false);
                health.OnCycleCompleted();
                backoff.Reset();
                delay = outcome switch
                {
                    CycleOutcome.Worked => TimeSpan.Zero,
                    CycleOutcome.Waiting => _options.WaitDelay,
                    _ => _options.IdleDelay,
                };
            }
            catch (Exception ex)
            {
                var failureClass = FailureClassifier.Classify(ex, stoppingToken);
                if (failureClass == FailureClass.HostCancellation)
                {
                    break;
                }

                diagnostics.AttemptFailed(failureClass, ex.GetType().Name);
                if (failureClass == FailureClass.Environment)
                {
                    health.Set(DegradedReason.EnvironmentFailure);
                }

                LogAttemptFailed(logger, failureClass, FailureClassifier.Sanitize(ex));
                delay = backoff.Next();
            }

            if (delay > TimeSpan.Zero)
            {
                try
                {
                    await Task.Delay(delay, clock, stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    private async Task<CycleOutcome> RunCycleAsync(CancellationToken budget, CancellationToken host)
    {
        var strings = connections.Open(ConsolidationConnectionRole.Consumer);

        BootstrapStateRow? state;
        bool projectionHasData = false;
        await using (var projection = await BootstrapStore.OpenAsync(strings.Projection, budget).ConfigureAwait(false))
        {
            state = await BootstrapStore.ReadStateAsync(projection, budget).ConfigureAwait(false);
            if (state is null)
            {
                projectionHasData = await BootstrapStore.ProjectionHasDataAsync(projection, budget).ConfigureAwait(false);
            }
        }

        if (state is null)
        {
            // Ausência nunca é interpretada como live: nada é consumido, nem com filtro nem sem.
            health.Clear(DegradedReason.ControlMismatch);
            if (projectionHasData)
            {
                health.Clear(DegradedReason.WaitingForBootstrap);
                health.Set(DegradedReason.ProjectionIncoherent);
                LogProjectionIncoherent(logger);
            }
            else
            {
                health.Clear(DegradedReason.ProjectionIncoherent);
                health.Set(DegradedReason.WaitingForBootstrap);
                WarnWaiting();
            }

            diagnostics.WaitingForBootstrap();
            return CycleOutcome.Waiting;
        }

        health.Clear(DegradedReason.WaitingForBootstrap);
        health.Clear(DegradedReason.ProjectionIncoherent);

        CaptureRow? capture;
        await using (var origin = await BootstrapStore.OpenAsync(strings.Origin, budget).ConfigureAwait(false))
        {
            capture = await BootstrapStore.ReadCaptureAsync(origin, budget).ConfigureAwait(false);
        }

        if (capture is null || capture.Descriptor != state.Descriptor)
        {
            health.Set(DegradedReason.ControlMismatch);
            LogControlMismatch(logger, capture is null);
            diagnostics.WaitingForBootstrap();
            return CycleOutcome.Waiting;
        }

        health.Clear(DegradedReason.ControlMismatch);

        return state.Phase == ConsolidationBootstrapState.Live
            ? await ConsumeLiveAsync(budget, host).ConfigureAwait(false)
            : await ConsumeLoadingAsync(state, budget, host).ConfigureAwait(false);
    }

    private async Task<CycleOutcome> ConsumeLiveAsync(CancellationToken budget, CancellationToken host)
    {
        health.Clear(DegradedReason.ReconciliationFailed);
        var batch = await source.ReadPendingBatchAsync(_options.BatchSize, null, budget).ConfigureAwait(false);
        return batch.Count == 0 ? CycleOutcome.Idle : await ApplyBatchAsync(batch, budget, host).ConfigureAwait(false);
    }

    private async Task<CycleOutcome> ConsumeLoadingAsync(BootstrapStateRow state, CancellationToken budget, CancellationToken host)
    {
        var batch = await source.ReadPendingBatchAsync(_options.BatchSize, state.BootstrapId, budget).ConfigureAwait(false);
        if (batch.Count > 0)
        {
            return await ApplyBatchAsync(batch, budget, host).ConfigureAwait(false);
        }

        // Nada a consumir no conjunto: só reconcilia quando não resta pendente nem quarentena nele.
        var counts = await source.CountSetPendingAsync(state.BootstrapId, budget).ConfigureAwait(false);
        if (counts.Failed > 0)
        {
            health.Set(DegradedReason.ReconciliationFailed);
            LogSetQuarantined(logger, counts.Failed);
            return CycleOutcome.Waiting;
        }

        if (counts.Pending > 0)
        {
            return CycleOutcome.Idle;
        }

        var result = await reconciler.ReconcileAsync(state.BootstrapId, budget).ConfigureAwait(false);
        if (!result.Equal)
        {
            health.Set(DegradedReason.ReconciliationFailed);
            diagnostics.ReconciliationFailed(result.FirstDeviation ?? "divergência");
            return CycleOutcome.Waiting;
        }

        var promoted = await PromoteAsync(state.BootstrapId, budget).ConfigureAwait(false);
        if (promoted)
        {
            health.Clear(DegradedReason.ReconciliationFailed);
            diagnostics.PhaseChanged(ConsolidationBootstrapState.Live);
            return CycleOutcome.Worked;
        }

        // Nenhuma linha afetada: outro estado já ocupa a linha. Nunca sobrescreve live; a próxima leitura decide.
        LogPromotionNoRows(logger);
        return CycleOutcome.Waiting;
    }

    /// <summary>Promoção condicional e local, a única do projeto: nunca altera uma linha que já esteja live, e não existe caminho inverso.</summary>
    private async Task<bool> PromoteAsync(Guid bootstrapId, CancellationToken cancellationToken)
    {
        await using var connection = await BootstrapStore.OpenAsync(connections.Open(ConsolidationConnectionRole.Reconcile).Projection, cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(
            """
            UPDATE consolidation_bootstrap
            SET phase = 'live', applied_at = @at, reconciled_at = @at
            WHERE id = 1 AND bootstrap_id = @id AND phase = 'loading'
            """,
            connection);
        command.Parameters.AddWithValue("at", clock.GetUtcNow());
        command.Parameters.AddWithValue("id", bootstrapId);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    private async Task<CycleOutcome> ApplyBatchAsync(IReadOnlyList<ConsolidationEvent> batch, CancellationToken budget, CancellationToken host)
    {
        int applied = 0, alreadyApplied = 0;
        foreach (var consolidationEvent in batch)
        {
            ApplyOutcome outcome;
            try
            {
                // Escopo e DbContext novos a cada tentativa.
                using var scope = scopes.CreateScope();
                scope.ServiceProvider.GetRequiredService<ConsolidationScopeRole>().Role = ConsolidationConnectionRole.Consumer;
                outcome = await scope.ServiceProvider.GetRequiredService<DailyConsolidationApplier>().ApplyAsync(consolidationEvent, budget).ConfigureAwait(false);
            }
            catch (Exception ex) when (FailureClassifier.Classify(ex, host) == FailureClass.Event)
            {
                // Defeito do evento: conta tentativa, quarentena na quinta, e o lote segue para o próximo.
                diagnostics.AttemptFailed(FailureClass.Event, ex.GetType().Name);
                await source.MarkFailedAsync(consolidationEvent.Sequence, FailureClassifier.Sanitize(ex), _options.MaxEventAttempts, budget).ConfigureAwait(false);
                if (consolidationEvent.Attempts + 1 >= _options.MaxEventAttempts)
                {
                    diagnostics.EventQuarantined(consolidationEvent.Sequence);
                }

                continue;
            }

            await failurePoints.AfterProjectionCommitBeforeMarkAsync(budget).ConfigureAwait(false);

            try
            {
                await source.MarkProcessedAsync(consolidationEvent.Sequence, budget).ConfigureAwait(false);
            }
            catch (Exception ex) when (FailureClassifier.Classify(ex, host) != FailureClass.HostCancellation)
            {
                // Projeção já confirmada, origem sem resultado: nada é forçado. A próxima leitura determina o estado
                // (se ficou pendente, a reentrega não soma de novo) e o backoff segue a classe da exceção.
                LogMarkUnconfirmed(logger, consolidationEvent.Sequence);
                throw;
            }

            if (outcome == ApplyOutcome.Applied)
            {
                applied++;
            }
            else
            {
                alreadyApplied++;
            }
        }

        diagnostics.BatchApplied(applied, alreadyApplied);
        return CycleOutcome.Worked;
    }

    private void WarnWaiting()
    {
        var now = clock.GetUtcNow();
        if (now - _lastWaitWarning >= _options.WaitWarningInterval)
        {
            _lastWaitWarning = now;
            LogWaitingForBootstrap(logger);
        }
    }

    [LoggerMessage(EventId = 6000, Level = LogLevel.Warning, Message = "Tentativa do consumidor falhou (classe {FailureClass}): {Failure}.")]
    private static partial void LogAttemptFailed(ILogger logger, FailureClass failureClass, string failure);

    [LoggerMessage(EventId = 6001, Level = LogLevel.Warning, Message = "Aguardando bootstrap: nenhum evento será consumido até o coordenador ativar a projeção.")]
    private static partial void LogWaitingForBootstrap(ILogger logger);

    [LoggerMessage(EventId = 6002, Level = LogLevel.Error, Message = "Projeção com dados e sem linha de bootstrap: incoerência; nada será consumido nem apagado.")]
    private static partial void LogProjectionIncoherent(ILogger logger);

    [LoggerMessage(EventId = 6003, Level = LogLevel.Error, Message = "Registro de controle da origem diverge da projeção (ausente na origem: {MissingOnOrigin}); nada será consumido.")]
    private static partial void LogControlMismatch(ILogger logger, bool missingOnOrigin);

    [LoggerMessage(EventId = 6004, Level = LogLevel.Error, Message = "{Failed} evento(s) do conjunto inicial em quarentena: a projeção permanece em loading.")]
    private static partial void LogSetQuarantined(ILogger logger, long failed);

    [LoggerMessage(EventId = 6005, Level = LogLevel.Error, Message = "A promoção para live não afetou nenhuma linha; o estado será relido, nunca sobrescrito.")]
    private static partial void LogPromotionNoRows(ILogger logger);

    [LoggerMessage(EventId = 6006, Level = LogLevel.Warning, Message = "Resultado não confirmado: evento {Sequence} aplicado na projeção, mas a origem não confirmou a marcação.")]
    private static partial void LogMarkUnconfirmed(ILogger logger, long sequence);
}
