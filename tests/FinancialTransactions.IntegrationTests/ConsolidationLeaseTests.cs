using System.Diagnostics;
using FinancialTransactions.Consolidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit.Abstractions;

namespace FinancialTransactions.IntegrationTests;

/// <summary>Apoio dos cenários R: eventos inseridos direto na Outbox (a fonte é exercitada sem a API) e leitura do estado da reserva.</summary>
internal static class LeaseSupport
{
    public static readonly Guid A1 = ConsolidationRig.AccountA1;
    public static readonly Guid CustomerA = ConsolidationRig.CustomerA;

    public static async Task<Guid> InsertEventAsync(
        ConsolidationRig rig,
        short type = 1,
        decimal amount = 1.00m,
        short status = 0,
        int attempts = 0,
        string? lastError = null,
        Guid? account = null,
        DateTimeOffset? effectiveAt = null)
    {
        var id = Guid.CreateVersion7();
        await rig.OriginExecuteAsync(
            """
            INSERT INTO consolidation_outbox (event_id, account_id, customer_id, type, amount, effective_at, created_at, origin, status, attempts, last_error)
            VALUES (@id, @account, @customer, @type, @amount, @effective, now(), 0, @status, @attempts, @error)
            """,
            ("id", id),
            ("account", account ?? A1),
            ("customer", CustomerA),
            ("type", type),
            ("amount", amount),
            ("effective", effectiveAt ?? DateTimeOffset.UtcNow.AddMinutes(-1)),
            ("status", status),
            ("attempts", attempts),
            ("error", lastError));
        return id;
    }

    public static OutboxConsolidationSource NewSource(ConsolidationRig rig) => new(new TestConnectionFactory(rig.Real()));

    public static async Task<(short Status, int Attempts, string? Owner, Guid? Token, DateTimeOffset? Expires)> LeaseStateAsync(ConsolidationRig rig, Guid eventId)
    {
        await using var connection = new NpgsqlConnection(rig.OriginConnection);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT status, attempts, lease_owner, lease_token, lease_expires_at FROM consolidation_outbox WHERE event_id = @id", connection);
        command.Parameters.AddWithValue("id", eventId);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync(), "evento ausente");
        return (
            reader.GetInt16(0),
            reader.GetInt32(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetGuid(3),
            reader.IsDBNull(4) ? null : reader.GetFieldValue<DateTimeOffset>(4));
    }

    /// <summary>Intervenção declarada, só em banco descartável: antecipa o vencimento da reserva no relógio da origem (o relógio do servidor não é alterado).</summary>
    public static Task ExpireLeaseNowAsync(ConsolidationRig rig, Guid eventId) =>
        rig.OriginExecuteAsync(
            "UPDATE consolidation_outbox SET lease_expires_at = statement_timestamp() - interval '1 second' WHERE event_id = @id",
            ("id", eventId));

    public static ConsolidationWorkerCounters CountersOf(WorkerHandle worker) => worker.App.Services.GetRequiredService<ConsolidationWorkerCounters>();
}

/// <summary>
/// R01 a R05: reserva durável na origem, expiração e token, duas instâncias sobre a mesma Outbox. Cada cenário usa um par de bancos próprio.
/// Os esperados são literais ou somas calculadas aqui a partir do que o próprio teste criou.
/// </summary>
public sealed class ConsolidationLeaseTests(ITestOutputHelper output)
{
    private static readonly TimeSpan Lease = TimeSpan.FromSeconds(30);

    [Fact(DisplayName = "R01_TwoSynchronizedAcquirersContendForOneEvent_OnlyOneValidLease")]
    public async Task R01TwoSynchronizedAcquirersContendForOneEventOnlyOneValidLease()
    {
        await using var rig = await ConsolidationRig.CreateAsync();
        var source = LeaseSupport.NewSource(rig);

        // (1) Prova determinística: uma linha travada por outra sessão é saltada, não esperada, e a reserva fica com quem chega depois do desbloqueio.
        var locked = await LeaseSupport.InsertEventAsync(rig);
        await using (var holder = new NpgsqlConnection(rig.OriginConnection))
        {
            await holder.OpenAsync();
            await using var transaction = await holder.BeginTransactionAsync();
            await using (var hold = new NpgsqlCommand("SELECT sequence FROM consolidation_outbox WHERE event_id = @id FOR UPDATE", holder, transaction))
            {
                hold.Parameters.AddWithValue("id", locked);
                await hold.ExecuteScalarAsync();
            }

            var watch = Stopwatch.StartNew();
            var whileLocked = await source.AcquireAsync("owner-b", null, Lease, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
            watch.Stop();
            output.WriteLine($"R01 com a linha travada: adquirida={(whileLocked is null ? "nenhuma" : "ALGUMA")} em {watch.ElapsedMilliseconds} ms (SKIP LOCKED, sem espera)");
            Assert.Null(whileLocked);
            await transaction.RollbackAsync();
        }

        var first = await source.AcquireAsync("owner-a", null, Lease, CancellationToken.None);
        Assert.NotNull(first);
        Assert.Null(await source.AcquireAsync("owner-b", null, Lease, CancellationToken.None)); // reserva válida de A: B não adquire a mesma posse
        var state = await LeaseSupport.LeaseStateAsync(rig, locked);
        Assert.Equal((short)0, state.Status); // reservado continua pendente
        Assert.Equal("owner-a", state.Owner);
        Assert.Equal(first.Token, state.Token);
        Assert.True(await source.ConfirmAsync(first.Event.Sequence, first.Token, CancellationToken.None));

        // (2) Disputa sincronizada repetida: dois adquirentes largam juntos sobre um único evento; exatamente uma reserva por rodada.
        const int rounds = 25;
        var winsA = 0;
        var winsB = 0;
        for (var round = 0; round < rounds; round++)
        {
            var id = await LeaseSupport.InsertEventAsync(rig);
            var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var a = Task.Run(async () =>
            {
                await start.Task;
                return await source.AcquireAsync("owner-a", null, Lease, CancellationToken.None);
            });
            var b = Task.Run(async () =>
            {
                await start.Task;
                return await source.AcquireAsync("owner-b", null, Lease, CancellationToken.None);
            });
            start.SetResult();
            var results = await Task.WhenAll(a, b);

            Assert.Equal(1, results.Count(r => r is not null));
            var winner = results.Single(r => r is not null)!;
            var ownerWon = results[0] is not null ? "owner-a" : "owner-b";
            winsA += results[0] is not null ? 1 : 0;
            winsB += results[1] is not null ? 1 : 0;
            var current = await LeaseSupport.LeaseStateAsync(rig, id);
            Assert.Equal(ownerWon, current.Owner);
            Assert.Equal(winner.Token, current.Token);
            Assert.Equal(winner.Event.EventId, id);
            Assert.True(await source.ConfirmAsync(winner.Event.Sequence, winner.Token, CancellationToken.None));
        }

        output.WriteLine($"R01 {rounds} rodadas sincronizadas: vitórias de A={winsA}, de B={winsB}; em todas, exatamente uma reserva válida");
    }

    [Fact(DisplayName = "R02_ExpiredLeaseTakenByB_LateConfirmFailReleaseFromA_AffectZeroRows_MoneyAndReceiptCountedOnce")]
    public async Task R02ExpiredLeaseTakenByBLateConfirmFailReleaseFromAAffectZeroRowsMoneyAndReceiptCountedOnce()
    {
        await using var rig = await ConsolidationRig.CreateAsync();
        var source = LeaseSupport.NewSource(rig);
        var id = await LeaseSupport.InsertEventAsync(rig, type: 1, amount: 12.34m);

        var a = await source.AcquireAsync("owner-a", null, Lease, CancellationToken.None);
        Assert.NotNull(a);
        await LeaseSupport.ExpireLeaseNowAsync(rig, id); // intervenção declarada: o vencimento é antecipado no banco descartável

        var b = await source.AcquireAsync("owner-b", null, Lease, CancellationToken.None);
        Assert.NotNull(b);
        Assert.NotEqual(a.Token, b.Token); // token novo, não o mesmo nome de instância
        var before = await LeaseSupport.LeaseStateAsync(rig, id);
        Assert.Equal("owner-b", before.Owner);
        Assert.Equal(b.Token, before.Token);

        // A, atrasada, tenta confirmar, falhar e liberar: nenhuma linha afetada e a posse de B permanece intacta.
        Assert.False(await source.ConfirmAsync(a.Event.Sequence, a.Token, CancellationToken.None));
        Assert.Equal(FailureRecording.LeaseLost, await source.RecordFailureAsync(a.Event.Sequence, a.Token, "InvalidEventException: x", 5, TimeSpan.Zero, CancellationToken.None));
        Assert.False(await source.ReleaseAsync(a.Event.Sequence, a.Token, TimeSpan.Zero, CancellationToken.None));
        var after = await LeaseSupport.LeaseStateAsync(rig, id);
        Assert.Equal(before, after);
        Assert.Equal(0, after.Attempts);
        output.WriteLine($"R02 depois das tentativas tardias de A: status={after.Status} tentativas={after.Attempts} dono={after.Owner} token de B preservado={after.Token == b.Token}");

        // O mesmo evento alcança o applier duas vezes (A e B): dinheiro e recibo entram uma vez.
        var event1 = a.Event;
        var event2 = b.Event;
        Assert.Equal(event1.EventId, event2.EventId);
        await using (var projectionA = new ConsolidationDbContext(new DbContextOptionsBuilder<ConsolidationDbContext>().UseNpgsql(rig.ProjectionConnection).Options))
        await using (var projectionB = new ConsolidationDbContext(new DbContextOptionsBuilder<ConsolidationDbContext>().UseNpgsql(rig.ProjectionConnection).Options))
        {
            var first = await new DailyConsolidationApplier(projectionB, new NoConsolidationFailurePoints(), TimeProvider.System).ApplyAsync(event2, CancellationToken.None);
            var second = await new DailyConsolidationApplier(projectionA, new NoConsolidationFailurePoints(), TimeProvider.System).ApplyAsync(event1, CancellationToken.None);
            Assert.Equal(ApplyOutcome.Applied, first);
            Assert.Equal(ApplyOutcome.AlreadyApplied, second);
        }

        Assert.Equal(1, await rig.ProcessedCountAsync());
        Assert.Equal(12.34m, await rig.ProjectionScalarAsync<decimal>("SELECT COALESCE(SUM(credits), 0) FROM daily_account_consolidation WHERE account_id = @a", ("a", LeaseSupport.A1)));
        Assert.True(await source.ConfirmAsync(b.Event.Sequence, b.Token, CancellationToken.None)); // quem tem a posse confirma
        Assert.Equal(1, await rig.OutboxCountAsync(1));
    }

    [Fact(DisplayName = "R03_TwoWorkersApplyDifferentEventsOfTheSameAccountDay_ExactTotals_FailureAfterDestinationCommit")]
    public async Task R03TwoWorkersApplyDifferentEventsOfTheSameAccountDayExactTotalsFailureAfterDestinationCommit()
    {
        await using var rig = await ConsolidationRig.CreateAsync();
        await rig.BringToLiveAsync();

        // Worker 1 aplica o primeiro evento, confirma na projeção e fica retido antes de marcar a origem; só então falha (resultado não confirmado).
        var gate = new Gate();
        var points = new TestConsolidationFailurePoints();
        points.AfterProjectionCommitBeforeMarkOnce(async ct =>
        {
            await gate.PassAsync(ct);
            throw new InvalidOperationException("falha injetada");
        });
        var worker1 = await rig.StartWorkerAsync(new WorkerStart { FailurePoints = points });

        var at = DateTimeOffset.UtcNow.AddMinutes(-1);
        var amounts = new (string Type, decimal Amount)[]
        {
            ("Credit", 10.00m), ("Debit", 3.25m), ("Credit", 7.10m), ("Credit", 0.99m), ("Debit", 1.01m), ("Credit", 22.22m),
            ("Debit", 4.40m), ("Credit", 5.05m), ("Credit", 9.99m), ("Debit", 6.60m), ("Credit", 1.50m), ("Debit", 2.50m),
            ("Credit", 8.80m), ("Debit", 0.10m), ("Credit", 3.33m), ("Credit", 4.44m), ("Debit", 7.77m), ("Credit", 6.06m),
            ("Debit", 1.11m), ("Credit", 2.22m),
        };
        foreach (var (type, amount) in amounts)
        {
            await rig.PostOkAsync(LeaseSupport.CustomerA, LeaseSupport.A1, type, amount, at);
        }

        await gate.Reached.WaitAsync(TimeSpan.FromSeconds(30)); // barreira: o Worker 1 já aplicou um evento e o segura
        var worker2 = await rig.StartWorkerAsync();
        await ConsolidationRig.Eventually(async () => await rig.OutboxCountAsync(1) == amounts.Length - 1, "o Worker 2 confirma todos os outros");
        Assert.Equal(1, await rig.OutboxCountAsync(0)); // o evento retido segue pendente (aplicado e não confirmado), reservado pelo Worker 1
        Assert.Equal(amounts.Length, await rig.ProcessedCountAsync());

        gate.Release();
        await ConsolidationRig.Eventually(async () => await rig.OutboxCountAsync(1) == amounts.Length, "o Worker 1 reentrega o seu evento e confirma");

        var day = BusinessDay.Of(at);
        var expectedCredits = amounts.Where(x => x.Type == "Credit").Sum(x => x.Amount);
        var expectedDebits = amounts.Where(x => x.Type == "Debit").Sum(x => x.Amount);
        await using var connection = new NpgsqlConnection(rig.ProjectionConnection);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT credits, debits, net, movement_count FROM daily_account_consolidation WHERE account_id = @a AND business_day = @d", connection);
        command.Parameters.AddWithValue("a", LeaseSupport.A1);
        command.Parameters.AddWithValue("d", day);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(expectedCredits, reader.GetDecimal(0));
        Assert.Equal(expectedDebits, reader.GetDecimal(1));
        Assert.Equal(expectedCredits - expectedDebits, reader.GetDecimal(2));
        Assert.Equal(amounts.Length, reader.GetInt64(3));
        Assert.Equal(amounts.Length, await rig.ProcessedCountAsync());
        Assert.Equal(0, await rig.ProjectionScalarAsync<long>("SELECT COUNT(*) - COUNT(DISTINCT event_id) FROM processed_events"));

        var c1 = LeaseSupport.CountersOf(worker1).Snapshot();
        var c2 = LeaseSupport.CountersOf(worker2).Snapshot();
        output.WriteLine($"R03 totais exatos: créditos={expectedCredits} débitos={expectedDebits} n={amounts.Length}; Worker1 {c1}; Worker2 {c2}");
        Assert.True(c1.AppliedNew >= 1 && c2.AppliedNew >= 1, "ambos os Workers aplicaram eventos distintos");
        Assert.Equal(amounts.Length, c1.AppliedNew + c2.AppliedNew);
        Assert.Equal(1, c1.DuplicatesAvoided); // a reentrega depois do commit do destino não somou de novo
        Assert.Equal(1, c1.UnconfirmedAcknowledgements + c1.EnvironmentFailures); // a falha injetada foi observada pelo Worker 1
    }

    [Fact(DisplayName = "R04_OwnerDiesAfterReservationBeforeProjection_OtherWorkerRecoversOnlyAfterExpiry")]
    public async Task R04OwnerDiesAfterReservationBeforeProjectionOtherWorkerRecoversOnlyAfterExpiry()
    {
        await using var rig = await ConsolidationRig.CreateAsync();
        await rig.BringToLiveAsync();
        var source = LeaseSupport.NewSource(rig);
        var id = await LeaseSupport.InsertEventAsync(rig, type: 1, amount: 8.00m);

        // O "dono" reserva e desaparece: não aplica, não confirma e não libera (queda abrupta; o processo real é encerrado na sondagem R04).
        var watch = Stopwatch.StartNew();
        var dead = await source.AcquireAsync("dead-worker", null, TimeSpan.FromSeconds(4), CancellationToken.None);
        Assert.NotNull(dead);

        var worker = await rig.StartWorkerAsync();
        await Task.Delay(TimeSpan.FromSeconds(1.5));
        var during = await LeaseSupport.LeaseStateAsync(rig, id);
        Assert.Equal((short)0, during.Status);
        Assert.Equal("dead-worker", during.Owner); // ainda retido: a reserva é válida
        Assert.Equal(0, await rig.ProcessedCountAsync());

        await ConsolidationRig.Eventually(async () => (await LeaseSupport.LeaseStateAsync(rig, id)).Status == 1, "recuperado depois do vencimento");
        watch.Stop();
        output.WriteLine($"R04 recuperação em {watch.Elapsed.TotalSeconds:F1} s com lease de 4 s; eventos processados={await rig.ProcessedCountAsync()}");
        Assert.True(watch.Elapsed >= TimeSpan.FromSeconds(3.9), "só depois do vencimento no relógio da origem");
        Assert.Equal(1, await rig.ProcessedCountAsync());
        Assert.Equal(0, (await LeaseSupport.LeaseStateAsync(rig, id)).Attempts);
        Assert.Equal(1, LeaseSupport.CountersOf(worker).Snapshot().AppliedNew);
    }

    private sealed class FlakyProjection : IConsolidationFailurePoints
    {
        private int _failures;

        public volatile bool Down;

        public int Failures => Volatile.Read(ref _failures);

        public ValueTask InsideProjectionTransactionAsync(CancellationToken cancellationToken)
        {
            if (Down)
            {
                Interlocked.Increment(ref _failures);
                throw new TimeoutException("projeção indisponível (injetada)");
            }

            return ValueTask.CompletedTask;
        }

        public ValueTask AfterProjectionCommitBeforeMarkAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }

    [Fact(DisplayName = "R05_ProlongedTransientFailureWithTwoWorkers_NoAttemptSpent_NoQuarantine_ConvergesWithoutManualStatusEdit")]
    public async Task R05ProlongedTransientFailureWithTwoWorkersNoAttemptSpentNoQuarantineConvergesWithoutManualStatusEdit()
    {
        await using var rig = await ConsolidationRig.CreateAsync();
        await rig.BringToLiveAsync();
        var flaky = new FlakyProjection { Down = true };
        var worker1 = await rig.StartWorkerAsync(new WorkerStart { FailurePoints = flaky });
        var worker2 = await rig.StartWorkerAsync(new WorkerStart { FailurePoints = flaky });

        var at = DateTimeOffset.UtcNow.AddMinutes(-1);
        var ids = new List<Guid>();
        for (var i = 0; i < 6; i++)
        {
            ids.Add(await rig.PostOkAsync(LeaseSupport.CustomerA, LeaseSupport.A1, "Credit", 2.50m, at));
        }

        // Falha prolongada: bem mais tentativas do que as cinco do orçamento de um evento.
        await ConsolidationRig.Eventually(() => Task.FromResult(flaky.Failures >= 12), "pelo menos 12 falhas transitórias na projeção", TimeSpan.FromSeconds(60));
        Assert.Equal(6, await rig.OutboxCountAsync(0));
        Assert.Equal(0, await rig.OutboxCountAsync(2));
        Assert.Equal(0, await rig.OriginScalarAsync<int>("SELECT COALESCE(MAX(attempts), 0) FROM consolidation_outbox"));
        Assert.Equal(0, await rig.ProcessedCountAsync());
        Assert.True(worker1.Diagnostics.Failures(FailureClass.Transient).Count + worker2.Diagnostics.Failures(FailureClass.Transient).Count >= 2);

        flaky.Down = false; // a dependência volta: nenhuma edição manual de status
        await ConsolidationRig.Eventually(async () => await rig.OutboxCountAsync(1) == 6, "convergência depois da recuperação", TimeSpan.FromSeconds(60));
        Assert.Equal(6, await rig.ProcessedCountAsync());
        Assert.Equal(15.00m, await rig.ProjectionScalarAsync<decimal>("SELECT COALESCE(SUM(credits), 0) FROM daily_account_consolidation WHERE account_id = @a", ("a", LeaseSupport.A1)));
        Assert.Equal(0, await rig.OriginScalarAsync<int>("SELECT COALESCE(MAX(attempts), 0) FROM consolidation_outbox"));
        var released = LeaseSupport.CountersOf(worker1).Snapshot().Releases + LeaseSupport.CountersOf(worker2).Snapshot().Releases;
        output.WriteLine($"R05 falhas injetadas={flaky.Failures}; reservas liberadas com adiamento={released}; eventos={ids.Count} todos confirmados, tentativas de evento=0, quarentena=0");
        Assert.True(released >= 1);
    }
}
