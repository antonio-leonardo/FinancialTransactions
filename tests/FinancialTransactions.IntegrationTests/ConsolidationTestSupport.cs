using System.Collections.Concurrent;
using FinancialTransactions.Api.Movements;
using FinancialTransactions.Consolidation;

namespace FinancialTransactions.IntegrationTests;

/// <summary>Ponto de sincronização de teste: o código sob teste chega e espera; o teste observa a chegada e libera quando quiser.</summary>
public sealed class Gate
{
    private readonly TaskCompletionSource _reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task Reached => _reached.Task;

    public bool IsReached => _reached.Task.IsCompleted;

    public void Release() => _release.TrySetResult();

    /// <summary>Chamado pelo código sob teste. Respeita o cancelamento (encerramento do host), para nunca prender o desligamento.</summary>
    public async ValueTask PassAsync(CancellationToken cancellationToken)
    {
        _reached.TrySetResult();
        await _release.Task.WaitAsync(cancellationToken);
    }
}

/// <summary>
/// Fábrica de conexão de teste, consultada a cada tentativa: pode tornar origem ou projeção inalcançáveis só para um papel (porta 1,
/// conexão recusada) e depois restaurá-las, sem tocar no host. Conta e registra as chamadas por papel.
/// </summary>
public sealed class TestConnectionFactory(ConsolidationConnectionStrings real) : IConsolidationConnectionFactory
{
    public const string Unreachable = "Host=127.0.0.1;Port=1;Database=unreachable;Username=ft;Password=ft_local_dev_only;Timeout=1;Command Timeout=1";

    private readonly ConcurrentDictionary<ConsolidationConnectionRole, int> _calls = new();
    private readonly ConcurrentDictionary<ConsolidationConnectionRole, ConsolidationConnectionStrings> _overrides = new();
    private readonly ConcurrentQueue<(ConsolidationConnectionRole Role, string Projection)> _log = new();

    public ConsolidationConnectionStrings Real => real;

    /// <summary>Intercepta a chamada de número <c>n</c> do papel e pode devolver outras strings (ex.: origem inalcançável só na 2.ª chamada).</summary>
    public Func<ConsolidationConnectionRole, int, ConsolidationConnectionStrings, ConsolidationConnectionStrings?>? Interceptor { get; set; }

    public ConsolidationConnectionStrings Open(ConsolidationConnectionRole role)
    {
        var number = _calls.AddOrUpdate(role, 1, (_, current) => current + 1);
        var strings = _overrides.TryGetValue(role, out var overridden) ? overridden : real;
        if (Interceptor?.Invoke(role, number, strings) is { } intercepted)
        {
            strings = intercepted;
        }

        _log.Enqueue((role, strings.Projection));
        return strings;
    }

    public int Calls(ConsolidationConnectionRole role) => _calls.TryGetValue(role, out var count) ? count : 0;

    /// <summary>Projeções efetivamente entregues a chamadores do papel (para provar que a consulta usou a conexão inalcançável).</summary>
    public IReadOnlyList<string> ProjectionsServed(ConsolidationConnectionRole role) => _log.Where(e => e.Role == role).Select(e => e.Projection).ToList();

    public void RedirectProjection(ConsolidationConnectionRole role, string connectionString) =>
        _overrides[role] = Current(role) with { Projection = connectionString };

    public void RedirectOrigin(ConsolidationConnectionRole role, string connectionString) =>
        _overrides[role] = Current(role) with { Origin = connectionString };

    public void Restore(ConsolidationConnectionRole role) => _overrides.TryRemove(role, out _);

    private ConsolidationConnectionStrings Current(ConsolidationConnectionRole role) => _overrides.TryGetValue(role, out var overridden) ? overridden : real;
}

public sealed record DiagEvent(DateTimeOffset At, string Kind, string? Detail, FailureClass? Class);

/// <summary>Sink de teste: guarda os eventos do consumidor real com o instante de cada um.</summary>
public sealed class TestDiagnostics : IConsolidationDiagnostics
{
    private readonly ConcurrentQueue<DiagEvent> _events = new();

    /// <summary>Executado de forma síncrona, na thread do consumidor, logo depois da promoção para live e antes de qualquer outro consumo.</summary>
    public Action<string>? OnPhaseChanged { get; set; }

    public IReadOnlyList<DiagEvent> Events => _events.ToList();

    public int Count(string kind) => _events.Count(e => e.Kind == kind);

    public IReadOnlyList<DiagEvent> Failures(FailureClass failureClass) =>
        _events.Where(e => e.Kind == "AttemptFailed" && e.Class == failureClass).ToList();

    public void AttemptFailed(FailureClass failureClass, string failureType) => Add("AttemptFailed", failureType, failureClass);

    public void BatchApplied(int applied, int alreadyApplied) => Add("BatchApplied", $"{applied}/{alreadyApplied}");

    public void PhaseChanged(string phase)
    {
        Add("PhaseChanged", phase);
        OnPhaseChanged?.Invoke(phase);
    }

    public void WaitingForBootstrap() => Add("WaitingForBootstrap");

    public void ReconciliationFailed(string firstDeviation) => Add("ReconciliationFailed", firstDeviation);

    public void EventQuarantined(long sequence) => Add("EventQuarantined", sequence.ToString(System.Globalization.CultureInfo.InvariantCulture));

    public void LeaseAcquired(long sequence) => Add("LeaseAcquired", sequence.ToString(System.Globalization.CultureInfo.InvariantCulture));

    public void LeaseLost(long sequence, string stage) => Add("LeaseLost", $"{sequence}/{stage}");

    private void Add(string kind, string? detail = null, FailureClass? failureClass = null) =>
        _events.Enqueue(new DiagEvent(DateTimeOffset.UtcNow, kind, detail, failureClass));
}

/// <summary>Pontos de falha do consumidor: cada comportamento dispara uma única vez (a falha injetada acontece e a tentativa seguinte passa).</summary>
public sealed class TestConsolidationFailurePoints : IConsolidationFailurePoints
{
    private Func<CancellationToken, Task>? _inside;
    private Func<CancellationToken, Task>? _afterCommit;

    public void InsideProjectionTransactionOnce(Func<CancellationToken, Task> behavior) => _inside = behavior;

    public void AfterProjectionCommitBeforeMarkOnce(Func<CancellationToken, Task> behavior) => _afterCommit = behavior;

    public async ValueTask InsideProjectionTransactionAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _inside, null) is { } behavior)
        {
            await behavior(cancellationToken);
        }
    }

    public async ValueTask AfterProjectionCommitBeforeMarkAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _afterCommit, null) is { } behavior)
        {
            await behavior(cancellationToken);
        }
    }
}

/// <summary>Pontos de pausa do coordenador. Cada gate, se definido, segura a execução no ponto correspondente.</summary>
public sealed class TestBootstrapFailurePoints : IBootstrapFailurePoints
{
    public Gate? AfterSnapshotEstablished { get; set; }

    public Func<CancellationToken, Task>? AfterCaptureCommitBeforeActivation { get; set; }

    public Gate? BeforeActivation { get; set; }

    public async ValueTask AfterSnapshotEstablishedAsync(CancellationToken cancellationToken)
    {
        if (AfterSnapshotEstablished is { } gate)
        {
            await gate.PassAsync(cancellationToken);
        }
    }

    public async ValueTask AfterCaptureCommitBeforeActivationAsync(CancellationToken cancellationToken)
    {
        if (AfterCaptureCommitBeforeActivation is { } behavior)
        {
            await behavior(cancellationToken);
        }
    }

    public async ValueTask BeforeActivationAsync(CancellationToken cancellationToken)
    {
        if (BeforeActivation is { } gate)
        {
            await gate.PassAsync(cancellationToken);
        }
    }
}

public sealed class TestApiFailurePoints : IConsolidationApiFailurePoints
{
    public Gate? BetweenOriginAndProjection { get; set; }

    public async ValueTask BetweenOriginAndProjectionAsync(CancellationToken cancellationToken)
    {
        if (BetweenOriginAndProjection is { } gate)
        {
            await gate.PassAsync(cancellationToken);
        }
    }
}

/// <summary>Segura o registro de lançamento antes do commit (ponto de falha já existente da API de lançamentos), até o teste liberar.</summary>
public sealed class GateRegisterFailurePoints(Gate beforeCommit) : IRegisterMovementFailurePoints
{
    public async ValueTask BeforeCommitAsync(CancellationToken cancellationToken) => await beforeCommit.PassAsync(cancellationToken);

    public ValueTask AfterCommitAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
}

public sealed record DailyDto(
    Guid AccountId,
    string Day,
    string BusinessTimeZone,
    string DayStatus,
    string Currency,
    decimal Credits,
    decimal Debits,
    decimal Net,
    long MovementCount,
    decimal ClosingBalance,
    long? MaxAppliedSequence,
    DateTimeOffset? ObservedAt,
    long? UnresolvedEvents,
    long? FailedEvents,
    bool? Stale,
    string Freshness);

public sealed record CustomerDailyAccountDto(Guid AccountId, decimal Credits, decimal Debits, decimal Net, long MovementCount, decimal ClosingBalance, long? MaxAppliedSequence);

public sealed record CustomerDailyExcludedDto(Guid AccountId, DateTimeOffset OpenedAt);

public sealed record CustomerDailyDto(
    Guid CustomerId,
    string Day,
    string BusinessTimeZone,
    string DayStatus,
    string Currency,
    decimal Credits,
    decimal Debits,
    decimal Net,
    long MovementCount,
    decimal ClosingBalance,
    long? MaxAppliedSequence,
    DateTimeOffset? ObservedAt,
    long? UnresolvedEvents,
    long? FailedEvents,
    bool? Stale,
    string Freshness,
    IReadOnlyList<CustomerDailyAccountDto> Accounts,
    IReadOnlyList<CustomerDailyExcludedDto> ExcludedAccounts);
