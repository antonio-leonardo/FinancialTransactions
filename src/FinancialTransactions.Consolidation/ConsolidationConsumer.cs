using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FinancialTransactions.Consolidation;

public sealed class ConsolidationConsumerOptions
{
    /// <summary>Espera quando não há pendentes.</summary>
    public TimeSpan IdleDelay { get; set; } = TimeSpan.FromMilliseconds(500);

    /// <summary>Espera quando o serviço aguarda um bootstrap válido.</summary>
    public TimeSpan WaitDelay { get; set; } = TimeSpan.FromSeconds(2);

    public TimeSpan BackoffInitial { get; set; } = TimeSpan.FromSeconds(1);

    public TimeSpan BackoffMax { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Orçamento de uma tentativa (leitura da fase ou um evento, da aquisição à confirmação); estourá-lo é falha transitória.</summary>
    public TimeSpan AttemptBudget { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Validade da reserva de um evento, medida no relógio da origem. Precisa superar o orçamento da tentativa; sem renovação.</summary>
    public TimeSpan LeaseDuration { get; set; } = TimeSpan.FromSeconds(45);

    /// <summary>Eventos processados em sequência (um em voo por vez) antes de reler a fase do bootstrap.</summary>
    public int MaxEventsPerCycle { get; set; } = 100;

    /// <summary>Adiamento depois de uma falha do evento ou de uma liberação por falha de infraestrutura, no relógio da origem.</summary>
    public TimeSpan RetryDelay { get; set; } = TimeSpan.FromSeconds(1);

    public int MaxEventAttempts { get; set; } = 5;

    /// <summary>Intervalo mínimo entre dois avisos "aguardando bootstrap".</summary>
    public TimeSpan WaitWarningInterval { get; set; } = TimeSpan.FromMinutes(1);
}

/// <summary>Valida os parâmetros do consumidor na inicialização: uma configuração incoerente impede o host de subir.</summary>
public sealed class ConsolidationConsumerOptionsValidator : IValidateOptions<ConsolidationConsumerOptions>
{
    public ValidateOptionsResult Validate(string? name, ConsolidationConsumerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var errors = new List<string>();
        if (options.AttemptBudget <= TimeSpan.Zero)
        {
            errors.Add("AttemptBudget precisa ser positivo.");
        }

        if (options.LeaseDuration <= options.AttemptBudget)
        {
            errors.Add("LeaseDuration precisa superar AttemptBudget: a reserva não pode vencer antes do fim da tentativa.");
        }

        if (options.MaxEventsPerCycle < 1)
        {
            errors.Add("MaxEventsPerCycle precisa ser de pelo menos 1.");
        }

        if (options.MaxEventAttempts < 1)
        {
            errors.Add("MaxEventAttempts precisa ser de pelo menos 1.");
        }

        if (options.RetryDelay < TimeSpan.Zero || options.IdleDelay < TimeSpan.Zero || options.WaitDelay < TimeSpan.Zero
            || options.BackoffInitial <= TimeSpan.Zero || options.BackoffMax < options.BackoffInitial)
        {
            errors.Add("Os intervalos de espera não podem ser negativos, e o backoff exige 0 < inicial <= máximo.");
        }

        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }
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
/// Consumidor de uma instância; várias instâncias podem rodar sobre a mesma Outbox. Cada ciclo lê a fase do bootstrap na projeção, confere
/// o registro de controle da origem e então consome (somente o conjunto inicial em <c>loading</c>, a fila inteira em <c>live</c>), com no
/// máximo um evento em voo: reserva durável na origem, aplicação na projeção, confirmação condicional ao token. Cada tentativa usa escopo e
/// <c>DbContext</c> novos. Toda exceção de um ciclo é capturada aqui: o laço nunca propaga ao host e nunca gira sem espera.
/// </summary>
public sealed partial class ConsolidationConsumer(
    IServiceScopeFactory scopes,
    IConsolidationConnectionFactory connections,
    IConsolidationSource source,
    BootstrapPromoter promoter,
    ConsolidationHealthState health,
    ConsolidationInstance instance,
    ConsolidationWorkerCounters counters,
    IConsolidationDiagnostics diagnostics,
    IConsolidationFailurePoints failurePoints,
    IOptions<ConsolidationConsumerOptions> options,
    TimeProvider clock,
    ILogger<ConsolidationConsumer> logger) : BackgroundService
{
    private static readonly TimeSpan ReleaseTimeout = TimeSpan.FromSeconds(5);

    private enum CycleOutcome
    {
        Worked,
        Idle,
        Waiting,
    }

    private enum Step
    {
        Applied,
        AlreadyApplied,
        EventFailed,
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
                var outcome = await RunCycleAsync(stoppingToken).ConfigureAwait(false);
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

                counters.Failed(failureClass);
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

    /// <summary>Orçamento próprio, ligado ao token do host: estourá-lo é falha transitória, não encerramento.</summary>
    private CancellationTokenSource NewBudget(CancellationToken host)
    {
        var budget = CancellationTokenSource.CreateLinkedTokenSource(host);
        budget.CancelAfter(_options.AttemptBudget);
        return budget;
    }

    private async Task<CycleOutcome> RunCycleAsync(CancellationToken host)
    {
        var strings = connections.Open(ConsolidationConnectionRole.Consumer);

        BootstrapStateRow? state;
        CaptureRow? capture = null;
        RebuildGuardRow? rebuildGuard = null;
        Guid? registryActive = null;
        var projectionHasData = false;
        using (var phaseBudget = NewBudget(host))
        {
            var budget = phaseBudget.Token;
            await using (var projection = await BootstrapStore.OpenAsync(strings.Projection, budget).ConfigureAwait(false))
            {
                state = await BootstrapStore.ReadStateAsync(projection, budget).ConfigureAwait(false);
                rebuildGuard = await BootstrapStore.ReadRebuildGuardAsync(projection, budget).ConfigureAwait(false);
                if (state is null)
                {
                    projectionHasData = await BootstrapStore.ProjectionHasDataAsync(projection, budget).ConfigureAwait(false);
                }
            }

            if (state is not null)
            {
                await using var origin = await BootstrapStore.OpenAsync(strings.Origin, budget).ConfigureAwait(false);
                capture = await BootstrapStore.ReadCaptureAsync(origin, budget).ConfigureAwait(false);
                registryActive = await BootstrapStore.ReadRegistryAsync(origin, budget).ConfigureAwait(false);
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

        if (capture is null || capture.Descriptor != state.Descriptor)
        {
            health.Set(DegradedReason.ControlMismatch);
            LogControlMismatch(logger, capture is null);
            diagnostics.WaitingForBootstrap();
            return CycleOutcome.Waiting;
        }

        health.Clear(DegradedReason.ControlMismatch);

        // Reconstrução: um destino que não é a projeção ativa registrada na origem (por exemplo, o banco antigo depois de uma promoção) não é
        // consumido; um destino reconstruído e ainda não promovido também não. Voltar ao banco antigo trocando a connection string não é seguro.
        if (registryActive is { } active && rebuildGuard?.ProjectionId != active)
        {
            health.Set(DegradedReason.ProjectionSuperseded);
            LogProjectionSuperseded(logger);
            diagnostics.WaitingForBootstrap();
            return CycleOutcome.Waiting;
        }

        health.Clear(DegradedReason.ProjectionSuperseded);
        if (rebuildGuard is not null && rebuildGuard.State != RebuildStates.Promoted)
        {
            health.Set(DegradedReason.RebuildInProgress);
            diagnostics.WaitingForBootstrap();
            return CycleOutcome.Waiting;
        }

        health.Clear(DegradedReason.RebuildInProgress);

        return state.Phase == ConsolidationBootstrapState.Live
            ? await ConsumeLiveAsync(host).ConfigureAwait(false)
            : await ConsumeLoadingAsync(state, host).ConfigureAwait(false);
    }

    private async Task<CycleOutcome> ConsumeLiveAsync(CancellationToken host)
    {
        health.Clear(DegradedReason.ReconciliationFailed);
        return await DrainAsync(null, host).ConfigureAwait(false) ? CycleOutcome.Worked : CycleOutcome.Idle;
    }

    private async Task<CycleOutcome> ConsumeLoadingAsync(BootstrapStateRow state, CancellationToken host)
    {
        if (await DrainAsync(state.BootstrapId, host).ConfigureAwait(false))
        {
            return CycleOutcome.Worked;
        }

        using var budgetSource = NewBudget(host);
        var budget = budgetSource.Token;

        // Nada a adquirir no conjunto: só reconcilia quando não resta pendente (nem reservado por outra instância) nem quarentena nele.
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

        var promotion = await promoter.TryPromoteAsync(state.BootstrapId, budget).ConfigureAwait(false);
        switch (promotion.Status)
        {
            case PromotionStatus.Promoted:
                health.Clear(DegradedReason.ReconciliationFailed);
                diagnostics.PhaseChanged(ConsolidationBootstrapState.Live);
                return CycleOutcome.Worked;

            case PromotionStatus.AlreadyLive:
                // Outra instância concluiu: sem regressão e sem falso erro de reconciliação. O próximo ciclo lê live.
                health.Clear(DegradedReason.ReconciliationFailed);
                return CycleOutcome.Worked;

            case PromotionStatus.Busy:
                return CycleOutcome.Idle;

            case PromotionStatus.Diverged:
                health.Set(DegradedReason.ReconciliationFailed);
                diagnostics.ReconciliationFailed(promotion.Deviation ?? "divergência");
                return CycleOutcome.Waiting;

            case PromotionStatus.StateMismatch:
                health.Set(DegradedReason.ControlMismatch);
                return CycleOutcome.Waiting;

            default:
                // Nenhuma linha afetada: outro estado já ocupa a linha. Nunca sobrescreve live; a próxima leitura decide.
                LogPromotionNoRows(logger);
                return CycleOutcome.Waiting;
        }
    }

    /// <summary>Consome em sequência, um evento em voo por vez, até não haver mais elegíveis ou atingir o limite do ciclo. Devolve se houve trabalho.</summary>
    private async Task<bool> DrainAsync(Guid? bootstrapId, CancellationToken host)
    {
        int applied = 0, alreadyApplied = 0, worked = 0;
        try
        {
            while (worked < _options.MaxEventsPerCycle)
            {
                var step = await ProcessNextAsync(bootstrapId, host).ConfigureAwait(false);
                if (step is null)
                {
                    break;
                }

                worked++;
                switch (step)
                {
                    case Step.Applied:
                        applied++;
                        break;
                    case Step.AlreadyApplied:
                        alreadyApplied++;
                        break;
                }
            }
        }
        finally
        {
            if (applied + alreadyApplied > 0)
            {
                diagnostics.BatchApplied(applied, alreadyApplied);
            }
        }

        return worked > 0;
    }

    /// <summary>
    /// Adquire, aplica e confirma um evento. Nulo: nenhum elegível. Falha atribuível ao evento é registrada e não propaga; falha de
    /// infraestrutura libera a reserva própria (se a origem responder) e propaga ao laço, sem contar tentativa do evento.
    /// </summary>
    private async Task<Step?> ProcessNextAsync(Guid? bootstrapId, CancellationToken host)
    {
        using var budgetSource = NewBudget(host);
        var budget = budgetSource.Token;

        var leased = await source.AcquireAsync(instance.Id, bootstrapId, _options.LeaseDuration, budget).ConfigureAwait(false);
        if (leased is null)
        {
            return null;
        }

        counters.Acquired();
        diagnostics.LeaseAcquired(leased.Event.Sequence);
        var consolidationEvent = leased.Event;

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
            // Defeito do evento: conta tentativa, quarentena na quinta; a reserva é liberada pelo mesmo comando condicional.
            counters.EventFailed();
            diagnostics.AttemptFailed(FailureClass.Event, ex.GetType().Name);
            var recording = await source.RecordFailureAsync(
                consolidationEvent.Sequence, leased.Token, FailureClassifier.Sanitize(ex), _options.MaxEventAttempts, _options.RetryDelay, budget).ConfigureAwait(false);
            switch (recording)
            {
                case FailureRecording.LeaseLost:
                    NoteLeaseLost(consolidationEvent.Sequence, "falha do evento");
                    break;
                case FailureRecording.Quarantined:
                    counters.EventQuarantined();
                    diagnostics.EventQuarantined(consolidationEvent.Sequence);
                    break;
            }

            return Step.EventFailed;
        }
        catch (Exception)
        {
            // Infraestrutura ou encerramento antes de a aplicação ser confirmada: nenhuma tentativa de evento é contada. Libera a reserva
            // própria, com adiamento, para não segurá-la até o vencimento; se a origem não responder, o vencimento recupera.
            await TryReleaseAsync(leased).ConfigureAwait(false);
            throw;
        }

        if (outcome == ApplyOutcome.Applied)
        {
            counters.Applied();
        }
        else
        {
            counters.DuplicateAvoided();
        }

        await failurePoints.AfterProjectionCommitBeforeMarkAsync(budget).ConfigureAwait(false);

        bool confirmed;
        try
        {
            confirmed = await source.ConfirmAsync(consolidationEvent.Sequence, leased.Token, budget).ConfigureAwait(false);
        }
        catch (Exception ex) when (FailureClassifier.Classify(ex, host) != FailureClass.HostCancellation)
        {
            // Projeção já confirmada, origem sem resultado: nada é forçado nem liberado. A próxima leitura determina o estado
            // (se ficou pendente, a reentrega não soma de novo) e o backoff segue a classe da exceção.
            counters.UnconfirmedAcknowledgement();
            LogMarkUnconfirmed(logger, consolidationEvent.Sequence);
            throw;
        }

        if (!confirmed)
        {
            // A posse mudou (vencida e readquirida, por exemplo). Nada foi sobrescrito; o dinheiro está protegido pelo marcador.
            NoteLeaseLost(consolidationEvent.Sequence, "confirmação");
        }

        return outcome == ApplyOutcome.Applied ? Step.Applied : Step.AlreadyApplied;
    }

    private async Task TryReleaseAsync(LeasedEvent leased)
    {
        using var timeout = new CancellationTokenSource(ReleaseTimeout);
        try
        {
            if (await source.ReleaseAsync(leased.Event.Sequence, leased.Token, _options.RetryDelay, timeout.Token).ConfigureAwait(false))
            {
                counters.Released();
            }
            else
            {
                // Reserva vencida ou já de outra aquisição: nada foi alterado (nem o agendamento). O vencimento ou a releitura recuperam o evento.
                NoteLeaseLost(leased.Event.Sequence, "liberação");
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            var failureType = ex.GetType().Name;
            LogReleaseFailed(logger, leased.Event.Sequence, failureType);
        }
    }

    private void NoteLeaseLost(long sequence, string stage)
    {
        counters.LeaseLost();
        diagnostics.LeaseLost(sequence, stage);
        LogLeaseLost(logger, sequence, stage);
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

    [LoggerMessage(EventId = 6007, Level = LogLevel.Warning, Message = "Posse perdida no evento {Sequence} ({Stage}): a reserva venceu ou passou a outra instância; nada foi sobrescrito.")]
    private static partial void LogLeaseLost(ILogger logger, long sequence, string stage);

    [LoggerMessage(EventId = 6009, Level = LogLevel.Error, Message = "Este destino não é a projeção ativa registrada na origem (reconstrução promovida); nada será consumido.")]
    private static partial void LogProjectionSuperseded(ILogger logger);

    [LoggerMessage(EventId = 6008, Level = LogLevel.Debug, Message = "Não foi possível liberar a reserva do evento {Sequence} ({FailureType}); o vencimento a recupera.")]
    private static partial void LogReleaseFailed(ILogger logger, long sequence, string failureType);
}
