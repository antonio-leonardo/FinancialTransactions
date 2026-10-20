using System.Text.Json;
using FinancialTransactions.Consolidation;
using FinancialTransactions.Consolidation.Worker;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using Xunit.Abstractions;

namespace FinancialTransactions.IntegrationTests;

/// <summary>
/// R06 a R08: reprocessamento administrativo restrito da quarentena, bootstrap com duas instâncias e observação operacional.
/// Cada cenário usa um par de bancos próprio. Os esperados são literais ou calculados aqui.
/// </summary>
public sealed class ConsolidationAdminTests(ITestOutputHelper output)
{
    private static readonly Guid A1 = ConsolidationRig.AccountA1;
    private static readonly Guid A2 = ConsolidationRig.AccountA2;
    private static readonly Guid CustomerA = ConsolidationRig.CustomerA;

    private static QuarantineAdmin NewAdmin(ConsolidationRig rig) => new(new TestConnectionFactory(rig.Real()));

    private static JsonElement Json(StringWriter writer) => JsonDocument.Parse(writer.ToString()).RootElement;

    private static async Task<(int Exit, JsonElement Body)> RequeueAsync(
        ConsolidationRig rig, string operation, IEnumerable<Guid> ids, bool apply, string? digest = null, string? database = null, string reason = RequeueReasons.CauseCorrected)
    {
        var writer = new StringWriter();
        var exit = await NewAdmin(rig).RequeueAsync(
            new RequeueRequest(operation, ids.ToList(), reason, apply, digest, database ?? rig.OriginDatabase), writer, CancellationToken.None);
        return (exit, Json(writer));
    }

    private static Task<string> OriginSnapshotAsync(ConsolidationRig rig) =>
        rig.OriginScalarAsync<string>(
            "SELECT COALESCE(string_agg(event_id::text || ':' || status || ':' || attempts || ':' || COALESCE(last_error, '') || ':' || xmin::text, ',' ORDER BY event_id), '') FROM consolidation_outbox")!;

    private const string QuarantineError = "InvalidEventException: type inválido (9).";

    [Fact(DisplayName = "R06a_DryRunHasNoEffect_ApplyRequiresPlanDigest_TrailIsTransactional_RepeatIsIdempotent_OtherSelectionConflicts")]
    public async Task R06aDryRunHasNoEffectApplyRequiresPlanDigestTrailIsTransactionalRepeatIsIdempotentOtherSelectionConflicts()
    {
        await using var rig = await ConsolidationRig.CreateAsync();
        var e1 = await LeaseSupport.InsertEventAsync(rig, type: 9, status: 2, attempts: 5, lastError: QuarantineError);
        var e2 = await LeaseSupport.InsertEventAsync(rig, type: 1, amount: 5.00m, status: 2, attempts: 5, lastError: QuarantineError);
        var e3 = await LeaseSupport.InsertEventAsync(rig, type: 9, status: 2, attempts: 5, lastError: QuarantineError); // fora da seleção
        var pendingOther = await LeaseSupport.InsertEventAsync(rig, type: 1, amount: 1.00m);
        var movementsBefore = await rig.OriginScalarAsync<long>("SELECT COUNT(*) FROM movements");

        // Dry-run: plano e digest, nenhuma alteração.
        var snapshotBefore = await OriginSnapshotAsync(rig);
        var (planExit, plan) = await RequeueAsync(rig, "op-r06-a", [e1, e2], apply: false);
        Assert.Equal(AdminExitCodes.Success, planExit);
        Assert.Equal("dry-run", plan.GetProperty("mode").GetString());
        Assert.True(plan.GetProperty("eligible").GetBoolean());
        var digest = plan.GetProperty("planDigest").GetString()!;
        Assert.Equal(2, plan.GetProperty("selected").GetInt32());
        Assert.Equal(snapshotBefore, await OriginSnapshotAsync(rig));
        Assert.Equal(0, await rig.OriginScalarAsync<long>("SELECT COUNT(*) FROM consolidation_outbox_admin_operation"));
        Assert.Equal(0, await rig.OriginScalarAsync<long>("SELECT COUNT(*) FROM consolidation_outbox_admin_log"));

        // Aplicar sem digest, com digest de outro plano: recusado/abortado sem consumir o identificador.
        var (noDigestExit, _) = await RequeueAsync(rig, "op-r06-a", [e1, e2], apply: true, digest: null);
        Assert.Equal(AdminExitCodes.Refused, noDigestExit);
        var (wrongExit, wrong) = await RequeueAsync(rig, "op-r06-a", [e1, e2], apply: true, digest: new string('0', 64));
        Assert.Equal(AdminExitCodes.Conflict, wrongExit);
        Assert.Equal("aborted", wrong.GetProperty("state").GetString());
        Assert.Equal(snapshotBefore, await OriginSnapshotAsync(rig));
        Assert.Equal(0, await rig.OriginScalarAsync<long>("SELECT COUNT(*) FROM consolidation_outbox_admin_operation"));

        // Aplicação: só a lista; trilha e mudança na mesma transação.
        var (applyExit, applied) = await RequeueAsync(rig, "op-r06-a", [e1, e2], apply: true, digest: digest);
        Assert.Equal(AdminExitCodes.Success, applyExit);
        Assert.Equal("applied", applied.GetProperty("state").GetString());
        Assert.Equal(2, applied.GetProperty("requeued").GetInt32());
        foreach (var id in new[] { e1, e2 })
        {
            var state = await LeaseSupport.LeaseStateAsync(rig, id);
            Assert.Equal(((short)0, 0), (state.Status, state.Attempts));
            Assert.Null(state.Token);
        }

        var untouched = await LeaseSupport.LeaseStateAsync(rig, e3);
        Assert.Equal(((short)2, 5), (untouched.Status, untouched.Attempts)); // fora da lista: intocado
        Assert.Equal(2, await rig.OriginScalarAsync<long>("SELECT COUNT(*) FROM consolidation_outbox_admin_log WHERE operation_id = 'op-r06-a' AND previous_status = 2 AND previous_attempts = 5 AND previous_error = @e", ("e", QuarantineError)));
        Assert.Equal(movementsBefore, await rig.OriginScalarAsync<long>("SELECT COUNT(*) FROM movements"));
        Assert.Equal(0, await rig.ProcessedCountAsync()); // nenhum recibo criado nem apagado
        var snapshotAfterApply = await OriginSnapshotAsync(rig);

        // Repetição com o mesmo identificador e a mesma seleção: resultado registrado, nenhum reenfileiramento novo.
        var (repeatExit, repeat) = await RequeueAsync(rig, "op-r06-a", [e2, e1], apply: true, digest: digest);
        Assert.Equal(AdminExitCodes.Success, repeatExit);
        Assert.Equal("already_applied", repeat.GetProperty("state").GetString());
        Assert.Equal(snapshotAfterApply, await OriginSnapshotAsync(rig));
        Assert.Equal(2, await rig.OriginScalarAsync<long>("SELECT COUNT(*) FROM consolidation_outbox_admin_log"));

        // Mesmo identificador, outra seleção: conflito, nada alterado.
        var (conflictExit, conflict) = await RequeueAsync(rig, "op-r06-a", [e3], apply: true, digest: "x");
        Assert.Equal(AdminExitCodes.Conflict, conflictExit);
        Assert.Equal("conflict", conflict.GetProperty("state").GetString());
        Assert.Equal(snapshotAfterApply, await OriginSnapshotAsync(rig));

        // Evento que não está em quarentena (pendente) ou inexistente: plano inelegível e aplicação abortada, sem ampliar o conjunto.
        var (badPlanExit, badPlan) = await RequeueAsync(rig, "op-r06-b", [pendingOther], apply: false);
        Assert.Equal(AdminExitCodes.Conflict, badPlanExit);
        Assert.False(badPlan.GetProperty("eligible").GetBoolean());
        output.WriteLine($"R06a plano {digest}; aplicado={applied}; repetição={repeat.GetProperty("state").GetString()}; conflito={conflict.GetProperty("state").GetString()}");
    }

    [Fact(DisplayName = "R06b_PersistentCauseReturnsToQuarantineObservably_ValidEventApplies_NoReceiptDeleted")]
    public async Task R06bPersistentCauseReturnsToQuarantineObservablyValidEventAppliesNoReceiptDeleted()
    {
        await using var rig = await ConsolidationRig.CreateAsync();
        await rig.BringToLiveAsync();
        var defective = await LeaseSupport.InsertEventAsync(rig, type: 9, status: 2, attempts: 5, lastError: QuarantineError);
        var corrected = await LeaseSupport.InsertEventAsync(rig, type: 1, amount: 5.00m, status: 2, attempts: 5, lastError: QuarantineError);

        // Um recibo prévio na projeção (evento já aplicado antes) não pode ser apagado por nenhuma etapa do reprocessamento.
        await rig.PostOkAsync(CustomerA, A1, "Credit", 3.00m, DateTimeOffset.UtcNow.AddMinutes(-1));
        var worker = await rig.StartWorkerAsync();
        await ConsolidationRig.Eventually(async () => await rig.ProcessedCountAsync() == 1, "recibo prévio");
        var receiptBefore = await rig.ProjectionScalarAsync<string>("SELECT string_agg(event_id::text || xmin::text, ',') FROM processed_events");

        var (_, plan) = await RequeueAsync(rig, "op-r06-b1", [defective, corrected], apply: false);
        var (exit, _) = await RequeueAsync(rig, "op-r06-b1", [defective, corrected], apply: true, digest: plan.GetProperty("planDigest").GetString());
        Assert.Equal(AdminExitCodes.Success, exit);

        await ConsolidationRig.Eventually(async () => (await LeaseSupport.LeaseStateAsync(rig, corrected)).Status == 1, "evento corrigido aplicado");
        await ConsolidationRig.Eventually(async () =>
        {
            var state = await LeaseSupport.LeaseStateAsync(rig, defective);
            return state.Status == 2 && state.Attempts == 5;
        }, "causa persistente: volta à quarentena");

        Assert.Equal(2, await rig.ProcessedCountAsync()); // o prévio e o corrigido; o defeituoso nunca ganha recibo
        Assert.Contains(receiptBefore!, await rig.ProjectionScalarAsync<string>("SELECT string_agg(event_id::text || xmin::text, ',') FROM processed_events") ?? string.Empty, StringComparison.Ordinal);
        Assert.Equal(2, await rig.OriginScalarAsync<long>("SELECT COUNT(*) FROM consolidation_outbox_admin_log WHERE previous_attempts = 5"));
        var lastError = await rig.OriginScalarAsync<string>("SELECT last_error FROM consolidation_outbox WHERE event_id = @id", ("id", defective));
        Assert.StartsWith("InvalidEventException: ", lastError, StringComparison.Ordinal);
        Assert.Equal(1, LeaseSupport.CountersOf(worker).Snapshot().Quarantined);
        output.WriteLine($"R06b o defeituoso voltou à quarentena com {5} tentativas; o corrigido aplicado; recibos={await rig.ProcessedCountAsync()}; trilha preservada");
    }

    [Fact(DisplayName = "R06c_RefusesUnconfirmedTarget_MalformedInput_EmptyOrUnknownSelection_AndGlobalEffects")]
    public async Task R06cRefusesUnconfirmedTargetMalformedInputEmptyOrUnknownSelectionAndGlobalEffects()
    {
        await using var rig = await ConsolidationRig.CreateAsync();
        var quarantined = await LeaseSupport.InsertEventAsync(rig, type: 9, status: 2, attempts: 5, lastError: QuarantineError);
        var snapshot = await OriginSnapshotAsync(rig);

        Assert.Equal(AdminExitCodes.Refused, (await RequeueAsync(rig, "op-r06-c", [quarantined], apply: false, database: "financialtransactions")).Exit); // alvo não confirmado
        Assert.Equal(AdminExitCodes.Refused, (await RequeueAsync(rig, "x", [quarantined], apply: false)).Exit); // identificador curto demais
        Assert.Equal(AdminExitCodes.Refused, (await RequeueAsync(rig, "op-r06-c", [], apply: false)).Exit); // nunca "todos"
        Assert.Equal(AdminExitCodes.Refused, (await RequeueAsync(rig, "op-r06-c", [Guid.Empty], apply: false)).Exit);
        Assert.Equal(AdminExitCodes.Refused, (await RequeueAsync(rig, "op-r06-c", [quarantined], apply: false, reason: "qualquer")).Exit); // motivo fora da lista fechada
        var tooMany = Enumerable.Range(0, QuarantineAdmin.MaxEventsPerOperation + 1).Select(_ => Guid.NewGuid());
        Assert.Equal(AdminExitCodes.Refused, (await RequeueAsync(rig, "op-r06-c", tooMany, apply: false)).Exit);

        var (unknownExit, unknown) = await RequeueAsync(rig, "op-r06-c", [Guid.NewGuid()], apply: false); // evento inexistente
        Assert.Equal(AdminExitCodes.Conflict, unknownExit);
        Assert.False(unknown.GetProperty("eligible").GetBoolean());

        Assert.Equal(snapshot, await OriginSnapshotAsync(rig));
        Assert.Equal(0, await rig.OriginScalarAsync<long>("SELECT COUNT(*) FROM consolidation_outbox_admin_operation"));

        // O executável recusa argumentos desconhecidos ou fora do formato antes de qualquer acesso aos dados.
        foreach (var args in new[] { new[] { "--event", "nao-e-guid" }, ["--limite", "3"], ["--event"] })
        {
            var writer = new StringWriter();
            var exit = await WorkerAdmin.RunAsync("--requeue", args, writer, builder => ConfigureConnections(builder, rig));
            Assert.Equal(AdminExitCodes.Refused, exit);
        }

        Assert.Equal(snapshot, await OriginSnapshotAsync(rig));
    }

    [Fact(DisplayName = "R06d_PlanIsBoundToTheRowRevision_OldPlanRefusedAfterAnotherReopening_NewPlanWorks_RepeatStaysIdempotent")]
    public async Task R06dPlanIsBoundToTheRowRevisionOldPlanRefusedAfterAnotherReopeningNewPlanWorksRepeatStaysIdempotent()
    {
        await using var rig = await ConsolidationRig.CreateAsync();
        var e = await LeaseSupport.InsertEventAsync(rig, type: 9, status: 2, attempts: 5, lastError: QuarantineError);

        // Plano A, revisado pelo operador A: evento em quarentena com cinco tentativas.
        var (_, planA) = await RequeueAsync(rig, "op-r06-d1", [e], apply: false);
        var digestA = planA.GetProperty("planDigest").GetString()!;

        // Outra operação reabre o evento; a causa persiste e ele volta a 2/5 (a volta é reproduzida por UPDATE no banco descartável,
        // equivalente ao que o consumidor grava ao esgotar as tentativas; intervenção declarada).
        var (_, planB) = await RequeueAsync(rig, "op-r06-d2", [e], apply: false);
        var digestB = planB.GetProperty("planDigest").GetString()!;
        Assert.Equal(digestA, digestB); // mesma versão da linha: os dois planos coincidem até alguém aplicar um deles
        var (applyBExit, _) = await RequeueAsync(rig, "op-r06-d2", [e], apply: true, digest: digestB);
        Assert.Equal(AdminExitCodes.Success, applyBExit);
        await rig.OriginExecuteAsync("UPDATE consolidation_outbox SET status = 2, attempts = 5, last_error = @e WHERE event_id = @id", ("e", QuarantineError), ("id", e));
        var requarantined = await LeaseSupport.LeaseStateAsync(rig, e);
        Assert.Equal(((short)2, 5), (requarantined.Status, requarantined.Attempts)); // mesmo estado resumido que o plano A viu

        var snapshot = await OriginSnapshotAsync(rig);
        var logsBefore = await rig.OriginScalarAsync<long>("SELECT COUNT(*) FROM consolidation_outbox_admin_log");

        // O plano A, de uma versão anterior da linha, é recusado sem trilha nem efeito parcial.
        var (staleExit, stale) = await RequeueAsync(rig, "op-r06-d1", [e], apply: true, digest: digestA);
        Assert.Equal(AdminExitCodes.Conflict, staleExit);
        Assert.Equal("aborted", stale.GetProperty("state").GetString());
        Assert.Equal(snapshot, await OriginSnapshotAsync(rig));
        Assert.Equal(logsBefore, await rig.OriginScalarAsync<long>("SELECT COUNT(*) FROM consolidation_outbox_admin_log"));
        Assert.Equal(0, await rig.OriginScalarAsync<long>("SELECT COUNT(*) FROM consolidation_outbox_admin_operation WHERE operation_id = 'op-r06-d1'"));

        // Um plano novo, da versão atual, funciona; a repetição da operação já concluída continua idempotente.
        var (_, planC) = await RequeueAsync(rig, "op-r06-d3", [e], apply: false);
        var digestC = planC.GetProperty("planDigest").GetString()!;
        Assert.NotEqual(digestA, digestC);
        var (applyCExit, _) = await RequeueAsync(rig, "op-r06-d3", [e], apply: true, digest: digestC);
        Assert.Equal(AdminExitCodes.Success, applyCExit);
        var afterC = await OriginSnapshotAsync(rig);
        var (repeatExit, repeat) = await RequeueAsync(rig, "op-r06-d3", [e], apply: true, digest: digestC);
        Assert.Equal(AdminExitCodes.Success, repeatExit);
        Assert.Equal("already_applied", repeat.GetProperty("state").GetString());
        Assert.Equal(afterC, await OriginSnapshotAsync(rig));
        output.WriteLine($"R06d plano antigo {digestA[..12]} recusado; plano novo {digestC[..12]} aplicado; repetição idempotente");
    }

    private static void ConfigureConnections(Microsoft.Extensions.Hosting.HostApplicationBuilder builder, ConsolidationRig rig, TestConnectionFactory? factory = null)
    {
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:FinancialTransactions"] = rig.OriginConnection,
            ["ConnectionStrings:Consolidation"] = rig.ProjectionConnection,
        });
        if (factory is not null)
        {
            builder.Services.RemoveAll<IConsolidationConnectionFactory>();
            builder.Services.AddSingleton<IConsolidationConnectionFactory>(factory);
        }
    }

    [Fact(DisplayName = "R07a_TwoWorkersBootstrap_NonMemberWaitsForLiveWhileAMemberIsHeld_ExactMemberSet_PromotionDoesNotRegress")]
    public async Task R07aTwoWorkersBootstrapNonMemberWaitsForLiveWhileAMemberIsHeldExactMemberSetPromotionDoesNotRegress()
    {
        await using var rig = await ConsolidationRig.CreateAsync();
        var at = DateTimeOffset.UtcNow.AddMinutes(-1);
        var members = new List<Guid>();
        for (var i = 0; i < 10; i++)
        {
            members.Add(await rig.InsertLegacyMovementAsync(i % 2 == 0 ? A1 : A2, i % 3 == 0 ? "Debit" : "Credit", 1.00m + i, at));
        }

        members.Add(await rig.PostOkAsync(CustomerA, A1, "Credit", 7.50m, at));
        members.Add(await rig.PostOkAsync(CustomerA, A2, "Credit", 2.25m, at));
        Assert.Equal(CoordinatorExitCodes.Success, await rig.RunCoordinatorAsync());
        var bootstrapId = await rig.OriginScalarAsync<Guid>("SELECT bootstrap_id FROM consolidation_bootstrap_capture WHERE id = 1");

        // Evento novo, posterior à captura: não é membro e só pode ser consumido depois de live.
        var newcomer = await rig.PostOkAsync(CustomerA, A1, "Credit", 100.00m, at);
        Assert.Equal(12, await rig.OriginScalarAsync<long>("SELECT COUNT(*) FROM consolidation_outbox WHERE bootstrap_id = @id", ("id", bootstrapId)));
        Assert.Null(await rig.OriginScalarAsync<Guid?>("SELECT bootstrap_id FROM consolidation_outbox WHERE event_id = @id", ("id", newcomer)));

        // Barreira causal: o Worker 1 aplica um membro na projeção e fica retido antes de confirmá-lo na origem; o membro segue pendente e reservado,
        // o que impede a reconciliação e a promoção enquanto a barreira estiver fechada. A prova não depende de qual Worker promove.
        var gate = new Gate();
        var points = new TestConsolidationFailurePoints();
        points.AfterProjectionCommitBeforeMarkOnce(async ct =>
        {
            await gate.PassAsync(ct);
            throw new InvalidOperationException("falha injetada");
        });
        var worker1 = await rig.StartWorkerAsync(new WorkerStart { FailurePoints = points });
        await gate.Reached.WaitAsync(TimeSpan.FromSeconds(30));
        var worker2 = await rig.StartWorkerAsync();
        await ConsolidationRig.Eventually(async () => await rig.OutboxCountAsync(1) >= 11, "o Worker 2 confirma os membros restantes");
        Assert.Equal(11, await rig.OutboxCountAsync(1)); // exatamente os 11 membros: o não membro não foi consumido

        // Janela de observação com a barreira fechada (dezenas de ciclos ociosos do Worker 2): o não membro não é reservado nem aplicado.
        for (var i = 0; i < 15; i++)
        {
            await Task.Delay(100);
            Assert.Equal("loading", await rig.PhaseAsync());
            Assert.Equal(0, await rig.ProjectionScalarAsync<long>("SELECT COUNT(*) FROM processed_events WHERE event_id = @id", ("id", newcomer)));
            Assert.Equal(12, await rig.ProcessedCountAsync()); // os 12 membros, nada além
        }

        var newcomerState = await LeaseSupport.LeaseStateAsync(rig, newcomer);
        Assert.Equal(((short)0, 0, (string?)null), (newcomerState.Status, newcomerState.Attempts, newcomerState.Owner));
        Assert.Equal(11, LeaseSupport.CountersOf(worker2).Snapshot().Acquisitions); // o Worker 2 nunca adquiriu o não membro
        Assert.Equal(1, LeaseSupport.CountersOf(worker1).Snapshot().Acquisitions);
        Assert.Equal(2, await rig.OutboxCountAsync(0)); // pendentes: o membro retido e o não membro
        output.WriteLine("R07a com a barreira fechada: fase loading, 12 recibos, não membro sem reserva nem recibo, Worker 2 com 11 aquisições");

        gate.Release();
        await ConsolidationRig.Eventually(async () => await rig.PhaseAsync() == "live" && await rig.OutboxCountAsync(1) == 13, "live com os 13 confirmados");
        Assert.Equal(13, await rig.ProcessedCountAsync());
        Assert.Equal(members.Concat([newcomer]).ToHashSet(), (await rig.ProjectionGuidsAsync("SELECT event_id FROM processed_events")).ToHashSet());
        for (var i = 0; i < 10; i++)
        {
            await Task.Delay(100);
            Assert.Equal("live", await rig.PhaseAsync()); // sem regressão de fase
        }

        Assert.False(worker1.App.Services.GetRequiredService<ConsolidationHealthState>().Has(DegradedReason.ReconciliationFailed));
        Assert.False(worker2.App.Services.GetRequiredService<ConsolidationHealthState>().Has(DegradedReason.ReconciliationFailed));
        Assert.Equal(0, await rig.ProjectionScalarAsync<long>("SELECT COUNT(*) - COUNT(DISTINCT event_id) FROM processed_events"));
        Assert.Equal(13, LeaseSupport.CountersOf(worker1).Snapshot().AppliedNew + LeaseSupport.CountersOf(worker2).Snapshot().AppliedNew);
        Assert.Equal(1, LeaseSupport.CountersOf(worker1).Snapshot().DuplicatesAvoided); // o membro retido foi reentregue sem somar de novo
    }

    [Fact(DisplayName = "R07b_ReconcilerFindingLiveByTheLockRecheck_ReturnsAlreadyLive_NoFalseDivergence_NoRegression")]
    public async Task R07bReconcilerFindingLiveByTheLockRecheckReturnsAlreadyLiveNoFalseDivergenceNoRegression()
    {
        await using var rig = await ConsolidationRig.CreateAsync();
        await rig.InsertLegacyMovementAsync(A1, "Credit", 10.00m, DateTimeOffset.UtcNow.AddMinutes(-5));
        Assert.Equal(CoordinatorExitCodes.Success, await rig.RunCoordinatorAsync());
        var bootstrapId = await rig.OriginScalarAsync<Guid>("SELECT bootstrap_id FROM consolidation_bootstrap_capture WHERE id = 1");
        var worker = await rig.StartWorkerAsync();
        await ConsolidationRig.Eventually(async () => await rig.PhaseAsync() == "live", "live");

        // Já em live, a projeção ganha um evento que não é membro: comparar com o conjunto inicial (1) divergiria. Quem chega depois relê a fase.
        await rig.PostOkAsync(CustomerA, A1, "Credit", 4.00m, DateTimeOffset.UtcNow.AddMinutes(-1));
        await ConsolidationRig.Eventually(async () => await rig.ProcessedCountAsync() == 2, "evento posterior aplicado em live");
        var xminBefore = await rig.ProjectionScalarAsync<string>("SELECT xmin::text FROM consolidation_bootstrap WHERE id = 1");

        var factory = new TestConnectionFactory(rig.Real());
        var promoter = new BootstrapPromoter(factory, new BootstrapReconciler(factory), TimeProvider.System, Microsoft.Extensions.Logging.Abstractions.NullLogger<BootstrapPromoter>.Instance);
        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => promoter.TryPromoteAsync(bootstrapId, CancellationToken.None)));
        output.WriteLine($"R07b quatro tentativas simultâneas depois de live: {string.Join(", ", results.Select(r => r.Status))}");
        Assert.All(results, r => Assert.True(r.Status is PromotionStatus.AlreadyLive or PromotionStatus.Busy, $"status inesperado: {r.Status} {r.Deviation}"));
        Assert.Contains(results, r => r.Status == PromotionStatus.AlreadyLive);
        Assert.Equal("live", await rig.PhaseAsync());
        Assert.Equal(xminBefore, await rig.ProjectionScalarAsync<string>("SELECT xmin::text FROM consolidation_bootstrap WHERE id = 1")); // linha não regravada
        Assert.False(worker.App.Services.GetRequiredService<ConsolidationHealthState>().IsDegraded);
    }

    [Theory(DisplayName = "R07c_DescriptorCountDifferentFromTheSet_PreventsPromotion")]
    [InlineData(1, 2, "menor")]
    [InlineData(4, 2, "maior")]
    [InlineData(1, 0, "caso vazio")]
    public async Task R07cDescriptorCountDifferentFromTheSetPreventsPromotion(long declared, int actualMembers, string label)
    {
        await using var rig = await ConsolidationRig.CreateAsync();
        for (var i = 0; i < actualMembers; i++)
        {
            await rig.InsertLegacyMovementAsync(A1, "Credit", 1.00m + i, DateTimeOffset.UtcNow.AddMinutes(-5));
        }

        Assert.Equal(CoordinatorExitCodes.Success, await rig.RunCoordinatorAsync());
        Assert.Equal(actualMembers, await rig.OriginScalarAsync<long>("SELECT member_count FROM consolidation_bootstrap_capture WHERE id = 1"));

        // Os dois descritores (origem e destino) são adulterados de forma coerente entre si: o consumidor os aceita e chega à reconciliação,
        // que precisa recusar porque o conjunto identificado tem outra cardinalidade.
        await rig.OriginExecuteAsync("UPDATE consolidation_bootstrap_capture SET member_count = @n WHERE id = 1", ("n", declared));
        await rig.ProjectionExecuteAsync("UPDATE consolidation_bootstrap SET member_count = @n WHERE id = 1", ("n", declared));

        var worker = await rig.StartWorkerAsync();
        await ConsolidationRig.Eventually(() => Task.FromResult(worker.Diagnostics.Count("ReconciliationFailed") >= 1), $"reconciliação recusada ({label})");
        var deviation = worker.Diagnostics.Events.First(e => e.Kind == "ReconciliationFailed").Detail;
        output.WriteLine($"R07c ({label}): descritor={declared}, conjunto={actualMembers}: {deviation}");
        Assert.Contains("membros", deviation, StringComparison.Ordinal);
        await Task.Delay(500);
        Assert.Equal("loading", await rig.PhaseAsync()); // nunca promovido
        Assert.True(worker.App.Services.GetRequiredService<ConsolidationHealthState>().Has(DegradedReason.ReconciliationFailed));
    }

    [Fact(DisplayName = "R08a_StatusDistinguishesPendingReservedExpiredQuarantine_DependencyDownIsUnknownNotZero")]
    public async Task R08aStatusDistinguishesPendingReservedExpiredQuarantineDependencyDownIsUnknownNotZero()
    {
        await using var rig = await ConsolidationRig.CreateAsync();
        await rig.BringToLiveAsync();
        var source = LeaseSupport.NewSource(rig);

        // Massa conhecida: 4 pendentes (1 comum, 1 reservado válido, 1 reservado expirado, 1 adiado), 2 em quarentena e 1 confirmado.
        var plain = await LeaseSupport.InsertEventAsync(rig);
        var reserved = await LeaseSupport.InsertEventAsync(rig);
        var expired = await LeaseSupport.InsertEventAsync(rig);
        var deferred = await LeaseSupport.InsertEventAsync(rig);
        await LeaseSupport.InsertEventAsync(rig, type: 9, status: 2, attempts: 5, lastError: QuarantineError);
        await LeaseSupport.InsertEventAsync(rig, type: 9, status: 2, attempts: 5, lastError: QuarantineError);
        await LeaseSupport.InsertEventAsync(rig, status: 1);
        await rig.OriginExecuteAsync("UPDATE consolidation_outbox SET next_eligible_at = statement_timestamp() + interval '1 hour' WHERE event_id = @id", ("id", deferred));
        await rig.OriginExecuteAsync(
            """
            UPDATE consolidation_outbox SET lease_owner = 'w-x', lease_token = @t, lease_expires_at = statement_timestamp() + interval '1 hour' WHERE event_id = @r;
            UPDATE consolidation_outbox SET lease_owner = 'w-y', lease_token = @t2, lease_expires_at = statement_timestamp() - interval '1 minute' WHERE event_id = @e
            """,
            ("t", Guid.NewGuid()),
            ("r", reserved),
            ("t2", Guid.NewGuid()),
            ("e", expired));
        _ = (plain, source);

        var reader = new ConsolidationStatusReader(new TestConnectionFactory(rig.Real()));
        var (report, exit) = await reader.ReadAsync(CancellationToken.None);
        var json = JsonSerializer.SerializeToElement(report, AdminJson.Options);
        var origin = json.GetProperty("origin");
        Assert.Equal(AdminExitCodes.Success, exit);
        Assert.Equal("ok", origin.GetProperty("state").GetString());
        Assert.Equal(4, origin.GetProperty("pending").GetInt64());
        Assert.Equal(1, origin.GetProperty("reservedValid").GetInt64());
        Assert.Equal(1, origin.GetProperty("expiredEligible").GetInt64());
        Assert.Equal(2, origin.GetProperty("quarantined").GetInt64());
        Assert.Equal(7, origin.GetProperty("outboxRows").GetInt64());
        Assert.True(origin.GetProperty("oldestPendingAgeSeconds").GetDouble() >= 0);
        Assert.True(origin.GetProperty("outboxBytes").GetInt64() > 0);
        var projection = json.GetProperty("projection");
        Assert.Equal("live", projection.GetProperty("phase").GetString());
        Assert.Equal(rig.ProjectionDatabase, projection.GetProperty("database").GetString());
        output.WriteLine($"R08a {json}");

        // Origem inalcançável: estado desconhecido e saída própria, nunca contagem zero; a projeção, alcançável, continua informada.
        var down = new TestConnectionFactory(rig.Real());
        down.RedirectOrigin(ConsolidationConnectionRole.Health, TestConnectionFactory.Unreachable);
        var (downReport, downExit) = await new ConsolidationStatusReader(down).ReadAsync(CancellationToken.None);
        var downJson = JsonSerializer.SerializeToElement(downReport, AdminJson.Options);
        Assert.Equal(AdminExitCodes.Unreachable, downExit);
        Assert.Equal("unknown", downJson.GetProperty("origin").GetProperty("state").GetString());
        Assert.Equal(JsonValueKind.Null, downJson.GetProperty("origin").GetProperty("pending").ValueKind);
        Assert.Equal("ok", downJson.GetProperty("projection").GetProperty("state").GetString());

        // O comando do executável usa a mesma leitura e devolve o mesmo código.
        var writer = new StringWriter();
        var cliExit = await WorkerAdmin.RunAsync("--status", [], writer, builder => ConfigureConnections(builder, rig, down));
        Assert.Equal(AdminExitCodes.Unreachable, cliExit);
        Assert.Equal("unknown", Json(writer).GetProperty("origin").GetProperty("state").GetString());
        Assert.DoesNotContain("Password", writer.ToString(), StringComparison.Ordinal);
    }

    [Fact(DisplayName = "R08b_InstanceDiagnostics_OpaqueId_SeparateCounters_NoSecrets_DistinctPerInstance")]
    public async Task R08bInstanceDiagnosticsOpaqueIdSeparateCountersNoSecretsDistinctPerInstance()
    {
        await using var rig = await ConsolidationRig.CreateAsync();
        await rig.BringToLiveAsync();
        var worker1 = await rig.StartWorkerAsync();
        var worker2 = await rig.StartWorkerAsync();
        var at = DateTimeOffset.UtcNow.AddMinutes(-1);
        for (var i = 0; i < 8; i++)
        {
            await rig.PostOkAsync(CustomerA, A1, "Credit", 1.00m, at);
        }

        await ConsolidationRig.Eventually(async () => await rig.OutboxCountAsync(1) == 8, "oito confirmados");
        var (status1, body1) = await worker1.GetAsync("/diagnostics/instance");
        var (_, body2) = await worker2.GetAsync("/diagnostics/instance");
        Assert.Equal(System.Net.HttpStatusCode.OK, status1);
        var d1 = JsonDocument.Parse(body1).RootElement;
        var d2 = JsonDocument.Parse(body2).RootElement;
        Assert.StartsWith("w-", d1.GetProperty("instanceId").GetString(), StringComparison.Ordinal);
        Assert.NotEqual(d1.GetProperty("instanceId").GetString(), d2.GetProperty("instanceId").GetString());
        Assert.DoesNotContain(Environment.MachineName, body1, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Environment.UserName, body1, StringComparison.OrdinalIgnoreCase);
        foreach (var forbidden in new[] { "Host=", "Password", "Bearer", "idempotency" })
        {
            Assert.DoesNotContain(forbidden, body1, StringComparison.OrdinalIgnoreCase);
        }

        var applied = d1.GetProperty("counters").GetProperty("appliedNew").GetInt64() + d2.GetProperty("counters").GetProperty("appliedNew").GetInt64();
        var acquisitions = d1.GetProperty("counters").GetProperty("acquisitions").GetInt64() + d2.GetProperty("counters").GetProperty("acquisitions").GetInt64();
        Assert.Equal(8, applied); // aplicações novas somam os movimentos distintos
        Assert.Equal(8, acquisitions);
        output.WriteLine($"R08b instância 1: {body1.ReplaceLineEndings(" ")}");
    }
}
