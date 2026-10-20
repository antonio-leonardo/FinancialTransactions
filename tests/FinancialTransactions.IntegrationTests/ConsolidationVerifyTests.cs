using System.Text.Json;
using FinancialTransactions.Consolidation;
using FinancialTransactions.Consolidation.Worker;
using Microsoft.Extensions.Configuration;
using Npgsql;
using Xunit.Abstractions;

namespace FinancialTransactions.IntegrationTests;

/// <summary>Massa convergida e Workers parados: a pré-condição das verificações e da reconstrução.</summary>
internal static class RebuildSeed
{
    public static readonly Guid A1 = ConsolidationRig.AccountA1;
    public static readonly Guid A2 = ConsolidationRig.AccountA2;
    public static readonly Guid CustomerA = ConsolidationRig.CustomerA;

    public static readonly (Guid Account, string Type, decimal Amount, int DayOffset)[] Plan =
    [
        (A1, "Credit", 10.00m, 0), (A1, "Debit", 3.25m, 0), (A2, "Credit", 7.50m, 0), (A2, "Credit", 2.50m, 0),
        (A1, "Credit", 4.00m, -3), (A2, "Debit", 1.10m, -3),
    ];

    /// <summary>Leva a projeção a live, posta os movimentos do plano, espera a convergência e para o Worker (sessões fechadas).</summary>
    public static async Task<DateTimeOffset> ConvergeAsync(ConsolidationRig rig)
    {
        await rig.BringToLiveAsync();
        var worker = await rig.StartWorkerAsync();
        var at = DateTimeOffset.UtcNow.AddMinutes(-1);
        foreach (var (account, type, amount, offset) in Plan)
        {
            await rig.PostOkAsync(CustomerA, account, type, amount, at.AddDays(offset));
        }

        await ConsolidationRig.Eventually(async () => await rig.OutboxCountAsync(1) == Plan.Length, "movimentos do plano confirmados");
        Assert.True(await worker.StopAsync(TimeSpan.FromSeconds(10)));
        await worker.DisposeAsync();
        NpgsqlConnection.ClearAllPools();
        return at;
    }

    public static async Task<string> TablesSnapshotAsync(ConsolidationRig rig)
    {
        var origin = await rig.OriginScalarAsync<string>(
            "SELECT COALESCE(string_agg(sequence::text || ':' || status || ':' || attempts || ':' || COALESCE(processed_at::text, '') || ':' || xmin::text, ',' ORDER BY sequence), '') FROM consolidation_outbox");
        var movements = await rig.OriginScalarAsync<string>("SELECT COALESCE(string_agg(id::text || ':' || xmin::text, ',' ORDER BY id), '') FROM movements");
        var daily = await rig.ProjectionScalarAsync<string>("SELECT COALESCE(string_agg(account_id::text || business_day::text || ':' || xmin::text, ',' ORDER BY account_id, business_day), '') FROM daily_account_consolidation");
        var receipts = await rig.ProjectionScalarAsync<string>("SELECT COALESCE(string_agg(event_id::text || ':' || xmin::text, ',' ORDER BY event_id), '') FROM processed_events");
        var boot = await rig.ProjectionScalarAsync<string>("SELECT COALESCE(string_agg(bootstrap_id::text || phase || ':' || xmin::text, ','), '') FROM consolidation_bootstrap");
        return string.Join('|', origin, movements, daily, receipts, boot);
    }
}

/// <summary>R09: o verificador financeiro somente leitura, o controle positivo e os controles negativos causais.</summary>
public sealed class ConsolidationVerifyTests(ITestOutputHelper output)
{
    private static string Kinds(VerificationReport report) => string.Join(",", report.DeviationCountsByKind.Keys.Order());

    [Fact(DisplayName = "R09a_PositiveBaseline_LiveAndConvergence_AreConsistent_AndVerifyChangesNothing")]
    public async Task R09aPositiveBaselineLiveAndConvergenceAreConsistentAndVerifyChangesNothing()
    {
        await using var rig = await ConsolidationRig.CreateAsync();
        await RebuildSeed.ConvergeAsync(rig);
        var before = await RebuildSeed.TablesSnapshotAsync(rig);

        var live = await rig.Verifier().VerifyAsync(VerifyMode.Live, CancellationToken.None);
        var convergence = await rig.Verifier().VerifyAsync(VerifyMode.Convergence, CancellationToken.None);

        Assert.True(live.Consistent, string.Join("; ", live.Deviations.Select(d => d.Kind + ": " + d.Detail)));
        Assert.True(convergence.Consistent, string.Join("; ", convergence.Deviations.Select(d => d.Kind + ": " + d.Detail)));
        Assert.Equal(RebuildSeed.Plan.Length, live.Counts.Receipts);
        Assert.Equal(RebuildSeed.Plan.Length, live.Counts.MovementsMatched);
        Assert.Equal(4, live.Counts.AggregatesExpected); // 2 contas x 2 dias
        Assert.Equal(live.Counts.AggregatesExpected, live.Counts.AggregatesProjection);
        Assert.Equal(0, convergence.Counts.PendingInScope);
        Assert.Equal(before, await RebuildSeed.TablesSnapshotAsync(rig)); // --verify não modifica dados (xmin de todas as linhas)
        output.WriteLine($"R09a live={live.Conclusion} | convergência={convergence.Conclusion}");
    }

    [Fact(DisplayName = "R09b_VerifyJsonViaTheExecutable_ExitCodes_ZeroConsistent_FourDivergent_TwoUnreachable")]
    public async Task R09bVerifyJsonViaTheExecutableExitCodesZeroConsistentFourDivergentTwoUnreachable()
    {
        await using var rig = await ConsolidationRig.CreateAsync();
        await RebuildSeed.ConvergeAsync(rig);
        static void Configure(Microsoft.Extensions.Hosting.HostApplicationBuilder builder, ConsolidationRig rig, string? origin = null)
        {
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:FinancialTransactions"] = origin ?? rig.OriginConnection,
                ["ConnectionStrings:Consolidation"] = rig.ProjectionConnection,
            });
        }

        var ok = new StringWriter();
        Assert.Equal(AdminExitCodes.Success, await WorkerAdmin.RunAsync("--verify", ["--mode", "convergence"], ok, b => Configure(b, rig)));
        var json = JsonDocument.Parse(ok.ToString()).RootElement;
        Assert.True(json.GetProperty("consistent").GetBoolean());
        Assert.Equal("convergence", json.GetProperty("mode").GetString());

        await rig.ProjectionExecuteAsync("UPDATE daily_account_consolidation SET credits = credits + 1, net = net + 1 WHERE account_id = @a AND business_day = (SELECT MAX(business_day) FROM daily_account_consolidation WHERE account_id = @a)", ("a", RebuildSeed.A1));
        var bad = new StringWriter();
        Assert.Equal(AdminExitCodes.Conflict, await WorkerAdmin.RunAsync("--verify", [], bad, b => Configure(b, rig)));
        Assert.False(JsonDocument.Parse(bad.ToString()).RootElement.GetProperty("consistent").GetBoolean());

        var down = new StringWriter();
        Assert.Equal(AdminExitCodes.Unreachable, await WorkerAdmin.RunAsync("--verify", [], down, b => Configure(b, rig, TestConnectionFactory.Unreachable)));
        Assert.Equal(AdminExitCodes.Refused, await WorkerAdmin.RunAsync("--verify", ["--mode", "qualquer"], new StringWriter(), b => Configure(b, rig)));
    }

    public static TheoryData<string> NegativeCases => new() { "credit_and_net", "compensated_accounts", "receipt_removed", "receipt_without_movement", "extra_aggregate", "descriptor_destination", "descriptor_both_sides" };

    [Theory(DisplayName = "R09c_NegativeControls_EachFailsForTheIntendedDifference_AfterAPositiveBaseline")]
    [MemberData(nameof(NegativeCases))]
    public async Task R09cNegativeControlsEachFailsForTheIntendedDifferenceAfterAPositiveBaseline(string scenario)
    {
        await using var rig = await ConsolidationRig.CreateAsync();
        await RebuildSeed.ConvergeAsync(rig);
        var verifier = rig.Verifier();

        // Baseline positiva ANTES da adulteração.
        Assert.True((await verifier.VerifyAsync(VerifyMode.Live, CancellationToken.None)).Consistent);
        Assert.True((await verifier.VerifyAsync(VerifyMode.Convergence, CancellationToken.None)).Consistent);

        var a1 = RebuildSeed.A1;
        var a2 = RebuildSeed.A2;
        const string LatestDay = "(SELECT MAX(business_day) FROM daily_account_consolidation WHERE account_id = @a)";
        VerifyMode mode = VerifyMode.Live;
        string[] mustContain;
        string[] allowed;
        switch (scenario)
        {
            case "credit_and_net":
            {
                // O crédito e o net correspondente sobem juntos: as CHECKs (credits >= 0, net = credits - debits) aceitam a adulteração.
                var before = await rig.ProjectionScalarAsync<decimal>($"SELECT credits FROM daily_account_consolidation WHERE account_id = @a AND business_day = {LatestDay}", ("a", a1));
                await rig.ProjectionExecuteAsync($"UPDATE daily_account_consolidation SET credits = credits + 1.00, net = net + 1.00 WHERE account_id = @a AND business_day = {LatestDay}", ("a", a1));
                var after = await rig.ProjectionScalarAsync<decimal>($"SELECT credits FROM daily_account_consolidation WHERE account_id = @a AND business_day = {LatestDay}", ("a", a1));
                Assert.Equal(before + 1.00m, after); // a adulteração foi aceita no banco
                mustContain = [DeviationKinds.ValueMismatch];
                allowed = [DeviationKinds.ValueMismatch, DeviationKinds.ClosingMismatch];
                break;
            }

            case "compensated_accounts":
            {
                var sumBefore = await rig.ProjectionScalarAsync<string>("SELECT SUM(credits)::text || '|' || SUM(net)::text FROM daily_account_consolidation");
                await rig.ProjectionExecuteAsync($"UPDATE daily_account_consolidation SET credits = credits + 1.00, net = net + 1.00 WHERE account_id = @a AND business_day = {LatestDay}", ("a", a1));
                await rig.ProjectionExecuteAsync($"UPDATE daily_account_consolidation SET credits = credits - 1.00, net = net - 1.00 WHERE account_id = @a AND business_day = {LatestDay}", ("a", a2));
                Assert.Equal(sumBefore, await rig.ProjectionScalarAsync<string>("SELECT SUM(credits)::text || '|' || SUM(net)::text FROM daily_account_consolidation")); // soma global preservada
                mustContain = [DeviationKinds.ValueMismatch];
                allowed = [DeviationKinds.ValueMismatch, DeviationKinds.ClosingMismatch];
                break;
            }

            case "receipt_removed":
            {
                var id = (await rig.ProjectionGuidsAsync("SELECT event_id FROM processed_events ORDER BY event_id"))[0];
                await rig.ProjectionExecuteAsync("DELETE FROM processed_events WHERE event_id = @id", ("id", id));
                Assert.Equal(RebuildSeed.Plan.Length - 1, await rig.ProcessedCountAsync());
                // Convergência: o movimento ficou sem recibo, e só isso diverge.
                var convergence = await verifier.VerifyAsync(VerifyMode.Convergence, CancellationToken.None);
                Assert.False(convergence.Consistent);
                Assert.Equal(DeviationKinds.MissingReceipt, Kinds(convergence));
                // Viva: os recibos restantes já não explicam o agregado (contagem e valores a mais).
                mustContain = [DeviationKinds.ValueMismatch];
                allowed = [DeviationKinds.ValueMismatch, DeviationKinds.ClosingMismatch];
                break;
            }

            case "receipt_without_movement":
                await rig.ProjectionExecuteAsync(
                    "INSERT INTO processed_events (event_id, sequence, account_id, processed_at) VALUES (@id, 999999, @a, now())",
                    ("id", Guid.NewGuid()),
                    ("a", a1));
                Assert.Equal(RebuildSeed.Plan.Length + 1, await rig.ProcessedCountAsync());
                mustContain = [DeviationKinds.ReceiptWithoutMovement];
                allowed = [DeviationKinds.ReceiptWithoutMovement];
                break;

            case "extra_aggregate":
                // Linha extra VÁLIDA (CHECKs satisfeitas) em um dia sem nenhum movimento.
                await rig.ProjectionExecuteAsync(
                    "INSERT INTO daily_account_consolidation (account_id, business_day, credits, debits, net, movement_count, max_applied_sequence, updated_at) VALUES (@a, '2026-01-15', 5.00, 0, 5.00, 1, 1, now())",
                    ("a", a2));
                Assert.Equal(1, await rig.ProjectionScalarAsync<long>("SELECT COUNT(*) FROM daily_account_consolidation WHERE business_day = '2026-01-15'"));
                mustContain = [DeviationKinds.ExtraAggregate];
                allowed = [DeviationKinds.ExtraAggregate, DeviationKinds.ClosingMismatch];
                break;

            case "descriptor_destination":
                await rig.ProjectionExecuteAsync("UPDATE consolidation_bootstrap SET member_count = member_count + 3 WHERE id = 1");
                Assert.Equal(3, await rig.ProjectionScalarAsync<long>("SELECT member_count FROM consolidation_bootstrap WHERE id = 1"));
                mustContain = [DeviationKinds.DescriptorMismatch];
                allowed = [DeviationKinds.DescriptorMismatch];
                break;

            default: // descriptor_both_sides: coerentes entre si e errados contra a cardinalidade real do conjunto (zero membros)
                await rig.OriginExecuteAsync("UPDATE consolidation_bootstrap_capture SET member_count = 4 WHERE id = 1");
                await rig.ProjectionExecuteAsync("UPDATE consolidation_bootstrap SET member_count = 4 WHERE id = 1");
                Assert.Equal(4, await rig.OriginScalarAsync<long>("SELECT member_count FROM consolidation_bootstrap_capture WHERE id = 1"));
                mustContain = [DeviationKinds.DescriptorMismatch];
                allowed = [DeviationKinds.DescriptorMismatch];
                break;
        }

        var report = await verifier.VerifyAsync(mode, CancellationToken.None);
        output.WriteLine($"R09c {scenario}: {Kinds(report)} | primeira: {(report.Deviations.Count > 0 ? report.Deviations[0].Detail : string.Empty)}");
        Assert.False(report.Consistent);
        foreach (var kind in mustContain)
        {
            Assert.Contains(kind, report.DeviationCountsByKind.Keys);
        }

        Assert.All(report.DeviationCountsByKind.Keys, kind => Assert.Contains(kind, allowed)); // falha SÓ pela diferença pretendida
        if (scenario == "compensated_accounts")
        {
            Assert.Equal(2, report.Deviations.Where(d => d.Kind == DeviationKinds.ValueMismatch).Select(d => d.AccountId).Distinct().Count()); // as duas contas
        }
    }
}
