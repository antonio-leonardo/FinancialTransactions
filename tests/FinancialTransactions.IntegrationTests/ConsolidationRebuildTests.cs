using System.Net;
using FinancialTransactions.Consolidation;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit.Abstractions;

namespace FinancialTransactions.IntegrationTests;

/// <summary>Pontos de pausa e de falha da reconstrução, programáveis por teste.</summary>
public sealed class ScriptedRebuildPoints : IRebuildFailurePoints
{
    public Func<CancellationToken, Task>? OnSnapshot { get; set; }

    public Func<CancellationToken, Task>? AfterCopy { get; set; }

    public Func<CancellationToken, Task>? AfterSeal { get; set; }

    public Func<long, CancellationToken, Task>? AfterReplayItem { get; set; }

    /// <summary>Ponto nomeado (<see cref="RebuildPoints"/>): recebe o nome e decide se pausa, falha ou intervém.</summary>
    public Func<string, CancellationToken, Task>? OnPoint { get; set; }

    public async ValueTask ReachedAsync(string point, CancellationToken cancellationToken)
    {
        if (OnPoint is { } hook)
        {
            await hook(point, cancellationToken);
        }
    }

    public async ValueTask AfterSnapshotEstablishedAsync(CancellationToken cancellationToken)
    {
        if (OnSnapshot is { } hook)
        {
            await hook(cancellationToken);
        }
    }

    public async ValueTask AfterCaptureCopyBeforeSealAsync(CancellationToken cancellationToken)
    {
        if (AfterCopy is { } hook)
        {
            await hook(cancellationToken);
        }
    }

    public async ValueTask AfterSealCommitAsync(CancellationToken cancellationToken)
    {
        if (AfterSeal is { } hook)
        {
            await hook(cancellationToken);
        }
    }

    public async ValueTask AfterReplayItemAsync(long appliedInThisRun, CancellationToken cancellationToken)
    {
        if (AfterReplayItem is { } hook)
        {
            await hook(appliedInThisRun, cancellationToken);
        }
    }
}

/// <summary>R10 a R13: reconstrução em banco novo paralelo (captura selada, replay idempotente, reconciliação de R, habilitação e promoção).</summary>
public sealed class ConsolidationRebuildTests(ITestOutputHelper output)
{
    private static RebuildRequest Request(string operation, RebuildTarget target, ConsolidationRig? rig = null) => new(operation, target.Database, rig?.ProjectionDatabase);

    private static async Task<T> TargetScalarAsync<T>(RebuildTarget target, string sql)
    {
        await using var connection = new NpgsqlConnection(target.Connection);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        var result = await command.ExecuteScalarAsync();
        return result is T typed ? typed : (T)Convert.ChangeType(result!, typeof(T), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task<HashSet<Guid>> TargetGuidsAsync(RebuildTarget target, string sql)
    {
        await using var connection = new NpgsqlConnection(target.Connection);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        var set = new HashSet<Guid>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            set.Add(reader.GetGuid(0));
        }

        return set;
    }

    private static Task<string> StateAsync(RebuildTarget target) => TargetScalarAsync<string>(target, "SELECT state FROM rebuild_operation WHERE id = 1");

    private static async Task RunToEnabledAsync(RebuildCoordinator rebuild, RebuildRequest request)
    {
        Assert.Equal(AdminExitCodes.Success, (await rebuild.CaptureAsync(request, CancellationToken.None)).ExitCode);
        Assert.Equal(AdminExitCodes.Success, (await rebuild.ReplayAsync(request, CancellationToken.None)).ExitCode);
        var reconciled = await rebuild.ReconcileAsync(request, CancellationToken.None);
        Assert.True(reconciled.ExitCode == AdminExitCodes.Success, System.Text.Json.JsonSerializer.Serialize(reconciled.Body, AdminJson.Options));
        Assert.Equal(AdminExitCodes.Success, (await rebuild.EnableAsync(request, CancellationToken.None)).ExitCode);
    }

    private static string BodyOf(RebuildOutcome outcome) => System.Text.Json.JsonSerializer.Serialize(outcome.Body, AdminJson.Options);

    [Fact(DisplayName = "R10a_CaptureInterruptedBeforeTheSealCommit_NoSealedSet_RecaptureDeclaredAsNew")]
    public async Task R10aCaptureInterruptedBeforeTheSealCommitNoSealedSetRecaptureDeclaredAsNew()
    {
        await using var rig = await ConsolidationRig.CreateAsync();
        await RebuildSeed.ConvergeAsync(rig);
        var target = await rig.CreateRebuildTargetAsync();
        var request = Request("op-r10a", target);
        var points = new ScriptedRebuildPoints { AfterCopy = _ => throw new InvalidOperationException("queda antes do commit da selagem") };

        await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Rebuilder(target, points).CaptureAsync(request, CancellationToken.None));

        Assert.Equal(RebuildStates.Prepared, await StateAsync(target)); // não existe conjunto concluído
        Assert.Equal(0, await TargetScalarAsync<long>(target, "SELECT COUNT(*) FROM rebuild_capture_item")); // a cópia estava na transação que não confirmou
        Assert.Null(await TargetScalarAsync<string?>(target, "SELECT capture_digest FROM rebuild_operation WHERE id = 1") is { Length: > 0 } digest ? digest : null);

        var retry = await rig.Rebuilder(target).CaptureAsync(request, CancellationToken.None); // nova captura, em visão nova
        Assert.Equal(AdminExitCodes.Success, retry.ExitCode);
        Assert.Equal(RebuildStates.Captured, await StateAsync(target));
        Assert.Equal(RebuildSeed.Plan.Length, await TargetScalarAsync<long>(target, "SELECT COUNT(*) FROM rebuild_capture_item"));
        output.WriteLine($"R10a depois da recaptura: {BodyOf(retry)}");
    }

    [Fact(DisplayName = "R10b_CaptureInterruptedAfterTheSealCommit_RerunResumesTheSameSet_NeverReplacesIt")]
    public async Task R10bCaptureInterruptedAfterTheSealCommitRerunResumesTheSameSetNeverReplacesIt()
    {
        await using var rig = await ConsolidationRig.CreateAsync();
        var at = await RebuildSeed.ConvergeAsync(rig);
        var target = await rig.CreateRebuildTargetAsync();
        var request = Request("op-r10b", target);
        var points = new ScriptedRebuildPoints { AfterSeal = _ => throw new InvalidOperationException("queda depois do commit") };

        await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Rebuilder(target, points).CaptureAsync(request, CancellationToken.None));

        Assert.Equal(RebuildStates.Captured, await StateAsync(target));
        var digest = await TargetScalarAsync<string>(target, "SELECT capture_digest FROM rebuild_operation WHERE id = 1");
        var snapshot = await TargetScalarAsync<string>(target, "SELECT capture_snapshot FROM rebuild_operation WHERE id = 1");
        var items = await TargetGuidsAsync(target, "SELECT event_id FROM rebuild_capture_item");
        Assert.Equal(RebuildSeed.Plan.Length, items.Count);

        // Novo movimento depois da selagem: a origem agora tem um a mais, mas R não muda.
        var later = await rig.PostOkAsync(RebuildSeed.CustomerA, RebuildSeed.A1, "Credit", 1.00m, at);
        var resumed = await rig.Rebuilder(target).CaptureAsync(request, CancellationToken.None);
        Assert.Equal(AdminExitCodes.Success, resumed.ExitCode);
        Assert.Contains("\"resumed\": true", BodyOf(resumed), StringComparison.Ordinal);
        Assert.Equal(digest, await TargetScalarAsync<string>(target, "SELECT capture_digest FROM rebuild_operation WHERE id = 1"));
        Assert.Equal(snapshot, await TargetScalarAsync<string>(target, "SELECT capture_snapshot FROM rebuild_operation WHERE id = 1"));
        Assert.Equal(items, await TargetGuidsAsync(target, "SELECT event_id FROM rebuild_capture_item"));
        Assert.DoesNotContain(later, items);
    }

    [Fact(DisplayName = "R10c_EmptySet_IsCapturedSealedReplayedAndReconciled")]
    public async Task R10cEmptySetIsCapturedSealedReplayedAndReconciled()
    {
        await using var rig = await ConsolidationRig.CreateAsync();
        await rig.BringToLiveAsync(); // coordenador com conjunto vazio: registro de controle com zero membros
        NpgsqlConnection.ClearAllPools(); // sessões ociosas do Worker de preparação não contam como consumidores conectados
        var target = await rig.CreateRebuildTargetAsync();
        var request = Request("op-r10c", target);
        var rebuild = rig.Rebuilder(target);

        await RunToEnabledAsync(rebuild, request);

        Assert.Equal(0, await TargetScalarAsync<long>(target, "SELECT capture_count FROM rebuild_operation WHERE id = 1"));
        Assert.Equal("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855", await TargetScalarAsync<string>(target, "SELECT capture_digest FROM rebuild_operation WHERE id = 1")); // SHA-256 do texto vazio
        Assert.Equal(RebuildStates.Enabled, await StateAsync(target));
        Assert.Equal(0, await TargetScalarAsync<long>(target, "SELECT member_count FROM consolidation_bootstrap WHERE id = 1")); // descritor histórico, não a contagem de R
    }

    [Fact(DisplayName = "R10d_MassCeilingIsValidatedBeforeTheCopy_AboveItTheCaptureIsRefusedNotTruncated")]
    public async Task R10dMassCeilingIsValidatedBeforeTheCopyAboveItTheCaptureIsRefusedNotTruncated()
    {
        await using var rig = await ConsolidationRig.CreateAsync();
        await RebuildSeed.ConvergeAsync(rig);
        var target = await rig.CreateRebuildTargetAsync();
        var request = Request("op-r10d", target);

        var refused = await rig.Rebuilder(target, movementCeiling: RebuildSeed.Plan.Length - 1).CaptureAsync(request, CancellationToken.None);

        Assert.Equal(AdminExitCodes.Conflict, refused.ExitCode);
        Assert.Contains("excedem o teto declarado", BodyOf(refused), StringComparison.Ordinal);
        Assert.Equal(0, await TargetScalarAsync<long>(target, "SELECT COUNT(*) FROM rebuild_operation")); // nada foi preparado nem copiado
        Assert.Equal(0, await TargetScalarAsync<long>(target, "SELECT COUNT(*) FROM rebuild_capture_item"));

        var exact = await rig.Rebuilder(target, movementCeiling: RebuildSeed.Plan.Length).CaptureAsync(request, CancellationToken.None); // no teto exato, captura
        Assert.Equal(AdminExitCodes.Success, exact.ExitCode);
    }

    [Fact(DisplayName = "R11a_ReplayFailsAfterACommit_ResumeAppliesOnlyTheRest_NoDuplicates_OriginOutboxUntouched")]
    public async Task R11aReplayFailsAfterACommitResumeAppliesOnlyTheRestNoDuplicatesOriginOutboxUntouched()
    {
        await using var rig = await ConsolidationRig.CreateAsync();
        await RebuildSeed.ConvergeAsync(rig);
        var target = await rig.CreateRebuildTargetAsync();
        var request = Request("op-r11a", target);
        Assert.Equal(AdminExitCodes.Success, (await rig.Rebuilder(target).CaptureAsync(request, CancellationToken.None)).ExitCode);
        var originBefore = await RebuildSeed.TablesSnapshotAsync(rig);

        var points = new ScriptedRebuildPoints { AfterReplayItem = (applied, _) => applied == 3 ? throw new InvalidOperationException("queda depois do 3.º commit") : Task.CompletedTask };
        await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Rebuilder(target, points).ReplayAsync(request, CancellationToken.None));

        Assert.Equal(3, await TargetScalarAsync<long>(target, "SELECT COUNT(*) FROM processed_events")); // os três commits ficaram
        var firstThree = await TargetScalarAsync<string>(target, "SELECT string_agg(event_id::text || ':' || xmin::text, ',' ORDER BY event_id) FROM processed_events");
        Assert.Equal(RebuildStates.Captured, await StateAsync(target)); // o replay não terminou

        var resumed = await rig.Rebuilder(target).ReplayAsync(request, CancellationToken.None);
        Assert.Equal(AdminExitCodes.Success, resumed.ExitCode);
        Assert.Contains("\"appliedInThisRun\": 3", BodyOf(resumed), StringComparison.Ordinal); // só o que faltava
        Assert.Equal(RebuildSeed.Plan.Length, await TargetScalarAsync<long>(target, "SELECT COUNT(*) FROM processed_events"));
        Assert.Equal(0, await TargetScalarAsync<long>(target, "SELECT COUNT(*) - COUNT(DISTINCT event_id) FROM processed_events"));
        foreach (var pair in firstThree.Split(','))
        {
            Assert.Contains(pair, await TargetScalarAsync<string>(target, "SELECT string_agg(event_id::text || ':' || xmin::text, ',') FROM processed_events"), StringComparison.Ordinal); // não reaplicados
        }

        Assert.Equal(AdminExitCodes.Success, (await rig.Rebuilder(target).ReconcileAsync(request, CancellationToken.None)).ExitCode);
        Assert.Equal(originBefore, await RebuildSeed.TablesSnapshotAsync(rig)); // o replay não tocou na origem (status, processed_at, xmin)
        Assert.True((await rig.Verifier(target.Connection).VerifyAsync(VerifyMode.Rebuild, CancellationToken.None)).Consistent);
    }

    [Fact(DisplayName = "R11b_ReplayFailsInsideTheItemTransaction_ItemRolledBack_ResumeAppliesItOnce")]
    public async Task R11bReplayFailsInsideTheItemTransactionItemRolledBackResumeAppliesItOnce()
    {
        await using var rig = await ConsolidationRig.CreateAsync();
        await RebuildSeed.ConvergeAsync(rig);
        var target = await rig.CreateRebuildTargetAsync();
        var request = Request("op-r11b", target);
        Assert.Equal(AdminExitCodes.Success, (await rig.Rebuilder(target).CaptureAsync(request, CancellationToken.None)).ExitCode);

        var applier = new TestConsolidationFailurePoints();
        applier.InsideProjectionTransactionOnce(_ => throw new InvalidOperationException("queda dentro da transação do item"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Rebuilder(target, applierPoints: applier).ReplayAsync(request, CancellationToken.None));

        Assert.Equal(0, await TargetScalarAsync<long>(target, "SELECT COUNT(*) FROM processed_events")); // marcador e agregado vivem ou morrem juntos
        Assert.Equal(0, await TargetScalarAsync<long>(target, "SELECT COUNT(*) FROM daily_account_consolidation"));

        Assert.Equal(AdminExitCodes.Success, (await rig.Rebuilder(target).ReplayAsync(request, CancellationToken.None)).ExitCode);
        Assert.Equal(RebuildSeed.Plan.Length, await TargetScalarAsync<long>(target, "SELECT COUNT(*) FROM processed_events"));
        Assert.Equal(AdminExitCodes.Success, (await rig.Rebuilder(target).ReconcileAsync(request, CancellationToken.None)).ExitCode);
    }

    [Fact(DisplayName = "R12_RebuildWithConfirmedPendingAndLateEvents_PostsAcceptedDuringCaptureAndReplay_RStable_LaterEventsArriveAfterPromotion")]
    public async Task R12RebuildWithConfirmedPendingAndLateEventsPostsAcceptedDuringCaptureAndReplayRStableLaterEventsArriveAfterPromotion()
    {
        await using var rig = await ConsolidationRig.CreateAsync();
        var at = await RebuildSeed.ConvergeAsync(rig);

        // Informação tardia e eventos que ainda estão pendentes na origem (nenhum consumidor rodando): entram em R, mas continuam status 0.
        var lateInformation = await rig.PostOkAsync(RebuildSeed.CustomerA, RebuildSeed.A1, "Credit", 6.00m, at.AddDays(-5));
        var pendingInR = await rig.PostOkAsync(RebuildSeed.CustomerA, RebuildSeed.A2, "Debit", 2.00m, at);
        Assert.Equal(2, await rig.OutboxCountAsync(0));

        var target = await rig.CreateRebuildTargetAsync();
        var request = Request("op-r12", target, rig);
        var gate = new Gate();
        var points = new ScriptedRebuildPoints { OnSnapshot = ct => gate.PassAsync(ct).AsTask() };
        var rebuild = rig.Rebuilder(target, points);

        var capture = Task.Run(() => rebuild.CaptureAsync(request, CancellationToken.None));
        await gate.Reached.WaitAsync(TimeSpan.FromSeconds(30));
        var expectedR = (await rig.OriginGuidsAsync("SELECT id FROM movements")).ToHashSet();
        var duringCapture = await rig.PostOkAsync(RebuildSeed.CustomerA, RebuildSeed.A1, "Credit", 1.50m, at); // lançamento continua sendo aceito
        gate.Release();
        Assert.Equal(AdminExitCodes.Success, (await capture).ExitCode);

        var r = await TargetGuidsAsync(target, "SELECT event_id FROM rebuild_capture_item");
        Assert.Equal(expectedR, r); // R estável: exatamente o que era visível no snapshot
        Assert.DoesNotContain(duringCapture, r);
        Assert.Contains(lateInformation, r);
        Assert.Contains(pendingInR, r);

        // Replay com POST no meio: aceito, e fora de R.
        Guid duringReplay = Guid.Empty;
        points.AfterReplayItem = async (applied, _) =>
        {
            if (applied == 2)
            {
                duringReplay = await rig.PostOkAsync(RebuildSeed.CustomerA, RebuildSeed.A2, "Credit", 3.00m, at);
            }
        };
        Assert.Equal(AdminExitCodes.Success, (await rebuild.ReplayAsync(request, CancellationToken.None)).ExitCode);
        Assert.NotEqual(Guid.Empty, duringReplay);
        Assert.DoesNotContain(duringReplay, r);
        Assert.Equal(AdminExitCodes.Success, (await rebuild.ReconcileAsync(request, CancellationToken.None)).ExitCode);
        Assert.Equal(AdminExitCodes.Success, (await rebuild.EnableAsync(request, CancellationToken.None)).ExitCode);

        // Habilitado e ainda NÃO promovido: um Worker apontado para o destino novo não consome nada.
        var targetFactory = new TestConnectionFactory(rig.RealWith(target.Connection, ConsolidationApplicationNames.Worker));
        var early = await rig.StartWorkerAsync(new WorkerStart { Factory = targetFactory });
        await Task.Delay(TimeSpan.FromSeconds(1.5));
        Assert.Equal(r.Count, await TargetScalarAsync<long>(target, "SELECT COUNT(*) FROM processed_events"));
        Assert.True(early.App.Services.GetRequiredService<ConsolidationHealthState>().Has(DegradedReason.RebuildInProgress));
        Assert.True(await early.StopAsync(TimeSpan.FromSeconds(10)));
        await early.DisposeAsync();
        NpgsqlConnection.ClearAllPools();

        var promoted = await rebuild.PromoteAsync(request, CancellationToken.None);
        Assert.True(promoted.ExitCode == AdminExitCodes.Success, BodyOf(promoted));

        // Depois da promoção os eventos posteriores a R chegam; os de R ainda pendentes na origem são deduplicados no destino novo.
        var worker = await rig.StartWorkerAsync(new WorkerStart { Factory = targetFactory });
        await ConsolidationRig.Eventually(async () => await rig.OutboxCountAsync(0) == 0, "pendentes da origem confirmados pelo Worker do destino novo");
        var counters = LeaseSupport.CountersOf(worker).Snapshot();
        output.WriteLine($"R12 contadores do Worker novo: {counters}");
        Assert.Equal(2, counters.DuplicatesAvoided); // informação tardia e o pendente, ambos já em R
        Assert.Equal(2, counters.AppliedNew); // durante a captura e durante o replay
        Assert.Equal(r.Count + 2, await TargetScalarAsync<long>(target, "SELECT COUNT(*) FROM processed_events"));
        Assert.True((await rig.Verifier(target.Connection).VerifyAsync(VerifyMode.Live, CancellationToken.None)).Consistent);
        Assert.True(await worker.StopAsync(TimeSpan.FromSeconds(10)));
        await worker.DisposeAsync();
        NpgsqlConnection.ClearAllPools();
        var convergence = await rig.Verifier(target.Connection).VerifyAsync(VerifyMode.Convergence, CancellationToken.None);
        Assert.True(convergence.Consistent, string.Join("; ", convergence.Deviations.Select(d => d.Kind + ": " + d.Detail)));
    }

    [Fact(DisplayName = "R13a_WrongTargetOriginAsTargetAndActiveProjection_AreRefused_PromoteBeforeReconcileRefused")]
    public async Task R13aWrongTargetOriginAsTargetAndActiveProjectionAreRefusedPromoteBeforeReconcileRefused()
    {
        await using var rig = await ConsolidationRig.CreateAsync();
        await RebuildSeed.ConvergeAsync(rig);
        var target = await rig.CreateRebuildTargetAsync();
        var before = await RebuildSeed.TablesSnapshotAsync(rig);

        // Alvo errado: a confirmação não corresponde ao banco configurado.
        var wrong = await rig.Rebuilder(target).CaptureAsync(new RebuildRequest("op-r13a", rig.ProjectionDatabase), CancellationToken.None);
        Assert.Equal(AdminExitCodes.Refused, wrong.ExitCode);
        Assert.Equal(0, await TargetScalarAsync<long>(target, "SELECT COUNT(*) FROM rebuild_operation"));

        // Origem como destino.
        var originAsTarget = rig.Rebuilder(new RebuildTarget(rig.OriginDatabase, rig.OriginConnection));
        var plan = await originAsTarget.PlanAsync(new RebuildRequest("op-r13a", rig.OriginDatabase), CancellationToken.None);
        Assert.Equal(AdminExitCodes.Conflict, plan.ExitCode);
        Assert.Contains("origem não pode ser o destino", BodyOf(plan), StringComparison.Ordinal);

        // Projeção ativa por engano (já tem dados): recusada, nada gravado.
        var active = rig.Rebuilder(new RebuildTarget(rig.ProjectionDatabase, rig.ProjectionConnection));
        var activeOutcome = await active.CaptureAsync(new RebuildRequest("op-r13a", rig.ProjectionDatabase), CancellationToken.None);
        Assert.Equal(AdminExitCodes.Conflict, activeOutcome.ExitCode);
        Assert.Equal(0, await rig.ProjectionScalarAsync<long>("SELECT COUNT(*) FROM rebuild_operation"));
        Assert.Equal(before, await RebuildSeed.TablesSnapshotAsync(rig));

        // Promoção antes de reconciliar.
        var request = Request("op-r13a", target, rig);
        var rebuild = rig.Rebuilder(target);
        Assert.Equal(AdminExitCodes.Success, (await rebuild.CaptureAsync(request, CancellationToken.None)).ExitCode);
        var early = await rebuild.PromoteAsync(request, CancellationToken.None);
        Assert.Equal(AdminExitCodes.Conflict, early.ExitCode);
        Assert.Contains("habilitado e reconciliado", BodyOf(early), StringComparison.Ordinal);
        Assert.Equal(AdminExitCodes.Conflict, (await rebuild.EnableAsync(request, CancellationToken.None)).ExitCode); // nem habilita sem reconciliar
        Assert.Equal(0, await rig.OriginScalarAsync<long>("SELECT COUNT(*) FROM consolidation_projection_registry"));
    }

    [Fact(DisplayName = "R13b_PromotionRefusedWhileKnownConsumersAreConnected_ThenOldProjectionIsNeitherConsumedNorServed")]
    public async Task R13bPromotionRefusedWhileKnownConsumersAreConnectedThenOldProjectionIsNeitherConsumedNorServed()
    {
        await using var rig = await ConsolidationRig.CreateAsync();
        var at = await RebuildSeed.ConvergeAsync(rig);
        var target = await rig.CreateRebuildTargetAsync();
        var request = Request("op-r13b", target, rig);
        var rebuild = rig.Rebuilder(target);
        await RunToEnabledAsync(rebuild, request);

        // Um Worker antigo ainda conectado à origem e ao banco antigo: a promoção é recusada e nada muda.
        var oldWorker = await rig.StartWorkerAsync();
        await Task.Delay(TimeSpan.FromSeconds(1));
        var refused = await rebuild.PromoteAsync(request, CancellationToken.None);
        Assert.Equal(AdminExitCodes.Conflict, refused.ExitCode);
        Assert.Contains("sessão(ões) de Worker/API", BodyOf(refused), StringComparison.Ordinal);
        Assert.Equal(0, await rig.OriginScalarAsync<long>("SELECT COUNT(*) FROM consolidation_projection_registry"));
        Assert.Equal(RebuildStates.Enabled, await StateAsync(target));

        Assert.True(await oldWorker.StopAsync(TimeSpan.FromSeconds(10)));
        await oldWorker.DisposeAsync();
        NpgsqlConnection.ClearAllPools();
        var promoted = await rebuild.PromoteAsync(request, CancellationToken.None);
        Assert.True(promoted.ExitCode == AdminExitCodes.Success, BodyOf(promoted));
        Assert.Contains("NÃO é um retorno seguro", BodyOf(promoted), StringComparison.Ordinal); // sem rollback ingênuo anunciado
        Assert.Equal(RebuildStates.Promoted, await StateAsync(target));
        Assert.Equal(1, await rig.OriginScalarAsync<long>("SELECT COUNT(*) FROM consolidation_projection_registry"));
        var repeat = await rebuild.PromoteAsync(request, CancellationToken.None);
        Assert.Equal(AdminExitCodes.Success, repeat.ExitCode); // idempotente

        // Voltar ao banco antigo trocando a connection string: não consome, e a API recusa servir.
        var pending = await rig.PostOkAsync(RebuildSeed.CustomerA, RebuildSeed.A1, "Credit", 5.00m, at);
        var processedBefore = await rig.ProcessedCountAsync();
        var back = await rig.StartWorkerAsync();
        await Task.Delay(TimeSpan.FromSeconds(1.5));
        Assert.Equal(processedBefore, await rig.ProcessedCountAsync());
        Assert.Equal((short)0, await rig.OriginScalarAsync<short>("SELECT status FROM consolidation_outbox WHERE event_id = @id", ("id", pending)));
        Assert.True(back.App.Services.GetRequiredService<ConsolidationHealthState>().Has(DegradedReason.ProjectionSuperseded));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await back.GetAsync("/health/ready")).Status);

        var day = BusinessDay.Of(at);
        var oldApi = await rig.StartConsolidationApiAsync();
        var (oldStatus, oldBody) = await oldApi.GetAccountAsync(RebuildSeed.CustomerA, RebuildSeed.A1, day.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, oldStatus);
        Assert.Contains("projection_superseded", oldBody, StringComparison.Ordinal);

        // A projeção reconstruída e promovida é servida e consumida normalmente.
        var newApi = await rig.StartConsolidationApiAsync(new TestConnectionFactory(rig.RealWith(target.Connection, ConsolidationApplicationNames.Api, 1, 1)));
        var (newStatus, newBody) = await newApi.GetAccountAsync(RebuildSeed.CustomerA, RebuildSeed.A1, day.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(HttpStatusCode.OK, newStatus);
        var daily = ConsolidationRig.Parse<DailyDto>(newBody);
        Assert.Equal(10.00m, daily.Credits);
        Assert.Equal(3.25m, daily.Debits);
        output.WriteLine($"R13b API antiga: {(int)oldStatus}; API nova: {(int)newStatus}; estado da guarda: superseded");
    }

    /// <summary>Pausa o comando num ponto nomeado até o teste liberar; o teste sincroniza pelo sinal "alcançado", nunca por tempo.</summary>
    private sealed class PointGate(string point)
    {
        private readonly TaskCompletionSource _reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Reached => _reached.Task;

        public void Release() => _release.TrySetResult();

        public async Task OnPointAsync(string reachedPoint, CancellationToken cancellationToken)
        {
            if (reachedPoint == point)
            {
                _reached.TrySetResult();
                await _release.Task.WaitAsync(cancellationToken);
            }
        }
    }

    private static readonly TimeSpan SyncTimeout = TimeSpan.FromSeconds(60);

    private static async Task<Guid> ProjectionIdAsync(RebuildTarget target) =>
        await TargetScalarAsync<Guid>(target, "SELECT projection_id FROM rebuild_operation WHERE id = 1");

    [Fact(DisplayName = "R13c_TwoCapturesWithDifferentLabels_OnlyTheOwnerOfTheOperationCompletes")]
    public async Task R13cTwoCapturesWithDifferentLabelsOnlyTheOwnerOfTheOperationCompletes()
    {
        await using var rig = await ConsolidationRig.CreateAsync();
        await RebuildSeed.ConvergeAsync(rig);
        var target = await rig.CreateRebuildTargetAsync();
        var requestA = Request("op-a-r13c", target);
        var requestB = Request("op-b-r13c", target);
        var gate = new PointGate(RebuildPoints.CaptureBeforePrepare);

        // A leu "operação ausente" e para antes de preparar; B e C, de outras identidades, tentam o mesmo alvo.
        var a = rig.Rebuilder(target, new ScriptedRebuildPoints { OnPoint = gate.OnPointAsync }).CaptureAsync(requestA, CancellationToken.None);
        try
        {
            await gate.Reached.WaitAsync(SyncTimeout);
            var b = await rig.Rebuilder(target).CaptureAsync(requestB, CancellationToken.None).WaitAsync(SyncTimeout);
            Assert.True(b.ExitCode == AdminExitCodes.Conflict, $"B não pode concluir a operação de A: {BodyOf(b)}");
            Assert.Contains("exclusiv", BodyOf(b), StringComparison.Ordinal);
            var c = await rig.Rebuilder(target).CaptureAsync(Request("op-c-r13c", target), CancellationToken.None).WaitAsync(SyncTimeout);
            Assert.Equal(AdminExitCodes.Conflict, c.ExitCode); // a recusa de B não liberou a exclusividade de A
            Assert.Equal(0, await TargetScalarAsync<long>(target, "SELECT COUNT(*) FROM rebuild_operation")); // nada foi preparado por quem perdeu
        }
        finally
        {
            gate.Release();
        }

        var outcome = await a.WaitAsync(SyncTimeout);
        Assert.True(outcome.ExitCode == AdminExitCodes.Success, BodyOf(outcome));
        Assert.Equal("op-a-r13c", await TargetScalarAsync<string>(target, "SELECT operation_label FROM rebuild_operation WHERE id = 1"));
        Assert.Equal(RebuildStates.Captured, await StateAsync(target));
        Assert.Equal(RebuildSeed.Plan.Length, await TargetScalarAsync<long>(target, "SELECT COUNT(*) FROM rebuild_capture_item"));
        var other = await rig.Rebuilder(target).CaptureAsync(requestB, CancellationToken.None); // liberada, a operação continua sendo de A
        Assert.Equal(AdminExitCodes.Conflict, other.ExitCode);
        Assert.Contains("outra operação", BodyOf(other), StringComparison.Ordinal);
    }

    [Fact(DisplayName = "R13d_CancelOrSecondReplayWhileAReplayRuns_IsRefused_AndTheReplayFinishesIntact")]
    public async Task R13dCancelOrSecondReplayWhileAReplayRunsIsRefusedAndTheReplayFinishesIntact()
    {
        await using var rig = await ConsolidationRig.CreateAsync();
        await RebuildSeed.ConvergeAsync(rig);
        var target = await rig.CreateRebuildTargetAsync();
        var request = Request("op-r13d", target, rig);
        Assert.Equal(AdminExitCodes.Success, (await rig.Rebuilder(target).CaptureAsync(request, CancellationToken.None)).ExitCode);

        var reachedReplay = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var points = new ScriptedRebuildPoints
        {
            AfterReplayItem = async (applied, ct) =>
            {
                if (applied == 2)
                {
                    reachedReplay.TrySetResult();
                    await release.Task.WaitAsync(ct);
                }
            },
        };
        var replay = rig.Rebuilder(target, points).ReplayAsync(request, CancellationToken.None);
        try
        {
            await reachedReplay.Task.WaitAsync(SyncTimeout);
            var cancel = await rig.Rebuilder(target).CancelAsync(request, CancellationToken.None).WaitAsync(SyncTimeout);
            Assert.True(cancel.ExitCode == AdminExitCodes.Conflict, $"cancelar no meio do replay de outro comando deve ser recusado: {BodyOf(cancel)}");
            var second = await rig.Rebuilder(target).ReplayAsync(request, CancellationToken.None).WaitAsync(SyncTimeout);
            Assert.Equal(AdminExitCodes.Conflict, second.ExitCode);
            var promote = await rig.Rebuilder(target).PromoteAsync(request, CancellationToken.None).WaitAsync(SyncTimeout);
            Assert.Equal(AdminExitCodes.Conflict, promote.ExitCode);
            Assert.Equal(RebuildStates.Captured, await StateAsync(target)); // ninguém mexeu na operação alheia
            Assert.Equal(2, await TargetScalarAsync<long>(target, "SELECT COUNT(*) FROM processed_events"));
        }
        finally
        {
            release.TrySetResult();
        }

        var outcome = await replay.WaitAsync(SyncTimeout);
        Assert.Equal(AdminExitCodes.Success, outcome.ExitCode);
        Assert.Equal(RebuildStates.Replayed, await StateAsync(target));
        Assert.Equal(RebuildSeed.Plan.Length, await TargetScalarAsync<long>(target, "SELECT COUNT(*) FROM processed_events"));
        Assert.Equal(0, await TargetScalarAsync<long>(target, "SELECT COUNT(*) - COUNT(DISTINCT event_id) FROM processed_events"));
    }

    [Fact(DisplayName = "R13e_CancelAndSecondPromoteWhilePromotionRuns_AreRefused_PromotionCompletes")]
    public async Task R13eCancelAndSecondPromoteWhilePromotionRunsAreRefusedPromotionCompletes()
    {
        await using var rig = await ConsolidationRig.CreateAsync();
        await RebuildSeed.ConvergeAsync(rig);
        var target = await rig.CreateRebuildTargetAsync();
        var request = Request("op-r13e", target, rig);
        await RunToEnabledAsync(rig.Rebuilder(target), request);
        var gate = new PointGate(RebuildPoints.PromoteAfterRegistry);

        var promotion = rig.Rebuilder(target, new ScriptedRebuildPoints { OnPoint = gate.OnPointAsync }).PromoteAsync(request, CancellationToken.None);
        try
        {
            await gate.Reached.WaitAsync(SyncTimeout); // registro da origem confirmado, marca local ainda não
            var cancel = await rig.Rebuilder(target).CancelAsync(request, CancellationToken.None).WaitAsync(SyncTimeout);
            Assert.True(cancel.ExitCode == AdminExitCodes.Conflict, $"cancelar durante a promoção deve ser recusado: {BodyOf(cancel)}");
            var second = await rig.Rebuilder(target).PromoteAsync(request, CancellationToken.None).WaitAsync(SyncTimeout);
            Assert.Equal(AdminExitCodes.Conflict, second.ExitCode);
            Assert.Equal(RebuildStates.Enabled, await StateAsync(target));
        }
        finally
        {
            gate.Release();
        }

        var outcome = await promotion.WaitAsync(SyncTimeout);
        Assert.True(outcome.ExitCode == AdminExitCodes.Success, BodyOf(outcome));
        Assert.Contains("\"promoted\": true", BodyOf(outcome), StringComparison.Ordinal);
        Assert.Equal(RebuildStates.Promoted, await StateAsync(target));
        Assert.Equal(await ProjectionIdAsync(target), await rig.OriginScalarAsync<Guid>("SELECT active_projection_id FROM consolidation_projection_registry WHERE id = 1"));
    }

    [Fact(DisplayName = "R13f_PromotionInterruptedAfterTheOriginRegistry_CancelIsRefused_ResumeCompletes_OldDatabaseStaysBlocked")]
    public async Task R13fPromotionInterruptedAfterTheOriginRegistryCancelIsRefusedResumeCompletesOldDatabaseStaysBlocked()
    {
        await using var rig = await ConsolidationRig.CreateAsync();
        await RebuildSeed.ConvergeAsync(rig);
        var target = await rig.CreateRebuildTargetAsync();
        var request = Request("op-r13f", target, rig);
        await RunToEnabledAsync(rig.Rebuilder(target), request);

        // Queda depois do commit na origem e antes da marca no destino.
        var crash = new ScriptedRebuildPoints { OnPoint = (point, _) => point == RebuildPoints.PromoteAfterRegistry ? throw new InvalidOperationException("queda entre os dois bancos") : Task.CompletedTask };
        await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Rebuilder(target, crash).PromoteAsync(request, CancellationToken.None));
        var projectionId = await ProjectionIdAsync(target);
        Assert.Equal(projectionId, await rig.OriginScalarAsync<Guid>("SELECT active_projection_id FROM consolidation_projection_registry WHERE id = 1"));
        Assert.Equal(RebuildStates.Enabled, await StateAsync(target));

        // Cancelar agora fingiria que a troca não ocorreu: recusado, estado intacto.
        var cancel = await rig.Rebuilder(target).CancelAsync(request, CancellationToken.None);
        Assert.True(cancel.ExitCode == AdminExitCodes.Conflict, BodyOf(cancel));
        Assert.Contains("promoção", BodyOf(cancel), StringComparison.Ordinal);
        Assert.Equal(RebuildStates.Enabled, await StateAsync(target));
        Assert.Equal(projectionId, await rig.OriginScalarAsync<Guid>("SELECT active_projection_id FROM consolidation_projection_registry WHERE id = 1"));

        // Retomada da mesma operação: completa a marca sem regravar o registro.
        var generationBefore = await rig.OriginScalarAsync<long>("SELECT generation FROM consolidation_projection_registry WHERE id = 1");
        var resumed = await rig.Rebuilder(target).PromoteAsync(request, CancellationToken.None);
        Assert.True(resumed.ExitCode == AdminExitCodes.Success, BodyOf(resumed));
        Assert.Contains("\"promoted\": true", BodyOf(resumed), StringComparison.Ordinal);
        Assert.Equal(RebuildStates.Promoted, await StateAsync(target));
        Assert.Equal(generationBefore, await rig.OriginScalarAsync<long>("SELECT generation FROM consolidation_projection_registry WHERE id = 1"));
        var repeat = await rig.Rebuilder(target).PromoteAsync(request, CancellationToken.None);
        Assert.Equal(AdminExitCodes.Success, repeat.ExitCode); // idempotente

        // O banco antigo continua bloqueado depois da retomada.
        var oldWorker = await rig.StartWorkerAsync();
        var health = oldWorker.App.Services.GetRequiredService<ConsolidationHealthState>();
        await ConsolidationRig.Eventually(() => Task.FromResult(health.Has(DegradedReason.ProjectionSuperseded)), "o Worker no banco antigo deve ser recusado como superado");
    }

    [Fact(DisplayName = "R13g_CancelOfAnEnabledOperationWithUnreadableOrigin_IsUnknown_NeverCancelled")]
    public async Task R13gCancelOfAnEnabledOperationWithUnreadableOriginIsUnknownNeverCancelled()
    {
        await using var rig = await ConsolidationRig.CreateAsync();
        await RebuildSeed.ConvergeAsync(rig);
        var target = await rig.CreateRebuildTargetAsync();
        var request = Request("op-r13g", target, rig);
        await RunToEnabledAsync(rig.Rebuilder(target), request);

        var unreadable = ConsolidationRig.RebuilderWithOrigin(target, new NpgsqlConnectionStringBuilder(rig.OriginConnection) { Port = 1, Timeout = 1 }.ConnectionString);
        var cancel = await unreadable.CancelAsync(request, CancellationToken.None);

        Assert.True(cancel.ExitCode == AdminExitCodes.Unreachable, $"sem ler a origem não se pode afirmar que a troca não ocorreu: {BodyOf(cancel)}");
        Assert.Equal(RebuildStates.Enabled, await StateAsync(target));

        var readable = await rig.Rebuilder(target).CancelAsync(request, CancellationToken.None); // com a origem legível e sem registro, cancelar é seguro
        Assert.Equal(AdminExitCodes.Success, readable.ExitCode);
        Assert.Equal(RebuildStates.Cancelled, await StateAsync(target));
    }

    [Fact(DisplayName = "R13h_ZeroRowsOnTheLocalPromotionMark_IsNeverAnnouncedAsPromoted")]
    public async Task R13hZeroRowsOnTheLocalPromotionMarkIsNeverAnnouncedAsPromoted()
    {
        await using var rig = await ConsolidationRig.CreateAsync();
        await RebuildSeed.ConvergeAsync(rig);
        var target = await rig.CreateRebuildTargetAsync();
        var request = Request("op-r13h", target, rig);
        await RunToEnabledAsync(rig.Rebuilder(target), request);

        // Intervenção declarada: SQL direto no destino (fora do coordenador) entre o registro na origem e a marca local.
        var points = new ScriptedRebuildPoints
        {
            OnPoint = async (point, ct) =>
            {
                if (point == RebuildPoints.PromoteAfterRegistry)
                {
                    await using var connection = new NpgsqlConnection(target.Connection);
                    await connection.OpenAsync(ct);
                    await using var command = new NpgsqlCommand("UPDATE rebuild_operation SET state = 'cancelled', cancelled_at = statement_timestamp() WHERE id = 1", connection);
                    await command.ExecuteNonQueryAsync(ct);
                }
            },
        };
        var outcome = await rig.Rebuilder(target, points).PromoteAsync(request, CancellationToken.None);

        Assert.True(outcome.ExitCode != AdminExitCodes.Success, $"zero linhas não comprova a transição: {BodyOf(outcome)}");
        Assert.DoesNotContain("\"promoted\": true", BodyOf(outcome), StringComparison.Ordinal);
        Assert.Equal(RebuildStates.Cancelled, await StateAsync(target));
    }

    [Fact(DisplayName = "R10e_CeilingIsRevalidatedInsideTheSnapshot_PostBetweenPreflightAndSnapshot_IsAcceptedAndCaptureRefused")]
    public async Task R10eCeilingIsRevalidatedInsideTheSnapshotPostBetweenPreflightAndSnapshotIsAcceptedAndCaptureRefused()
    {
        await using var rig = await ConsolidationRig.CreateAsync();
        var at = await RebuildSeed.ConvergeAsync(rig);
        var target = await rig.CreateRebuildTargetAsync();
        var request = Request("op-r10e", target);
        Guid? late = null;
        var points = new ScriptedRebuildPoints
        {
            OnPoint = async (point, ct) =>
            {
                if (point == RebuildPoints.CaptureBeforeSnapshot)
                {
                    late = await rig.PostOkAsync(RebuildSeed.CustomerA, RebuildSeed.A1, "Credit", 1.00m, at); // o lançamento legítimo é aceito
                }
            },
        };

        // O preflight passa no teto exato; o lançamento confirmado antes do snapshot faz a visão enumerada exceder o teto.
        var outcome = await rig.Rebuilder(target, points, movementCeiling: RebuildSeed.Plan.Length).CaptureAsync(request, CancellationToken.None);

        Assert.NotNull(late);
        Assert.Equal(1, await rig.OriginScalarAsync<long>("SELECT COUNT(*) FROM movements WHERE id = @id", ("id", late.GetValueOrDefault())));
        Assert.True(outcome.ExitCode == AdminExitCodes.Conflict, BodyOf(outcome));
        Assert.Contains("teto", BodyOf(outcome), StringComparison.Ordinal);
        Assert.Equal(RebuildStates.Prepared, await StateAsync(target)); // nenhum R selado
        Assert.Equal(0, await TargetScalarAsync<long>(target, "SELECT COUNT(*) FROM rebuild_capture_item")); // nenhum R parcial
        Assert.Null(await TargetScalarAsync<string?>(target, "SELECT capture_digest FROM rebuild_operation WHERE id = 1") is { Length: > 0 } digest ? digest : null);
    }

    private static async Task<long> ScalarOnAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    [Fact(DisplayName = "R13i_PartialPromotionWithAnAccessibleButWrongOrigin_CancelAndPromoteAreRefused_NothingChanges_ResumeWithTheRightOriginCompletes")]
    public async Task R13iPartialPromotionWithAnAccessibleButWrongOriginCancelAndPromoteAreRefusedNothingChangesResumeWithTheRightOriginCompletes()
    {
        await using var rig = await ConsolidationRig.CreateAsync();
        await RebuildSeed.ConvergeAsync(rig);
        var target = await rig.CreateRebuildTargetAsync();
        var request = Request("op-r13i", target, rig);
        await RunToEnabledAsync(rig.Rebuilder(target), request);
        var crash = new ScriptedRebuildPoints { OnPoint = (point, _) => point == RebuildPoints.PromoteAfterRegistry ? throw new InvalidOperationException("queda entre os dois bancos") : Task.CompletedTask };
        await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Rebuilder(target, crash).PromoteAsync(request, CancellationToken.None));
        var projectionId = await ProjectionIdAsync(target);
        var generation = await rig.OriginScalarAsync<long>("SELECT generation FROM consolidation_projection_registry WHERE id = 1");

        // Outra origem acessível, migrada, com a tabela de registro vazia (erro de configuração tecnicamente válido).
        var decoy = await rig.CreateDecoyOriginAsync();
        var wrong = ConsolidationRig.RebuilderWithOrigin(target, decoy);

        var cancel = await wrong.CancelAsync(request, CancellationToken.None);
        Assert.True(cancel.ExitCode == AdminExitCodes.Conflict, $"origem errada não pode autorizar o cancelamento: {BodyOf(cancel)}");
        Assert.Contains("origem", BodyOf(cancel), StringComparison.Ordinal);
        var promote = await wrong.PromoteAsync(request, CancellationToken.None);
        Assert.True(promote.ExitCode == AdminExitCodes.Conflict, $"origem errada não pode autorizar a promoção: {BodyOf(promote)}");

        Assert.Equal(RebuildStates.Enabled, await StateAsync(target)); // alvo intacto
        Assert.Equal(projectionId, await rig.OriginScalarAsync<Guid>("SELECT active_projection_id FROM consolidation_projection_registry WHERE id = 1")); // registro verdadeiro intacto
        Assert.Equal(generation, await rig.OriginScalarAsync<long>("SELECT generation FROM consolidation_projection_registry WHERE id = 1"));
        Assert.Equal(0, await ScalarOnAsync(decoy, "SELECT COUNT(*) FROM consolidation_projection_registry")); // a origem errada não recebeu registro

        var resumed = await rig.Rebuilder(target).PromoteAsync(request, CancellationToken.None); // origem verdadeira: conclui a mesma operação
        Assert.True(resumed.ExitCode == AdminExitCodes.Success, BodyOf(resumed));
        Assert.Contains("\"resumed\": true", BodyOf(resumed), StringComparison.Ordinal);
        Assert.Equal(RebuildStates.Promoted, await StateAsync(target));
        Assert.Equal(generation, await rig.OriginScalarAsync<long>("SELECT generation FROM consolidation_projection_registry WHERE id = 1")); // sem incremento indevido
    }

    [Fact(DisplayName = "R13j_ResumingAPreparedCaptureWithAWrongAccessibleOrigin_IsRefused_NothingSealed")]
    public async Task R13jResumingAPreparedCaptureWithAWrongAccessibleOriginIsRefusedNothingSealed()
    {
        await using var rig = await ConsolidationRig.CreateAsync();
        await RebuildSeed.ConvergeAsync(rig);
        var target = await rig.CreateRebuildTargetAsync();
        var request = Request("op-r13j", target);
        var crash = new ScriptedRebuildPoints { AfterCopy = _ => throw new InvalidOperationException("queda antes do commit da selagem") };
        await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Rebuilder(target, crash).CaptureAsync(request, CancellationToken.None));
        Assert.Equal(RebuildStates.Prepared, await StateAsync(target));

        // Outra origem acessível, com cópia do registro de controle do bootstrap e zero movimentos: passaria nas pré-condições.
        var decoy = await rig.CreateDecoyOriginAsync(copyCaptureRecordFromThisOrigin: true);
        var wrong = await ConsolidationRig.RebuilderWithOrigin(target, decoy).CaptureAsync(request, CancellationToken.None);

        Assert.True(wrong.ExitCode == AdminExitCodes.Conflict, $"a preparação pertence a outra origem: {BodyOf(wrong)}");
        Assert.Contains("origem", BodyOf(wrong), StringComparison.Ordinal);
        Assert.Equal(RebuildStates.Prepared, await StateAsync(target)); // nenhum R selado a partir da origem errada
        Assert.Equal(0, await TargetScalarAsync<long>(target, "SELECT COUNT(*) FROM rebuild_capture_item"));

        var right = await rig.Rebuilder(target).CaptureAsync(request, CancellationToken.None); // origem verdadeira: captura normalmente
        Assert.Equal(AdminExitCodes.Success, right.ExitCode);
        Assert.Equal(RebuildSeed.Plan.Length, await TargetScalarAsync<long>(target, "SELECT COUNT(*) FROM rebuild_capture_item"));
    }
}
