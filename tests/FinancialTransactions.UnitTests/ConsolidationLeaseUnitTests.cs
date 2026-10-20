using FinancialTransactions.Consolidation;

namespace FinancialTransactions.UnitTests;

/// <summary>Parâmetros da reserva, verificação do descritor do bootstrap, códigos administrativos e contadores por instância.</summary>
public sealed class ConsolidationLeaseUnitTests
{
    private static readonly Guid Id = Guid.Parse("00000000-0000-0000-0000-00000000000a");
    private static readonly Guid OtherId = Guid.Parse("00000000-0000-0000-0000-00000000000b");
    private static readonly Guid Account = Guid.Parse("aaaaaaa1-0000-0000-0000-000000000001");
    private static readonly DateOnly Day = new(2026, 9, 1);

    // --- defaults e validação do consumidor ---

    [Fact(DisplayName = "LeaseDefaults_Are45SecondsLeaseAnd30SecondsAttempt_AndValid")]
    public void LeaseDefaultsAre45SecondsLeaseAnd30SecondsAttemptAndValid()
    {
        var options = new ConsolidationConsumerOptions();

        Assert.Equal(TimeSpan.FromSeconds(45), options.LeaseDuration);
        Assert.Equal(TimeSpan.FromSeconds(30), options.AttemptBudget);
        Assert.True(new ConsolidationConsumerOptionsValidator().Validate(null, options).Succeeded);
    }

    [Theory(DisplayName = "ConsumerOptionsValidator_RejectsLeaseNotAboveAttemptAndInvalidValues")]
    [InlineData(30, 30, 100, 5, 1, "LeaseDuration")]
    [InlineData(20, 30, 100, 5, 1, "LeaseDuration")]
    [InlineData(45, 0, 100, 5, 1, "AttemptBudget")]
    [InlineData(45, 30, 0, 5, 1, "MaxEventsPerCycle")]
    [InlineData(45, 30, 100, 0, 1, "MaxEventAttempts")]
    [InlineData(45, 30, 100, 5, -1, "intervalos")]
    public void ConsumerOptionsValidatorRejectsLeaseNotAboveAttemptAndInvalidValues(int leaseSeconds, int attemptSeconds, int perCycle, int maxAttempts, int retrySeconds, string expectedFragment)
    {
        var options = new ConsolidationConsumerOptions
        {
            LeaseDuration = TimeSpan.FromSeconds(leaseSeconds),
            AttemptBudget = TimeSpan.FromSeconds(attemptSeconds),
            MaxEventsPerCycle = perCycle,
            MaxEventAttempts = maxAttempts,
            RetryDelay = TimeSpan.FromSeconds(retrySeconds),
        };

        var result = new ConsolidationConsumerOptionsValidator().Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, f => f.Contains(expectedFragment, StringComparison.Ordinal));
    }

    // --- descritor do bootstrap: origem, destino e conjunto identificado, inclusive vazio ---

    private static CaptureRow Capture(long count, Guid? id = null) => new(id ?? Id, count, DateTimeOffset.UnixEpoch, "1:1:");

    private static BootstrapStateRow State(long count, Guid? id = null) => new(id ?? Id, "loading", count, DateTimeOffset.UnixEpoch, null, null);

    private static ReconciliationResult? Verify(
        CaptureRow? capture, BootstrapStateRow? state, int ids, long outboxCount, long expectedMovements, long appliedMovements, int processed, Guid? requested = null) =>
        ReconciliationComparer.VerifyDescriptor(
            requested ?? Id,
            capture,
            state,
            Enumerable.Range(0, ids).Select(_ => Guid.NewGuid()).ToList(),
            outboxCount,
            expectedMovements == 0 ? [] : [new DayAggregate(Account, Day, expectedMovements, 1m, 0m)],
            appliedMovements == 0 ? [] : [new DayAggregate(Account, Day, appliedMovements, 1m, 0m)],
            Enumerable.Range(0, processed).Select(_ => Guid.NewGuid()).ToList());

    [Fact(DisplayName = "VerifyDescriptor_ConsistentSet_ReturnsNull_IncludingTheEmptySet")]
    public void VerifyDescriptorConsistentSetReturnsNullIncludingTheEmptySet()
    {
        Assert.Null(Verify(Capture(12), State(12), 12, 12, 12, 12, 12));
        Assert.Null(Verify(Capture(0), State(0), 0, 0, 0, 0, 0));
    }

    [Theory(DisplayName = "VerifyDescriptor_CountSmallerOrLargerThanTheSet_OrEmptyTampered_Differs")]
    [InlineData(11L, 11L, 12, 12, 12, 12, 12, "declara 11 membros")] // contagem menor nos dois lados
    [InlineData(13L, 13L, 12, 12, 12, 12, 12, "declara 13 membros")] // contagem maior nos dois lados
    [InlineData(1L, 1L, 0, 0, 0, 0, 0, "declara 1 membros")] // conjunto vazio adulterado
    [InlineData(12L, 11L, 12, 12, 12, 12, 12, "member_count diverge")] // origem e destino discordam
    [InlineData(12L, 12L, 12, 13, 12, 12, 12, "a Outbox marca 13")] // linhas marcadas na Outbox além do descritor
    [InlineData(12L, 12L, 12, 12, 11, 12, 12, "soma de movement_count esperada")]
    [InlineData(12L, 12L, 12, 12, 12, 12, 11, "soma de movement_count na projeção")]
    public void VerifyDescriptorCountSmallerOrLargerThanTheSetOrEmptyTamperedDiffers(
        long captureCount, long stateCount, int ids, long outboxCount, long expectedMovements, long appliedMovements, int processed, string fragment)
    {
        var result = Verify(Capture(captureCount), State(stateCount), ids, outboxCount, expectedMovements, appliedMovements, processed);

        Assert.NotNull(result);
        Assert.False(result.Equal);
        Assert.Contains(fragment, result.FirstDeviation, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "VerifyDescriptor_MissingControlRowsOrDifferentBootstrapId_Differs")]
    public void VerifyDescriptorMissingControlRowsOrDifferentBootstrapIdDiffers()
    {
        Assert.False(Verify(null, State(1), 1, 1, 1, 1, 1)!.Equal);
        Assert.False(Verify(Capture(1), null, 1, 1, 1, 1, 1)!.Equal);
        Assert.False(Verify(Capture(1, OtherId), State(1, OtherId), 1, 1, 1, 1, 1)!.Equal); // não é o bootstrap pedido
        Assert.False(Verify(Capture(1), State(1, OtherId), 1, 1, 1, 1, 1)!.Equal);
    }

    // --- administração ---

    [Fact(DisplayName = "AdminExitCodes_AreDistinctAndDocumented")]
    public void AdminExitCodesAreDistinctAndDocumented()
    {
        Assert.Equal(0, AdminExitCodes.Success);
        Assert.Equal(2, AdminExitCodes.Unreachable);
        Assert.Equal(3, AdminExitCodes.Refused);
        Assert.Equal(4, AdminExitCodes.Conflict);
        Assert.Equal(5, AdminExitCodes.CommitUnknown);
    }

    [Fact(DisplayName = "RequeueReasons_ClosedList")]
    public void RequeueReasonsClosedList()
    {
        Assert.Equal(3, RequeueReasons.All.Count);
        Assert.Contains("cause_corrected", RequeueReasons.All);
        Assert.DoesNotContain("qualquer", RequeueReasons.All);
    }

    // --- instância e contadores ---

    [Fact(DisplayName = "InstanceId_IsOpaque_AndDifferentPerInstance")]
    public void InstanceIdIsOpaqueAndDifferentPerInstance()
    {
        var first = new ConsolidationInstance();
        var second = new ConsolidationInstance();

        Assert.NotEqual(first.Id, second.Id);
        Assert.Matches("^w-[0-9a-f]{12}$", first.Id);
        Assert.DoesNotContain(Environment.MachineName, first.Id, StringComparison.OrdinalIgnoreCase);
    }

    [Fact(DisplayName = "WorkerCounters_KeepDeliveriesAndDuplicatesApart")]
    public void WorkerCountersKeepDeliveriesAndDuplicatesApart()
    {
        var counters = new ConsolidationWorkerCounters();

        counters.Acquired();
        counters.Acquired();
        counters.Applied();
        counters.DuplicateAvoided();
        counters.LeaseLost();
        counters.Failed(FailureClass.Transient);
        counters.Failed(FailureClass.Environment);
        counters.Failed(FailureClass.HostCancellation); // encerramento não é falha contada
        var snapshot = counters.Snapshot();

        Assert.Equal(2, snapshot.Acquisitions);
        Assert.Equal(1, snapshot.AppliedNew);
        Assert.Equal(1, snapshot.DuplicatesAvoided);
        Assert.Equal(1, snapshot.LeaseLosses);
        Assert.Equal(1, snapshot.TransientFailures);
        Assert.Equal(1, snapshot.EnvironmentFailures);
    }
}
