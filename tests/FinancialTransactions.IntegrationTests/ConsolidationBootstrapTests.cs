using System.Globalization;
using System.Net;
using FinancialTransactions.Consolidation;
using Npgsql;
using Xunit.Abstractions;

namespace FinancialTransactions.IntegrationTests;

/// <summary>
/// C9a a C9h: bootstrap do consolidado com a escrita de lançamentos ativa. Cada subcaso usa um par de bancos próprio (sem a pré-condição de
/// phase live). Os esperados são literais ou somas calculadas aqui, a partir dos movimentos criados pelo próprio teste.
/// </summary>
public sealed class ConsolidationBootstrapTests(ITestOutputHelper output)
{
    private static readonly Guid CustomerA = ConsolidationRig.CustomerA;
    private static readonly Guid A1 = ConsolidationRig.AccountA1;
    private static readonly Guid A2 = ConsolidationRig.AccountA2;

    private static DateTimeOffset At(string iso) => DateTimeOffset.Parse(iso, CultureInfo.InvariantCulture);

    private sealed record Seeded(Guid Account, string Type, decimal Amount, DateTimeOffset EffectiveAt, Guid Id)
    {
        public decimal Signed => Type == "Credit" ? Amount : -Amount;
    }

    /// <summary>10 movimentos legados, inseridos por SQL e sem Outbox.</summary>
    private static async Task<List<Seeded>> InsertTenLegacyAsync(ConsolidationRig rig)
    {
        var plan = new (Guid Account, string Type, decimal Amount, string At)[]
        {
            (A1, "Credit", 10.00m, "2026-09-01T12:00:00Z"),
            (A1, "Credit", 10.00m, "2026-09-01T12:30:00Z"),
            (A1, "Credit", 10.00m, "2026-09-01T13:00:00Z"),
            (A1, "Debit", 4.00m, "2026-09-01T14:00:00Z"),
            (A1, "Debit", 4.00m, "2026-09-01T14:30:00Z"),
            (A1, "Debit", 4.00m, "2026-09-01T15:00:00Z"),
            (A1, "Credit", 6.00m, "2026-08-31T15:00:00Z"),
            (A1, "Credit", 6.00m, "2026-08-31T16:00:00Z"),
            (A2, "Credit", 8.00m, "2026-09-01T14:00:00Z"),
            (A2, "Debit", 2.00m, "2026-09-01T16:00:00Z"),
        };
        var seeded = new List<Seeded>();
        foreach (var (account, type, amount, at) in plan)
        {
            var id = await rig.InsertLegacyMovementAsync(account, type, amount, At(at));
            seeded.Add(new Seeded(account, type, amount, At(at), id));
        }

        return seeded;
    }

    /// <summary>2 movimentos online, por POST, antes do bootstrap (Outbox origin = 0 e bootstrap_id nulo até a captura).</summary>
    private static async Task<List<Seeded>> PostTwoOnlineAsync(ConsolidationRig rig)
    {
        var list = new List<Seeded>();
        foreach (var (account, type, amount, at) in new[] { (A1, "Credit", 7.50m, "2026-09-01T13:30:00Z"), (A2, "Debit", 2.25m, "2026-09-02T13:00:00Z") })
        {
            list.Add(new Seeded(account, type, amount, At(at), await rig.PostOkAsync(CustomerA, account, type, amount, At(at))));
        }

        return list;
    }

    private static readonly DateTimeOffset ThirteenthAt = At("2026-09-02T14:00:00Z");

    private static long SyncCount(string connectionString, string sql)
    {
        using var connection = new NpgsqlConnection(connectionString);
        connection.Open();
        using var command = new NpgsqlCommand(sql, connection);
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static HashSet<Guid> SyncGuids(string connectionString, string sql)
    {
        using var connection = new NpgsqlConnection(connectionString);
        connection.Open();
        using var command = new NpgsqlCommand(sql, connection);
        using var reader = command.ExecuteReader();
        var set = new HashSet<Guid>();
        while (reader.Read())
        {
            set.Add(reader.GetGuid(0));
        }

        return set;
    }

    /// <summary>Estado no instante da promoção para live, lido de forma síncrona na thread do consumidor, antes de qualquer outro consumo.</summary>
    private sealed record AtPromotion(long Processed, long AggregatedMovements, HashSet<Guid> ProcessedIds, bool ReconciledAtSet);

    private static TestDiagnostics CapturePromotion(ConsolidationRig rig, Action<AtPromotion> store) => new()
    {
        OnPhaseChanged = phase =>
        {
            if (phase == ConsolidationBootstrapState.Live)
            {
                store(new AtPromotion(
                    SyncCount(rig.ProjectionConnection, "SELECT COUNT(*) FROM processed_events"),
                    SyncCount(rig.ProjectionConnection, "SELECT COALESCE(SUM(movement_count), 0)::bigint FROM daily_account_consolidation"),
                    SyncGuids(rig.ProjectionConnection, "SELECT event_id FROM processed_events"),
                    SyncCount(rig.ProjectionConnection, "SELECT COUNT(*) FROM consolidation_bootstrap WHERE reconciled_at IS NOT NULL") == 1));
            }
        },
    };

    private static async Task<(Guid BootstrapId, long MemberCount, string Snapshot)> ReadCaptureAsync(ConsolidationRig rig)
    {
        await using var connection = new NpgsqlConnection(rig.OriginConnection);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT bootstrap_id, member_count, snapshot FROM consolidation_bootstrap_capture WHERE id = 1", connection);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync(), "registro de controle ausente na origem");
        return (reader.GetGuid(0), reader.GetInt64(1), reader.GetString(2));
    }

    private static async Task<(Guid BootstrapId, long MemberCount, string Phase, DateTimeOffset? AppliedAt, DateTimeOffset? ReconciledAt, string Xmin)?> ReadStateAsync(ConsolidationRig rig)
    {
        await using var connection = new NpgsqlConnection(rig.ProjectionConnection);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT bootstrap_id, member_count, phase, applied_at, reconciled_at, xmin::text FROM consolidation_bootstrap WHERE id = 1", connection);
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            return null;
        }

        return (
            reader.GetGuid(0),
            reader.GetInt64(1),
            reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetFieldValue<DateTimeOffset>(3),
            reader.IsDBNull(4) ? null : reader.GetFieldValue<DateTimeOffset>(4),
            reader.GetString(5));
    }

    private static Task<IReadOnlyList<Guid>> MembersAsync(ConsolidationRig rig) =>
        rig.OriginGuidsAsync("SELECT event_id FROM consolidation_outbox WHERE bootstrap_id IS NOT NULL ORDER BY event_id");

    private static decimal ExpectedClosing(IEnumerable<Seeded> all, Guid account) => all.Where(s => s.Account == account).Sum(s => s.Signed);

    private static DateOnly Today => BusinessDay.Today(TimeProvider.System);

    [Fact(DisplayName = "C9a_CaptureWithCausalControl_ExactlyTwelveMembers_ThirteenthAppliedInLive")]
    public async Task C9aCaptureWithCausalControlExactlyTwelveMembersThirteenthAppliedInLive()
    {
        await using var rig = await ConsolidationRig.CreateAsync();
        var seeded = await InsertTenLegacyAsync(rig); // (a)
        seeded.AddRange(await PostTwoOnlineAsync(rig)); // (b)

        // (c) o 13.º POST fica retido antes do commit, pelo ponto de falha que já existe na API de lançamentos.
        var held = new Gate();
        using var heldFactory = rig.Writes.WithFailurePoints(new GateRegisterFailurePoints(held));
        var thirteenth = Task.Run(() => rig.PostAsync(CustomerA, A1, "Credit", 1.00m, ThirteenthAt, factory: heldFactory));
        await held.Reached.WaitAsync(TimeSpan.FromSeconds(20));

        // (d) o coordenador para depois de estabelecer o snapshot.
        var snapshotGate = new Gate();
        var coordinator = Task.Run(() => rig.RunCoordinatorAsync(new TestBootstrapFailurePoints { AfterSnapshotEstablished = snapshotGate }));
        await snapshotGate.Reached.WaitAsync(TimeSpan.FromSeconds(20));

        // (e) com a captura pausada, o 13.º é liberado e confirma.
        held.Release();
        var (status13, id13, _) = await thirteenth;
        Assert.Equal(HttpStatusCode.Created, status13);
        var thirteenthId = id13 ?? throw new InvalidOperationException("sem id");
        seeded.Add(new Seeded(A1, "Credit", 1.00m, ThirteenthAt, thirteenthId));

        // (f) coordenador liberado.
        snapshotGate.Release();
        Assert.Equal(CoordinatorExitCodes.Success, await coordinator);

        var capture = await ReadCaptureAsync(rig);
        Assert.Equal(12, capture.MemberCount);
        output.WriteLine($"C9a captura: member_count={capture.MemberCount} bootstrap_id={capture.BootstrapId} snapshot={capture.Snapshot}; coordenador saiu com 0");
        Assert.False(string.IsNullOrWhiteSpace(capture.Snapshot));
        var state = await ReadStateAsync(rig);
        Assert.NotNull(state);
        Assert.Equal((capture.BootstrapId, 12L, "loading"), (state.Value.BootstrapId, state.Value.MemberCount, state.Value.Phase));
        Assert.Equal(12, await rig.OriginScalarAsync<long>("SELECT COUNT(*) FROM consolidation_outbox WHERE bootstrap_id = @id", ("id", capture.BootstrapId)));
        Assert.Equal(13, await rig.OriginScalarAsync<long>("SELECT COUNT(*) FROM consolidation_outbox"));
        Assert.Equal(0, await rig.OriginScalarAsync<long>("SELECT COUNT(*) FROM consolidation_outbox WHERE event_id = @id AND bootstrap_id IS NOT NULL", ("id", thirteenthId)));
        Assert.Equal(0, await rig.OriginScalarAsync<short>("SELECT origin FROM consolidation_outbox WHERE event_id = @id", ("id", thirteenthId)));
        Assert.Equal(10, await rig.OriginScalarAsync<long>("SELECT COUNT(*) FROM consolidation_outbox WHERE origin = 1")); // inseridos pelo bootstrap

        // (g) com a fase em loading a API responde 503 consolidation_not_ready.
        await using var api = await rig.StartConsolidationApiAsync();
        var (notReadyStatus, notReadyBody) = await api.GetAccountAsync(CustomerA, A1, Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, notReadyStatus);
        Assert.Contains("\"code\":\"consolidation_not_ready\"", notReadyBody, StringComparison.Ordinal);

        // (h) serviço iniciado: loading aplica só os 12 e reconcilia o mesmo conjunto.
        AtPromotion? atPromotion = null;
        var worker = await rig.StartWorkerAsync(new WorkerStart { Diagnostics = CapturePromotion(rig, p => atPromotion = p) });
        await ConsolidationRig.Eventually(async () => await rig.ProcessedCountAsync() == 13 && await rig.PhaseAsync() == "live", "13 eventos aplicados em live");

        Assert.NotNull(atPromotion);
        Assert.Equal(12, atPromotion.Processed);
        Assert.Equal(12, atPromotion.AggregatedMovements);
        Assert.True(atPromotion.ReconciledAtSet);
        output.WriteLine($"C9a na promocao para live: processed_events={atPromotion.Processed} movimentos agregados={atPromotion.AggregatedMovements} reconciled_at preenchido={atPromotion.ReconciledAtSet}; GET antes de live: {(int)notReadyStatus} consolidation_not_ready");
        var members = (await MembersAsync(rig)).ToHashSet();
        Assert.Equal(members, atPromotion.ProcessedIds); // processed_events == conjunto, por identidade
        Assert.DoesNotContain(thirteenthId, members);
        Assert.Equal("live", await rig.PhaseAsync());

        var today = Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var (okStatus, okBody) = await api.GetAccountAsync(CustomerA, A1, today);
        Assert.Equal(HttpStatusCode.OK, okStatus);
        var a1 = ConsolidationRig.Parse<DailyDto>(okBody);
        Assert.Equal(ExpectedClosing(seeded, A1), a1.ClosingBalance); // 38,50: os 13 contam
        Assert.Equal("complete", a1.Freshness);

        // (i) um POST no mesmo dia de um legado, aplicado em live; o fechamento inclui tudo.
        var extra = await rig.PostOkAsync(CustomerA, A1, "Credit", 2.00m, At("2026-09-01T15:00:00Z"));
        seeded.Add(new Seeded(A1, "Credit", 2.00m, At("2026-09-01T15:00:00Z"), extra));
        await ConsolidationRig.Eventually(async () => await rig.ProcessedCountAsync() == 14, "evento (i) aplicado");
        Assert.Equal(extra, (await rig.ProjectionGuidsAsync("SELECT event_id FROM processed_events WHERE event_id = '" + extra + "'")).Single());

        var d1 = ConsolidationRig.Parse<DailyDto>((await api.GetAccountAsync(CustomerA, A1, "2026-09-01")).Body);
        Assert.Equal(39.50m, d1.Credits); // 3x10 + 7,50 + 2,00
        Assert.Equal(12.00m, d1.Debits);
        Assert.Equal(8, d1.MovementCount);
        Assert.Equal(ExpectedClosing(seeded, A1), ConsolidationRig.Parse<DailyDto>((await api.GetAccountAsync(CustomerA, A1, today)).Body).ClosingBalance);
        Assert.Equal(ExpectedClosing(seeded, A2), ConsolidationRig.Parse<DailyDto>((await api.GetAccountAsync(CustomerA, A2, today)).Body).ClosingBalance);
        Assert.False(worker.LoopCompleted);
    }

    [Fact(DisplayName = "C9b_ThirteenthCommitsBeforeTheCoordinator_ThirteenMembers_LiveOnlyAfterAllThirteenReconciled")]
    public async Task C9bThirteenthCommitsBeforeTheCoordinatorThirteenMembersLiveOnlyAfterAllThirteenReconciled()
    {
        await using var rig = await ConsolidationRig.CreateAsync();
        var seeded = await InsertTenLegacyAsync(rig);
        seeded.AddRange(await PostTwoOnlineAsync(rig));
        seeded.Add(new Seeded(A1, "Credit", 1.00m, ThirteenthAt, await rig.PostOkAsync(CustomerA, A1, "Credit", 1.00m, ThirteenthAt)));

        Assert.Equal(CoordinatorExitCodes.Success, await rig.RunCoordinatorAsync());

        var capture = await ReadCaptureAsync(rig);
        Assert.Equal(13, capture.MemberCount);
        Assert.Equal(13, await rig.OriginScalarAsync<long>("SELECT COUNT(*) FROM consolidation_outbox WHERE bootstrap_id = @id", ("id", capture.BootstrapId)));
        output.WriteLine($"C9b captura: member_count={capture.MemberCount} bootstrap_id={capture.BootstrapId} snapshot={capture.Snapshot}");
        Assert.Equal(0, await rig.OriginScalarAsync<long>("SELECT COUNT(*) FROM consolidation_outbox WHERE bootstrap_id IS NULL"));

        AtPromotion? atPromotion = null;
        await rig.StartWorkerAsync(new WorkerStart { Diagnostics = CapturePromotion(rig, p => atPromotion = p) });
        await ConsolidationRig.Eventually(async () => await rig.PhaseAsync() == "live", "live");

        Assert.NotNull(atPromotion);
        Assert.Equal(13, atPromotion.Processed);
        Assert.Equal(13, atPromotion.AggregatedMovements);
        Assert.Equal(13, await rig.OutboxCountAsync(1));
        output.WriteLine($"C9b na promocao para live: processed_events={atPromotion.Processed} movimentos agregados={atPromotion.AggregatedMovements}");
        Assert.Equal(seeded.Select(s => s.Id).ToHashSet(), atPromotion.ProcessedIds);
    }

    [Fact(DisplayName = "C9c_InterruptionBetweenTheDatabases_RerunActivatesFromTheOriginControlRecord_WithoutRemarking")]
    public async Task C9cInterruptionBetweenTheDatabasesRerunActivatesFromTheOriginControlRecord()
    {
        await using var rig = await ConsolidationRig.CreateAsync();
        var seeded = await InsertTenLegacyAsync(rig);
        seeded.AddRange(await PostTwoOnlineAsync(rig));
        seeded.Add(new Seeded(A1, "Credit", 1.00m, ThirteenthAt, await rig.PostOkAsync(CustomerA, A1, "Credit", 1.00m, ThirteenthAt)));

        // A captura confirma na origem; logo depois a projeção fica inalcançável para o papel Bootstrap.
        var connections = new TestConnectionFactory(rig.Real());
        var points = new TestBootstrapFailurePoints
        {
            AfterCaptureCommitBeforeActivation = _ =>
            {
                connections.RedirectProjection(ConsolidationConnectionRole.Bootstrap, TestConnectionFactory.Unreachable);
                return Task.CompletedTask;
            },
        };
        var interruptedExit = await rig.RunCoordinatorAsync(points, connections);
        Assert.Equal(CoordinatorExitCodes.Unreachable, interruptedExit);

        var capture = await ReadCaptureAsync(rig);
        output.WriteLine($"C9c interrupcao: saida={interruptedExit}; registro de controle member_count={capture.MemberCount} bootstrap_id={capture.BootstrapId}; destino ausente={await ReadStateAsync(rig) is null}");
        Assert.Equal(13, capture.MemberCount); // a captura sobreviveu à interrupção
        Assert.Null(await ReadStateAsync(rig)); // nada foi ativado
        var markedBefore = (await MembersAsync(rig)).ToHashSet();
        Assert.Equal(13, markedBefore.Count);

        var fourteenth = await rig.PostOkAsync(CustomerA, A1, "Credit", 3.00m, At("2026-09-03T12:00:00Z"));
        connections.Restore(ConsolidationConnectionRole.Bootstrap);
        var rerunExit = await rig.RunCoordinatorAsync(factory: connections);
        Assert.Equal(CoordinatorExitCodes.Success, rerunExit);

        var state = await ReadStateAsync(rig);
        Assert.NotNull(state);
        output.WriteLine($"C9c reexecucao: saida={rerunExit}; destino bootstrap_id={state.Value.BootstrapId} member_count={state.Value.MemberCount} phase={state.Value.Phase}; ids marcados antes={markedBefore.Count} depois={(await MembersAsync(rig)).Count}");
        Assert.Equal((capture.BootstrapId, 13L), (state.Value.BootstrapId, state.Value.MemberCount)); // o mesmo da linha de controle
        Assert.Equal(markedBefore, (await MembersAsync(rig)).ToHashSet()); // nenhuma linha remarcada
        Assert.Equal(0, await rig.OriginScalarAsync<long>("SELECT COUNT(*) FROM consolidation_outbox WHERE event_id = @id AND bootstrap_id IS NOT NULL", ("id", fourteenth)));
        Assert.Equal(capture.BootstrapId, await rig.OriginScalarAsync<Guid>("SELECT bootstrap_id FROM consolidation_bootstrap_capture")); // não foi regravada

        AtPromotion? atPromotion = null;
        await rig.StartWorkerAsync(new WorkerStart { Diagnostics = CapturePromotion(rig, p => atPromotion = p) });
        await ConsolidationRig.Eventually(async () => await rig.ProcessedCountAsync() == 14 && await rig.PhaseAsync() == "live", "14 aplicados");
        Assert.NotNull(atPromotion);
        Assert.Equal(13, atPromotion.Processed);
        Assert.DoesNotContain(fourteenth, atPromotion.ProcessedIds);
    }

    [Fact(DisplayName = "C9d_EmptySetWithTheSameInterruption_ControlRecordWithZeroMembers_ActivatesAndLivesWithoutMembers")]
    public async Task C9dEmptySetWithTheSameInterruptionControlRecordWithZeroMembers()
    {
        await using var rig = await ConsolidationRig.CreateAsync();
        var connections = new TestConnectionFactory(rig.Real());
        var points = new TestBootstrapFailurePoints
        {
            AfterCaptureCommitBeforeActivation = _ =>
            {
                connections.RedirectProjection(ConsolidationConnectionRole.Bootstrap, TestConnectionFactory.Unreachable);
                return Task.CompletedTask;
            },
        };
        Assert.Equal(CoordinatorExitCodes.Unreachable, await rig.RunCoordinatorAsync(points, connections));

        var capture = await ReadCaptureAsync(rig);
        output.WriteLine($"C9d interrupcao: registro de controle member_count={capture.MemberCount} bootstrap_id={capture.BootstrapId} snapshot={capture.Snapshot}");
        Assert.Equal(0, capture.MemberCount); // a existência da linha significa "captura confirmada", mesmo vazia
        Assert.Equal(0, await rig.OriginScalarAsync<long>("SELECT COUNT(*) FROM consolidation_outbox WHERE bootstrap_id IS NOT NULL"));

        var first = await rig.PostOkAsync(CustomerA, A1, "Credit", 5.00m, At("2026-09-03T12:00:00Z"));
        var second = await rig.PostOkAsync(CustomerA, A2, "Debit", 1.00m, At("2026-09-03T13:00:00Z"));
        connections.Restore(ConsolidationConnectionRole.Bootstrap);
        Assert.Equal(CoordinatorExitCodes.Success, await rig.RunCoordinatorAsync(factory: connections));

        var state = await ReadStateAsync(rig);
        Assert.NotNull(state);
        Assert.Equal((capture.BootstrapId, 0L), (state.Value.BootstrapId, state.Value.MemberCount));
        Assert.Equal(2, await rig.OriginScalarAsync<long>("SELECT COUNT(*) FROM consolidation_outbox WHERE bootstrap_id IS NULL AND event_id = ANY(@ids)", ("ids", new[] { first, second })));

        AtPromotion? atPromotion = null;
        await rig.StartWorkerAsync(new WorkerStart { Diagnostics = CapturePromotion(rig, p => atPromotion = p) });
        await ConsolidationRig.Eventually(async () => await rig.ProcessedCountAsync() == 2 && await rig.PhaseAsync() == "live", "2 aplicados em live");
        Assert.NotNull(atPromotion);
        Assert.Equal(0, atPromotion.Processed); // loading sem membros: reconciliação vazia = vazia
        Assert.Equal(0, atPromotion.AggregatedMovements);
        Assert.True(atPromotion.ReconciledAtSet);
        output.WriteLine($"C9d na promocao para live: processed_events={atPromotion.Processed} movimentos agregados={atPromotion.AggregatedMovements} reconciled_at preenchido={atPromotion.ReconciledAtSet}; destino member_count={state.Value.MemberCount}");
    }

    [Fact(DisplayName = "C9e_ConcurrentRerunReadLoading_ServicePromotesLive_CoordinatorReleased_ChangesNothing")]
    public async Task C9eConcurrentRerunReadLoadingServicePromotesLiveCoordinatorReleasedChangesNothing()
    {
        await using var rig = await ConsolidationRig.CreateAsync();
        var seeded = await InsertTenLegacyAsync(rig);
        seeded.AddRange(await PostTwoOnlineAsync(rig));
        seeded.Add(new Seeded(A1, "Credit", 1.00m, ThirteenthAt, await rig.PostOkAsync(CustomerA, A1, "Credit", 1.00m, ThirteenthAt)));
        Assert.Equal(CoordinatorExitCodes.Success, await rig.RunCoordinatorAsync());

        // O serviço aplica os 13 membros mas não consegue reconciliar (papel Reconcile inalcançável): fica em loading.
        var workerConnections = new TestConnectionFactory(rig.Real());
        workerConnections.RedirectProjection(ConsolidationConnectionRole.Reconcile, TestConnectionFactory.Unreachable);
        await rig.StartWorkerAsync(new WorkerStart { Factory = workerConnections });
        await ConsolidationRig.Eventually(async () => await rig.ProcessedCountAsync() == 13, "13 membros aplicados");
        var postCapture = await rig.PostOkAsync(CustomerA, A1, "Credit", 3.00m, At("2026-09-03T12:00:00Z"));
        var loading = await ReadStateAsync(rig);
        Assert.NotNull(loading);
        Assert.Equal("loading", loading.Value.Phase);
        Assert.Null(loading.Value.AppliedAt);

        // Segundo coordenador: já leu loading e fica retido antes de qualquer efeito.
        var gate = new Gate();
        var rerun = Task.Run(() => rig.RunCoordinatorAsync(new TestBootstrapFailurePoints { BeforeActivation = gate }));
        await gate.Reached.WaitAsync(TimeSpan.FromSeconds(20));

        // O serviço avança: reconcilia, promove live e aplica o evento pós-captura.
        workerConnections.Restore(ConsolidationConnectionRole.Reconcile);
        await ConsolidationRig.Eventually(async () => await rig.PhaseAsync() == "live" && await rig.ProcessedCountAsync() == 14, "live e evento pós-captura aplicado");
        var beforeRelease = await ReadStateAsync(rig);
        Assert.NotNull(beforeRelease);
        Assert.NotNull(beforeRelease.Value.AppliedAt);
        Assert.NotNull(beforeRelease.Value.ReconciledAt);

        gate.Release();
        Assert.Equal(CoordinatorExitCodes.Success, await rerun);

        var afterRelease = await ReadStateAsync(rig);
        Assert.NotNull(afterRelease);
        output.WriteLine($"C9e em loading: phase={loading.Value.Phase} applied_at={loading.Value.AppliedAt} reconciled_at={loading.Value.ReconciledAt}");
        output.WriteLine($"C9e live, antes de liberar o coordenador: phase={beforeRelease.Value.Phase} applied_at={beforeRelease.Value.AppliedAt:o} reconciled_at={beforeRelease.Value.ReconciledAt:o} member_count={beforeRelease.Value.MemberCount} bootstrap_id={beforeRelease.Value.BootstrapId} xmin={beforeRelease.Value.Xmin}");
        output.WriteLine($"C9e live, depois de liberar (coordenador saiu com 0): phase={afterRelease.Value.Phase} applied_at={afterRelease.Value.AppliedAt:o} reconciled_at={afterRelease.Value.ReconciledAt:o} member_count={afterRelease.Value.MemberCount} bootstrap_id={afterRelease.Value.BootstrapId} xmin={afterRelease.Value.Xmin}");
        Assert.Equal("live", afterRelease.Value.Phase);
        Assert.Equal(beforeRelease, afterRelease); // applied_at, reconciled_at, member_count, bootstrap_id e xmin idênticos: nenhum UPDATE
        Assert.Contains(postCapture, await rig.ProjectionGuidsAsync("SELECT event_id FROM processed_events"));
    }

    [Fact(DisplayName = "C9f_ServiceStartedBeforeTheCoordinator_WaitsWithoutConsuming_ThenLoadsAndGoesLive")]
    public async Task C9fServiceStartedBeforeTheCoordinatorWaitsWithoutConsumingThenLoadsAndGoesLive()
    {
        await using var rig = await ConsolidationRig.CreateAsync();
        var worker = await rig.StartWorkerAsync();
        await using var api = await rig.StartConsolidationApiAsync();
        var at = DateTimeOffset.UtcNow.AddMinutes(-1);
        for (var i = 0; i < 3; i++)
        {
            await rig.PostOkAsync(CustomerA, A1, "Credit", 1.00m + i, at);
        }

        await Task.Delay(TimeSpan.FromSeconds(3)); // espera de pelo menos 3 s, como no cenário

        Assert.Equal(0, await rig.ProcessedCountAsync());
        Assert.Equal(3, await rig.OutboxCountAsync(0));
        Assert.Equal(3, await rig.OriginScalarAsync<long>("SELECT COUNT(*) FROM consolidation_outbox WHERE bootstrap_id IS NULL"));
        Assert.True(worker.Diagnostics.Count("WaitingForBootstrap") >= 1);
        var (waitingStatus, waitingBody) = await worker.GetAsync("/health/ready");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, waitingStatus);
        Assert.Equal("{\"status\":\"Degraded\"}", waitingBody);
        var (apiStatus, apiBody) = await api.GetAccountAsync(CustomerA, A1, Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, apiStatus);
        Assert.Contains("\"code\":\"consolidation_not_ready\"", apiBody, StringComparison.Ordinal);

        output.WriteLine($"C9f durante a espera: processed_events=0; Outbox pendentes=3 com bootstrap_id nulo; eventos de espera no sink={worker.Diagnostics.Count("WaitingForBootstrap")}; ready do Worker={(int)waitingStatus} {waitingBody}; GET consolidado={(int)apiStatus} consolidation_not_ready");
        Assert.Equal(CoordinatorExitCodes.Success, await rig.RunCoordinatorAsync());
        Assert.Equal(3, (await ReadCaptureAsync(rig)).MemberCount);
        output.WriteLine("C9f coordenador: saida 0; registro de controle member_count=3");

        await ConsolidationRig.Eventually(async () => await rig.PhaseAsync() == "live" && await rig.ProcessedCountAsync() == 3, "serviço carrega os 3 e chega a live");
        await ConsolidationRig.Eventually(async () =>
        {
            var (status, body) = await worker.GetAsync("/health/ready");
            return status == HttpStatusCode.OK && body == "{\"status\":\"Healthy\"}";
        }, "ready 200 Healthy");
        var (finalStatus, finalBody) = await worker.GetAsync("/health/ready");
        output.WriteLine($"C9f depois de live: ready do Worker={(int)finalStatus} {finalBody}; phase={await rig.PhaseAsync()}; processed_events={await rig.ProcessedCountAsync()}");

        await rig.PostOkAsync(CustomerA, A1, "Credit", 9.00m, at);
        await ConsolidationRig.Eventually(async () => await rig.ProcessedCountAsync() == 4, "POST seguinte aplicado em live");
    }

    [Fact(DisplayName = "C9g_ServiceInterruptedMidLoading_RestartedInNewHost_NoDuplicates_ReachesLive")]
    public async Task C9gServiceInterruptedMidLoadingRestartedInNewHostNoDuplicatesReachesLive()
    {
        await using var rig = await ConsolidationRig.CreateAsync();
        var seeded = await InsertTenLegacyAsync(rig);
        Assert.Equal(CoordinatorExitCodes.Success, await rig.RunCoordinatorAsync());

        // O primeiro host morre com o 1.º membro aplicado na projeção e ainda não confirmado na origem.
        var gate = new Gate();
        var points = new TestConsolidationFailurePoints();
        points.AfterProjectionCommitBeforeMarkOnce(async ct => await gate.PassAsync(ct));
        var first = await rig.StartWorkerAsync(new WorkerStart { FailurePoints = points, ConfigureConsumer = o => o.BatchSize = 1 });
        await gate.Reached.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.True(await first.StopAsync(TimeSpan.FromSeconds(10)));

        output.WriteLine($"C9g depois de derrubar o 1.o host: processed_events={await rig.ProcessedCountAsync()} phase={await rig.PhaseAsync()} pendentes na Outbox={await rig.OutboxCountAsync(0)}");
        Assert.Equal(1, await rig.ProcessedCountAsync());
        Assert.Equal("loading", await rig.PhaseAsync());
        Assert.Equal(10, await rig.OutboxCountAsync(0)); // nada confirmado: o 1.º será reentregue

        await rig.StartWorkerAsync(); // host novo sobre os mesmos bancos
        await ConsolidationRig.Eventually(async () => await rig.PhaseAsync() == "live" && await rig.OutboxCountAsync(1) == 10, "chega a live com os 10 confirmados");

        output.WriteLine($"C9g depois do host novo: phase={await rig.PhaseAsync()} processed_events={await rig.ProcessedCountAsync()} duplicatas={await rig.ProjectionScalarAsync<long>("SELECT COUNT(*) - COUNT(DISTINCT event_id) FROM processed_events")} confirmados na Outbox={await rig.OutboxCountAsync(1)}");
        Assert.Equal(10, await rig.ProcessedCountAsync());
        Assert.Equal(0, await rig.ProjectionScalarAsync<long>("SELECT COUNT(*) - COUNT(DISTINCT event_id) FROM processed_events"));
        Assert.Equal(seeded.Select(s => s.Id).ToHashSet(), (await rig.ProjectionGuidsAsync("SELECT event_id FROM processed_events")).ToHashSet());
        foreach (var account in new[] { A1, A2 })
        {
            var projected = await rig.ProjectionScalarAsync<decimal>("SELECT COALESCE(SUM(net), 0) FROM daily_account_consolidation WHERE account_id = @a", ("a", account));
            Assert.Equal(ExpectedClosing(seeded, account), projected); // somas = origem, sem dupla contagem
        }
    }

    [Fact(DisplayName = "C9h_TwoSimultaneousCoordinators_SecondGetsCode3ByTheLockWithinFiveSeconds_ThirdChangesNothing")]
    public async Task C9hTwoSimultaneousCoordinatorsSecondGetsCode3ByTheLockWithinFiveSecondsThirdChangesNothing()
    {
        await using var rig = await ConsolidationRig.CreateAsync();
        await InsertTenLegacyAsync(rig);

        var gate = new Gate();
        var first = Task.Run(() => rig.RunCoordinatorAsync(new TestBootstrapFailurePoints { AfterSnapshotEstablished = gate }));
        await gate.Reached.WaitAsync(TimeSpan.FromSeconds(20)); // o primeiro mantém o lock e a transação de captura abertos

        var watch = System.Diagnostics.Stopwatch.StartNew();
        var secondCode = await rig.RunCoordinatorAsync().WaitAsync(TimeSpan.FromSeconds(5));
        watch.Stop();
        output.WriteLine($"C9h segundo coordenador: saida={secondCode} em {watch.Elapsed.TotalSeconds:F2} s (limite 5 s)");
        Assert.Equal(CoordinatorExitCodes.LockBusy, secondCode); // pelo lock consultivo, não por porta
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5));
        Assert.Equal(0, await rig.OriginScalarAsync<long>("SELECT COUNT(*) FROM consolidation_bootstrap_capture")); // o segundo não tocou em nada

        gate.Release();
        Assert.Equal(CoordinatorExitCodes.Success, await first);

        var captureBefore = await ReadCaptureAsync(rig);
        var stateBefore = await ReadStateAsync(rig);
        var captureXminBefore = await rig.OriginScalarAsync<string>("SELECT xmin::text FROM consolidation_bootstrap_capture");
        Assert.Equal(CoordinatorExitCodes.Success, await rig.RunCoordinatorAsync()); // terceiro: concluído/em andamento, sem alteração

        Assert.Equal(captureBefore, await ReadCaptureAsync(rig));
        Assert.Equal(stateBefore, await ReadStateAsync(rig));
        output.WriteLine($"C9h primeiro: saida=0 member_count={captureBefore.MemberCount} bootstrap_id={captureBefore.BootstrapId}; terceiro: saida=0, registro de controle e destino inalterados (xmin da captura {captureXminBefore})");
        Assert.Equal(captureXminBefore, await rig.OriginScalarAsync<string>("SELECT xmin::text FROM consolidation_bootstrap_capture"));
    }
}
