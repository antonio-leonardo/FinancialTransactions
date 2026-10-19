using System.Net;
using FinancialTransactions.Consolidation;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace FinancialTransactions.IntegrationTests;

/// <summary>
/// C6 a C8 e C12: atomicidade da projeção, reentrega sem nova soma, informação tardia e classes de falha. Cada falha é injetada num ponto
/// que só existe no host de teste; o estado intermediário é observado por SQL com o ponto retido, não depois do fato.
/// </summary>
public sealed class ConsolidationRecoveryTests(ConsolidationFixture fixture) : ConsolidationTest(fixture)
{
    private async Task<(long Processed, decimal Credits, long Count)> A1StateAsync()
    {
        var processed = await Rig.ProjectionScalarAsync<long>("SELECT COUNT(*) FROM processed_events WHERE account_id = @a", ("a", A1));
        var credits = await Rig.ProjectionScalarAsync<decimal>("SELECT COALESCE(SUM(credits), 0) FROM daily_account_consolidation WHERE account_id = @a", ("a", A1));
        var count = await Rig.ProjectionScalarAsync<long>("SELECT COALESCE(SUM(movement_count), 0)::bigint FROM daily_account_consolidation WHERE account_id = @a", ("a", A1));
        return (processed, credits, count);
    }

    private ConsolidationDbContext NewProjectionContext() =>
        new(new DbContextOptionsBuilder<ConsolidationDbContext>().UseNpgsql(Rig.ProjectionConnection).Options);

    [Fact(DisplayName = "C6a_FailureInsideProjectionTransaction_NothingPersisted_NextAttemptAppliesExactlyOnce")]
    public async Task C6aFailureInsideProjectionTransactionNothingPersistedNextAttemptAppliesExactlyOnce()
    {
        Use(A1);
        var gate = new Gate();
        var points = new TestConsolidationFailurePoints();
        points.InsideProjectionTransactionOnce(async ct =>
        {
            await gate.PassAsync(ct);
            throw new InvalidOperationException("falha injetada");
        });
        var worker = await StartWorkerAsync(new WorkerStart { FailurePoints = points, ConfigureConsumer = o => o.BackoffInitial = TimeSpan.FromSeconds(1) });
        await Rig.PostOkAsync(CustomerA, A1, "Credit", 10.00m, DateTimeOffset.UtcNow.AddMinutes(-1));

        await gate.Reached.WaitAsync(TimeSpan.FromSeconds(20));
        // Transação aberta, marcador já inserido nela, UPSERT ainda não: nada é visível de fora.
        Assert.Equal((0L, 0m, 0L), await A1StateAsync());

        gate.Release();
        await ConsolidationRig.Eventually(() => Task.FromResult(worker.Diagnostics.Failures(FailureClass.Environment).Count >= 1), "falha injetada observada");
        Assert.Equal((0L, 0m, 0L), await A1StateAsync()); // rollback: nem marcador, nem agregado
        Assert.Equal(1, await Rig.OutboxCountAsync(0));
        Assert.Equal(0, await Rig.OriginScalarAsync<int>("SELECT COALESCE(MAX(attempts), 0) FROM consolidation_outbox WHERE account_id = @a", ("a", A1)));

        await ConsolidationRig.Eventually(async () => await Rig.OutboxCountAsync(0) == 0, "tentativa seguinte aplica");
        Assert.Equal((1L, 10.00m, 1L), await A1StateAsync());
    }

    [Fact(DisplayName = "C6b_FailureAfterProjectionCommitBeforeMark_Redelivery_DoesNotSumAgain")]
    public async Task C6bFailureAfterProjectionCommitBeforeMarkRedeliveryDoesNotSumAgain()
    {
        Use(A1);
        var gate = new Gate();
        var points = new TestConsolidationFailurePoints();
        points.AfterProjectionCommitBeforeMarkOnce(async ct =>
        {
            await gate.PassAsync(ct);
            throw new InvalidOperationException("falha injetada");
        });
        var worker = await StartWorkerAsync(new WorkerStart { FailurePoints = points, ConfigureConsumer = o => o.BackoffInitial = TimeSpan.FromSeconds(1) });
        await Rig.PostOkAsync(CustomerA, A1, "Credit", 10.00m, DateTimeOffset.UtcNow.AddMinutes(-1));

        await gate.Reached.WaitAsync(TimeSpan.FromSeconds(20));
        // Projeção confirmada, origem ainda sem a marcação: o evento está "aplicado" e ainda "pendente".
        Assert.Equal((1L, 10.00m, 1L), await A1StateAsync());
        Assert.Equal(1, await Rig.OutboxCountAsync(0));

        gate.Release();
        await ConsolidationRig.Eventually(() => Task.FromResult(worker.Diagnostics.Failures(FailureClass.Environment).Count >= 1), "falha injetada observada");
        await ConsolidationRig.Eventually(async () => await Rig.OutboxCountAsync(0) == 0, "reentrega marca a Outbox");

        Assert.Equal(1, await Rig.OutboxCountAsync(1)); // marcada na passagem seguinte
        Assert.Equal((1L, 10.00m, 1L), await A1StateAsync()); // agregado inalterado: a reentrega não somou de novo
        Assert.Equal(0, await Rig.OriginScalarAsync<int>("SELECT COALESCE(MAX(attempts), 0) FROM consolidation_outbox WHERE account_id = @a", ("a", A1)));
    }

    [Fact(DisplayName = "C7_SameEventPresentedAgainInNewTransaction_IsNoOp")]
    public async Task C7SameEventPresentedAgainInNewTransactionIsNoOp()
    {
        Use(A1);
        var consolidationEvent = new ConsolidationEvent(9001, Guid.NewGuid(), A1, CustomerA, 1, 12.34m, NoonOf(Today), 0);

        await using (var first = NewProjectionContext())
        {
            var outcome = await new DailyConsolidationApplier(first, new NoConsolidationFailurePoints(), TimeProvider.System).ApplyAsync(consolidationEvent, CancellationToken.None);
            Assert.Equal(ApplyOutcome.Applied, outcome);
        }

        await using (var second = NewProjectionContext())
        {
            var outcome = await new DailyConsolidationApplier(second, new NoConsolidationFailurePoints(), TimeProvider.System).ApplyAsync(consolidationEvent, CancellationToken.None);
            Assert.Equal(ApplyOutcome.AlreadyApplied, outcome);
        }

        Assert.Equal((1L, 12.34m, 1L), await A1StateAsync());
    }

    [Fact(DisplayName = "C7b_AggregateOverflow_IsAnEventFailure_AndPersistsNothing")]
    public async Task C7bAggregateOverflowIsAnEventFailureAndPersistsNothing()
    {
        Use(A1);
        var day = Today;
        // Valor acumulado preparado em banco descartável (não por volume): a linha já está no limite de numeric(24,2).
        await Rig.ProjectionExecuteAsync(
            """
            INSERT INTO daily_account_consolidation (account_id, business_day, credits, debits, net, movement_count, max_applied_sequence, updated_at)
            VALUES (@a, @d, 9999999999999999999999.99, 0, 9999999999999999999999.99, 1, 1, now())
            """,
            ("a", A1),
            ("d", day));
        var overflowing = new ConsolidationEvent(9002, Guid.NewGuid(), A1, CustomerA, 1, 0.02m, NoonOf(day), 0);

        await using var db = NewProjectionContext();
        var exception = await Assert.ThrowsAsync<PostgresException>(() =>
            new DailyConsolidationApplier(db, new NoConsolidationFailurePoints(), TimeProvider.System).ApplyAsync(overflowing, CancellationToken.None));

        Assert.Equal(PostgresErrorCodes.NumericValueOutOfRange, exception.SqlState);
        Assert.Equal(FailureClass.Event, FailureClassifier.Classify(exception, CancellationToken.None));
        Assert.Equal(0, await Rig.ProjectionScalarAsync<long>("SELECT COUNT(*) FROM processed_events WHERE account_id = @a", ("a", A1)));
        Assert.Equal(9999999999999999999999.99m, await Rig.ProjectionScalarAsync<decimal>("SELECT credits FROM daily_account_consolidation WHERE account_id = @a", ("a", A1)));
    }

    [Fact(DisplayName = "C8_LateInformation_ChangesTheEventDayRow_AndDerivedClosingBalances_NotTheLaterRows")]
    public async Task C8LateInformationChangesTheEventDayRowAndDerivedClosingBalancesNotTheLaterRows()
    {
        Use(A1);
        var today = Today;
        await StartWorkerAsync();
        var api = await StartApiAsync();
        await Rig.PostOkAsync(CustomerA, A1, "Credit", 100.00m, DateTimeOffset.UtcNow.AddMinutes(-1)); // hoje
        await Rig.PostOkAsync(CustomerA, A1, "Credit", 20.00m, NoonOf(today.AddDays(-2)));            // D-2
        await ConsolidationRig.Eventually(async () => await Rig.OutboxCountAsync(0) == 0, "dois eventos aplicados");

        var before = new Dictionary<int, DailyDto>();
        foreach (var back in new[] { 3, 2, 1, 0 })
        {
            before[back] = await ReadAccountAsync(api, CustomerA, A1, today.AddDays(-back));
        }

        Assert.Equal((0m, 0m), (before[3].Credits, before[3].ClosingBalance));
        Assert.Equal((20.00m, 20.00m), (before[2].Credits, before[2].ClosingBalance));
        Assert.Equal((0m, 20.00m), (before[1].Credits, before[1].ClosingBalance));
        Assert.Equal((100.00m, 120.00m), (before[0].Credits, before[0].ClosingBalance));

        // Informação tardia: efetiva há três dias, registrada agora.
        await Rig.PostOkAsync(CustomerA, A1, "Credit", 5.00m, NoonOf(today.AddDays(-3)));
        await ConsolidationRig.Eventually(async () => await Rig.OutboxCountAsync(0) == 0, "evento tardio aplicado");

        var after = new Dictionary<int, DailyDto>();
        foreach (var back in new[] { 3, 2, 1, 0 })
        {
            after[back] = await ReadAccountAsync(api, CustomerA, A1, today.AddDays(-back));
        }

        // A linha de D-3 mudou.
        Assert.Equal((5.00m, 5.00m, 1L), (after[3].Credits, after[3].Net, after[3].MovementCount));
        // As linhas dos dias seguintes (valores do dia) não mudaram.
        foreach (var back in new[] { 2, 1, 0 })
        {
            Assert.Equal((before[back].Credits, before[back].Debits, before[back].Net, before[back].MovementCount), (after[back].Credits, after[back].Debits, after[back].Net, after[back].MovementCount));
        }

        // O fechamento acumulado mudou em todos eles, por derivação.
        Assert.Equal(5.00m, after[3].ClosingBalance);
        Assert.Equal(25.00m, after[2].ClosingBalance);
        Assert.Equal(25.00m, after[1].ClosingBalance);
        Assert.Equal(125.00m, after[0].ClosingBalance);
        Assert.Equal("closed", after[3].DayStatus);
        Assert.Equal("open", after[0].DayStatus);
    }

    [Fact(DisplayName = "C12a_InvalidEventIsQuarantinedAfterFiveAttempts_ValidOneAppliedAndWorkerAlive")]
    public async Task C12aInvalidEventIsQuarantinedAfterFiveAttemptsValidOneAppliedAndWorkerAlive()
    {
        Use(A1);
        var worker = await StartWorkerAsync();
        var invalid = Guid.NewGuid();
        await Rig.OriginExecuteAsync(
            """
            INSERT INTO consolidation_outbox (event_id, account_id, customer_id, type, amount, effective_at, created_at, origin, status, attempts)
            VALUES (@id, @account, @customer, 9, 1.00, now(), now(), 0, 0, 0)
            """,
            ("id", invalid),
            ("account", A1),
            ("customer", CustomerA));
        var at = DateTimeOffset.UtcNow.AddMinutes(-1);
        var valid = await Rig.PostOkAsync(CustomerA, A1, "Credit", 10.00m, at);

        await ConsolidationRig.Eventually(async () =>
            await Rig.OriginScalarAsync<short>("SELECT status FROM consolidation_outbox WHERE event_id = @id", ("id", invalid)) == 2, "evento inválido em quarentena");
        await ConsolidationRig.Eventually(async () =>
            await Rig.OriginScalarAsync<short>("SELECT status FROM consolidation_outbox WHERE event_id = @id", ("id", valid)) == 1, "evento válido aplicado");

        Assert.Equal(5, await Rig.OriginScalarAsync<int>("SELECT attempts FROM consolidation_outbox WHERE event_id = @id", ("id", invalid)));
        var lastError = await Rig.OriginScalarAsync<string>("SELECT last_error FROM consolidation_outbox WHERE event_id = @id", ("id", invalid));
        Assert.NotNull(lastError);
        Assert.StartsWith("InvalidEventException: ", lastError, StringComparison.Ordinal);
        Assert.DoesNotContain("Host=", lastError, StringComparison.Ordinal);
        Assert.DoesNotContain("Password", lastError, StringComparison.Ordinal);
        Assert.DoesNotContain(" at ", lastError, StringComparison.Ordinal); // sem stack trace
        Assert.Equal(1, worker.Diagnostics.Count("EventQuarantined"));
        Assert.False(worker.LoopCompleted);
        Assert.Equal(HttpStatusCode.OK, (await worker.GetAsync("/health/live")).Status);

        var api = await StartApiAsync();
        var daily = await ReadAccountAsync(api, CustomerA, A1, BusinessDay.Of(at));
        Assert.Equal(10.00m, daily.Credits);
        Assert.Equal(1, daily.FailedEvents);
        Assert.Equal(1, daily.UnresolvedEvents);
        Assert.Equal("stale", daily.Freshness); // nunca complete com quarentena no recorte
    }

    [Fact(DisplayName = "C12b_EnvironmentFailure_NoEventQuarantined_ReadyDegraded503_ThenDisarmedAfterFirstSuccessfulCycle")]
    public async Task C12bEnvironmentFailureNoEventQuarantinedReadyDegraded503ThenDisarmed()
    {
        Use(A1);
        var emptyDatabase = "ft_cons_test_" + TestAdmin.Hex();
        await TestAdmin.CreateDatabaseAsync(TestAdmin.Consolidation, emptyDatabase);
        try
        {
            // Banco existente, sem o esquema da projeção: 42P01 em todo evento (falha de ambiente, não do evento).
            var emptySchema = new NpgsqlConnectionStringBuilder(TestAdmin.Consolidation) { Database = emptyDatabase }.ConnectionString;
            var connections = new TestConnectionFactory(Rig.Real());
            connections.RedirectProjection(ConsolidationConnectionRole.Consumer, emptySchema);
            var worker = await StartWorkerAsync(new WorkerStart { Factory = connections });
            await Rig.PostOkAsync(CustomerA, A1, "Credit", 10.00m, DateTimeOffset.UtcNow.AddMinutes(-1));

            await ConsolidationRig.Eventually(() => Task.FromResult(worker.Diagnostics.Failures(FailureClass.Environment).Count >= 2), "falhas de ambiente repetidas");
            Assert.Equal(0, await Rig.OutboxCountAsync(2));
            Assert.Equal(1, await Rig.OutboxCountAsync(0));
            Assert.Equal(0, await Rig.OriginScalarAsync<int>("SELECT COALESCE(MAX(attempts), 0) FROM consolidation_outbox WHERE account_id = @a", ("a", A1)));
            Assert.Empty(worker.Diagnostics.Failures(FailureClass.Event));

            await ConsolidationRig.Eventually(async () =>
            {
                var (status, body) = await worker.GetAsync("/health/ready");
                return status == HttpStatusCode.ServiceUnavailable && body == "{\"status\":\"Degraded\"}";
            }, "ready 503 Degraded");

            connections.Restore(ConsolidationConnectionRole.Consumer);

            await ConsolidationRig.Eventually(async () => await Rig.OutboxCountAsync(0) == 0, "convergência");
            await ConsolidationRig.Eventually(async () =>
            {
                var (status, body) = await worker.GetAsync("/health/ready");
                return status == HttpStatusCode.OK && body == "{\"status\":\"Healthy\"}";
            }, "ready 200 Healthy depois do desarme");
        }
        finally
        {
            await TestAdmin.DropDatabaseAsync(TestAdmin.Consolidation, emptyDatabase);
        }
    }
}
