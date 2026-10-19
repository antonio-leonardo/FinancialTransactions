using FinancialTransactions.Consolidation;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace FinancialTransactions.UnitTests;

/// <summary>Decisão do passo 2 do coordenador, comparação da reconciliação, saúde, backoff, capacidade e validação de configuração.</summary>
public sealed class ConsolidationBootstrapUnitTests
{
    private static readonly Guid IdA = Guid.Parse("00000000-0000-0000-0000-00000000000a");
    private static readonly Guid IdB = Guid.Parse("00000000-0000-0000-0000-00000000000b");
    private static readonly Guid Account1 = Guid.Parse("aaaaaaa1-0000-0000-0000-000000000001");
    private static readonly Guid Account2 = Guid.Parse("aaaaaaa2-0000-0000-0000-000000000002");

    // --- decisão do coordenador: todas as combinações de capture × destino ---

    [Theory(DisplayName = "CoordinatorDecision_AllCaptureByDestinationCombinations")]
    [InlineData(false, 0L, false, 0L, false, CoordinatorDecision.Capture)]
    [InlineData(true, 12L, false, 0L, false, CoordinatorDecision.Activate)]
    [InlineData(true, 0L, false, 0L, false, CoordinatorDecision.Activate)]
    [InlineData(false, 0L, true, 12L, false, CoordinatorDecision.Diverged)]
    [InlineData(true, 12L, true, 12L, true, CoordinatorDecision.Done)]
    [InlineData(true, 0L, true, 0L, true, CoordinatorDecision.Done)]
    [InlineData(true, 12L, true, 13L, false, CoordinatorDecision.Diverged)]
    [InlineData(true, 12L, true, 12L, false, CoordinatorDecision.Diverged)]
    public void CoordinatorDecisionAllCombinations(bool hasCapture, long captureCount, bool hasDestination, long destinationCount, bool sameId, CoordinatorDecision expected)
    {
        var capture = hasCapture ? new BootstrapDescriptor(IdA, captureCount) : null;
        var destination = hasDestination ? new BootstrapDescriptor(sameId ? IdA : IdB, destinationCount) : null;

        Assert.Equal(expected, CoordinatorDecisionTable.Decide(capture, destination));
    }

    [Fact(DisplayName = "CoordinatorExitCodes_AreTheDocumentedOnes")]
    public void CoordinatorExitCodesAreTheDocumentedOnes()
    {
        Assert.Equal(0, CoordinatorExitCodes.Success);
        Assert.Equal(2, CoordinatorExitCodes.Unreachable);
        Assert.Equal(3, CoordinatorExitCodes.LockBusy);
        Assert.Equal(4, CoordinatorExitCodes.Diverged);
        Assert.Equal(5, CoordinatorExitCodes.CommitUnknown);
    }

    // --- reconciliação ---

    private static DayAggregate Day(Guid account, int day, long count, decimal credits, decimal debits) =>
        new(account, new DateOnly(2026, 9, day), count, credits, debits);

    [Fact(DisplayName = "Reconcile_EqualSets_IsEqual_EvenWhenListedInDifferentOrder")]
    public void ReconcileEqualSetsIsEqual()
    {
        var expected = new[] { Day(Account1, 1, 2, 30m, 10m), Day(Account2, 2, 1, 5m, 0m) };
        var actual = new[] { Day(Account2, 2, 1, 5m, 0m), Day(Account1, 1, 2, 30m, 10m) };

        var result = ReconciliationComparer.Compare(expected, actual, [IdA, IdB], [IdB, IdA]);

        Assert.True(result.Equal);
        Assert.Null(result.FirstDeviation);
    }

    [Fact(DisplayName = "Reconcile_EmptyEqualsEmpty")]
    public void ReconcileEmptyEqualsEmpty()
    {
        Assert.True(ReconciliationComparer.Compare([], [], [], []).Equal);
    }

    [Fact(DisplayName = "Reconcile_DifferentCountCreditsDebitsOrNet_IsDivergent")]
    public void ReconcileDifferentAmountsIsDivergent()
    {
        var expected = new[] { Day(Account1, 1, 2, 30m, 10m) };

        Assert.False(ReconciliationComparer.Compare(expected, [Day(Account1, 1, 3, 30m, 10m)], [IdA], [IdA]).Equal);
        Assert.False(ReconciliationComparer.Compare(expected, [Day(Account1, 1, 2, 31m, 10m)], [IdA], [IdA]).Equal);
        Assert.False(ReconciliationComparer.Compare(expected, [Day(Account1, 1, 2, 30m, 11m)], [IdA], [IdA]).Equal);
        // Totais iguais, composição diferente (crédito 20/débito 0 em vez de 30/10): o total sozinho não prova nada.
        Assert.False(ReconciliationComparer.Compare([Day(Account1, 1, 2, 30m, 10m)], [Day(Account1, 1, 2, 20m, 0m)], [IdA], [IdA]).Equal);
    }

    [Fact(DisplayName = "Reconcile_MissingOrExtraDayRow_IsDivergent_AndReportsTheFirstDeviation")]
    public void ReconcileMissingOrExtraDayRowIsDivergent()
    {
        var one = new[] { Day(Account1, 1, 2, 30m, 10m) };
        var two = new[] { Day(Account1, 1, 2, 30m, 10m), Day(Account1, 2, 1, 1m, 0m) };

        var missing = ReconciliationComparer.Compare(two, one, [IdA], [IdA]);
        var extra = ReconciliationComparer.Compare(one, two, [IdA], [IdA]);

        Assert.False(missing.Equal);
        Assert.Contains("ausente", missing.FirstDeviation, StringComparison.Ordinal);
        Assert.False(extra.Equal);
    }

    [Fact(DisplayName = "Reconcile_SameTotalsButDifferentIdentities_IsDivergent")]
    public void ReconcileSameTotalsButDifferentIdentitiesIsDivergent()
    {
        var aggregates = new[] { Day(Account1, 1, 1, 10m, 0m) };

        var result = ReconciliationComparer.Compare(aggregates, aggregates, [IdA], [IdB]);

        Assert.False(result.Equal);
        Assert.Contains("ids", result.FirstDeviation, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "Reconcile_ProcessedEventOutsideTheSet_IsDivergent")]
    public void ReconcileProcessedEventOutsideTheSetIsDivergent()
    {
        var aggregates = new[] { Day(Account1, 1, 1, 10m, 0m) };

        Assert.False(ReconciliationComparer.Compare(aggregates, aggregates, [IdA], [IdA, IdB]).Equal);
        Assert.False(ReconciliationComparer.Compare(aggregates, aggregates, [IdA, IdB], [IdA]).Equal);
    }

    // --- saúde ---

    [Theory(DisplayName = "HealthMapping_DegradedIs503_NotTheAspNetDefault200")]
    [InlineData(HealthStatus.Healthy, 200, "{\"status\":\"Healthy\"}")]
    [InlineData(HealthStatus.Degraded, 503, "{\"status\":\"Degraded\"}")]
    [InlineData(HealthStatus.Unhealthy, 503, "{\"status\":\"Unhealthy\"}")]
    public void HealthMappingDegradedIs503(HealthStatus status, int code, string body)
    {
        Assert.Equal(code, ConsolidationHealthMapping.StatusCodeFor(status));
        Assert.Equal(body, ConsolidationHealthMapping.BodyFor(status));
    }

    [Fact(DisplayName = "HealthMapping_ReadyOptions_MapEveryStatusExplicitly")]
    public void HealthMappingReadyOptionsMapEveryStatusExplicitly()
    {
        var options = ConsolidationHealthMapping.ReadyOptions("ready");

        Assert.Equal(StatusCodes.Status200OK, options.ResultStatusCodes[HealthStatus.Healthy]);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, options.ResultStatusCodes[HealthStatus.Degraded]);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, options.ResultStatusCodes[HealthStatus.Unhealthy]);
    }

    [Fact(DisplayName = "HealthState_EnvironmentFailure_IsDisarmedByTheFirstCompletedCycle_AndOnlyThat")]
    public void HealthStateEnvironmentFailureIsDisarmedByTheFirstCompletedCycle()
    {
        var state = new ConsolidationHealthState();
        Assert.False(state.IsDegraded);

        state.Set(DegradedReason.EnvironmentFailure);
        state.Set(DegradedReason.ReconciliationFailed);
        Assert.True(state.IsDegraded);

        state.OnCycleCompleted();

        Assert.False(state.Has(DegradedReason.EnvironmentFailure));
        Assert.True(state.Has(DegradedReason.ReconciliationFailed));
        Assert.True(state.IsDegraded);

        state.Clear(DegradedReason.ReconciliationFailed);
        Assert.False(state.IsDegraded);
    }

    [Fact(DisplayName = "HealthState_EachReasonIsIndependent")]
    public void HealthStateEachReasonIsIndependent()
    {
        var state = new ConsolidationHealthState();
        foreach (var reason in Enum.GetValues<DegradedReason>())
        {
            state.Set(reason);
        }

        state.Clear(DegradedReason.WaitingForBootstrap);

        Assert.False(state.Has(DegradedReason.WaitingForBootstrap));
        Assert.True(state.Has(DegradedReason.ControlMismatch));
        Assert.True(state.Has(DegradedReason.ProjectionIncoherent));
        Assert.True(state.Has(DegradedReason.ReconciliationFailed));
        Assert.True(state.Has(DegradedReason.EnvironmentFailure));
    }

    [Fact(DisplayName = "WorkerReadiness_UnreachableDatabases_IsUnhealthy_EvenWhenState_IsHealthy")]
    public async Task WorkerReadinessUnreachableDatabasesIsUnhealthy()
    {
        var factory = new FixedFactory(new ConsolidationConnectionStrings(
            "Host=127.0.0.1;Port=1;Database=x;Username=u;Password=p;Timeout=1",
            "Host=127.0.0.1;Port=1;Database=y;Username=u;Password=p;Timeout=1"));
        var check = new WorkerReadinessCheck(factory, new ConsolidationHealthState());

        var result = await check.CheckHealthAsync(new HealthCheckContext(), CancellationToken.None);

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
    }

    private sealed class FixedFactory(ConsolidationConnectionStrings strings) : IConsolidationConnectionFactory
    {
        public ConsolidationConnectionStrings Open(ConsolidationConnectionRole role) => strings;
    }

    // --- backoff ---

    [Fact(DisplayName = "Backoff_Production1sTo30sCap_ThenResets")]
    public void BackoffProductionOneToThirtySecondsCapThenResets()
    {
        var options = new ConsolidationConsumerOptions();
        var backoff = new Backoff(options.BackoffInitial, options.BackoffMax);

        var sequence = Enumerable.Range(0, 8).Select(_ => backoff.Next().TotalSeconds).ToArray();

        Assert.Equal([1d, 2d, 4d, 8d, 16d, 30d, 30d, 30d], sequence);
        Assert.Equal(TimeSpan.FromMilliseconds(500), options.IdleDelay);

        backoff.Reset();
        Assert.Equal(1d, backoff.Next().TotalSeconds);
    }

    // --- capacidade dos agregados ---

    [Fact(DisplayName = "Capacity_OneRowHoldsTenToThe13MinusOneMovementsAtTheCeiling_NotTenToThe13")]
    public void CapacityOneRowHoldsTenToThe13MinusOneMovementsAtTheCeiling()
    {
        const decimal Ceiling = 1_000_000_000m; // H3
        const decimal ColumnLimit = 9_999_999_999_999_999_999_999.99m; // 10^22 - 0,01: maior valor positivo de numeric(24,2)
        const decimal Fits = 9_999_999_999_999m;   // 10^13 - 1
        const decimal Exceeds = 10_000_000_000_000m; // 10^13

        Assert.True(Fits * Ceiling <= ColumnLimit);
        Assert.True(Exceeds * Ceiling > ColumnLimit);
    }

    [Fact(DisplayName = "Capacity_ClosingTotalsAreSummedInDecimal_AndOverflowIsObservable_NeverRounded")]
    public void CapacityClosingTotalsAreSummedInDecimalAndOverflowIsObservable()
    {
        var rowAtTheLimit = 9_999_999_999_999_999_999_999.99m;
        var thousandRows = Enumerable.Repeat(rowAtTheLimit, 1000);

        // 1000 linhas no limite (10^25) cabem em decimal (28-29 dígitos) sem perder os centavos.
        Assert.Equal((rowAtTheLimit * 1000m), thousandRows.Sum());

        // Acima de decimal.MaxValue a soma lança: nunca arredonda nem vira double.
        var huge = Enumerable.Repeat(decimal.MaxValue / 2m, 3);
        Assert.Throws<OverflowException>(() => huge.Sum());
    }

    // --- configuração validada na inicialização ---

    [Fact(DisplayName = "ConnectionOptionsValidator_RejectsMissingEqualOrMalformedStrings_AndAcceptsDistinctValidOnes")]
    public void ConnectionOptionsValidatorBehavior()
    {
        var validator = new ConsolidationConnectionOptionsValidator();
        const string Origin = "Host=127.0.0.1;Port=15432;Database=a;Username=u;Password=p";
        const string Projection = "Host=127.0.0.1;Port=15433;Database=b;Username=u;Password=p";

        Assert.True(validator.Validate(null, new ConsolidationConnectionOptions { Origin = Origin, Projection = Projection }).Succeeded);

        var missing = validator.Validate(null, new ConsolidationConnectionOptions { Origin = string.Empty, Projection = Projection });
        Assert.True(missing.Failed);
        Assert.Contains(missing.Failures!, f => f.Contains("ConnectionStrings:FinancialTransactions", StringComparison.Ordinal));

        Assert.True(validator.Validate(null, new ConsolidationConnectionOptions { Origin = Origin, Projection = Origin }).Failed);

        var malformed = validator.Validate(null, new ConsolidationConnectionOptions { Origin = Origin, Projection = "isto não é uma connection string" });
        Assert.True(malformed.Failed);
        Assert.DoesNotContain(malformed.Failures!, f => f.Contains("isto não", StringComparison.Ordinal));

        Assert.True(validator.Validate(null, new ConsolidationConnectionOptions { Origin = Origin, Projection = Projection, ConnectTimeoutSeconds = 0 }).Failed);
    }

    [Fact(DisplayName = "ConfigurationFactory_AppliesTheProcessTimeouts_ToBothStrings")]
    public void ConfigurationFactoryAppliesTheProcessTimeoutsToBothStrings()
    {
        var options = Options.Create(new ConsolidationConnectionOptions
        {
            Origin = "Host=127.0.0.1;Port=15432;Database=a;Username=u;Password=p;Timeout=30;Command Timeout=60",
            Projection = "Host=127.0.0.1;Port=15433;Database=b;Username=u;Password=p",
            ConnectTimeoutSeconds = 1,
            CommandTimeoutSeconds = 1,
        });

        var strings = new ConfigurationConsolidationConnectionFactory(options).Open(ConsolidationConnectionRole.Query);

        Assert.Contains("Timeout=1", strings.Origin, StringComparison.Ordinal);
        Assert.Contains("Command Timeout=1", strings.Origin, StringComparison.Ordinal);
        Assert.Contains("Timeout=1", strings.Projection, StringComparison.Ordinal);
        Assert.Contains("Command Timeout=1", strings.Projection, StringComparison.Ordinal);
    }
}
