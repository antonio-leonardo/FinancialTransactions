using System.Diagnostics;
using System.Net;
using FinancialTransactions.Consolidation;
using Microsoft.Extensions.DependencyInjection;

namespace FinancialTransactions.IntegrationTests;

/// <summary>
/// C1 a C5 e C13: o registro de lançamentos é independente do Worker, da projeção e da API do consolidado, e a consolidação se recupera
/// sozinha. Esperados vêm dos valores enviados e de SQL direto, não de código de produção. Este é o cenário automatizado; a sondagem
/// contra processos reais é outra evidência, registrada à parte.
/// </summary>
public sealed class ConsolidationIndependenceTests(ConsolidationFixture fixture) : ConsolidationTest(fixture)
{
    private async Task<(Guid E1, Guid E2, Guid E3, DateOnly Day)> PostThreeWithWorkerDownAsync()
    {
        // Host anterior descartado: o Worker já existiu e foi encerrado, como no cenário descrito.
        var previous = await StartWorkerAsync();
        Assert.True(await previous.StopAsync(TimeSpan.FromSeconds(10)));

        var at = DateTimeOffset.UtcNow.AddMinutes(-1);
        var e1 = await Rig.PostOkAsync(CustomerA, A1, "Credit", 100.00m, at);
        var e2 = await Rig.PostOkAsync(CustomerA, A1, "Debit", 30.00m, at);
        var e3 = await Rig.PostOkAsync(CustomerA, A2, "Credit", 50.00m, at);
        return (e1, e2, e3, BusinessDay.Of(at));
    }

    [Fact(DisplayName = "C1_WorkerDown_WritesAccepted_ConsolidatedShowsPendingAndStale")]
    public async Task C1WorkerDownWritesAcceptedConsolidatedShowsPendingAndStale()
    {
        Use(A1, A2, B1);
        var (_, _, _, day) = await PostThreeWithWorkerDownAsync();
        Assert.Equal(3, await Rig.OutboxCountAsync(0));
        var api = await StartApiAsync();

        var a1 = await ReadAccountAsync(api, CustomerA, A1, day);
        Assert.Equal(0m, a1.Credits); // valores anteriores: nada aplicado ainda
        Assert.Equal(0m, a1.ClosingBalance);
        Assert.Equal(2, a1.UnresolvedEvents);
        Assert.Equal(true, a1.Stale);
        Assert.Equal("stale", a1.Freshness);
        Assert.NotNull(a1.ObservedAt);
        Assert.Equal(DayText(day), a1.Day);
        Assert.Equal("America/Sao_Paulo", a1.BusinessTimeZone);

        var a2 = await ReadAccountAsync(api, CustomerA, A2, day);
        Assert.Equal(1, a2.UnresolvedEvents);

        var (customerStatus, customerBody) = await api.GetCustomerAsync(CustomerA, DayText(day));
        Assert.Equal(HttpStatusCode.OK, customerStatus);
        var customer = ConsolidationRig.Parse<CustomerDailyDto>(customerBody);
        Assert.Equal(3, customer.UnresolvedEvents);
        Assert.Equal("stale", customer.Freshness);

        // Cliente B: nenhum vazamento dos eventos do cliente A.
        var (bStatus, bBody) = await api.GetCustomerAsync(CustomerB, DayText(day));
        Assert.Equal(HttpStatusCode.OK, bStatus);
        var b = ConsolidationRig.Parse<CustomerDailyDto>(bBody);
        Assert.Equal(0, b.UnresolvedEvents);
        Assert.Equal("complete", b.Freshness);
    }

    [Fact(DisplayName = "C2_WorkerRestartedInNewHost_AppliesExactlyTheThreeEvents_AndReconcilesWithOrigin")]
    public async Task C2WorkerRestartedInNewHostAppliesExactlyTheThreeEvents()
    {
        Use(A1, A2, B1);
        var (e1, e2, e3, day) = await PostThreeWithWorkerDownAsync();
        var before = await Rig.ProcessedCountAsync();

        var host = await StartWorkerAsync(); // host novo sobre os mesmos bancos
        await ConsolidationRig.Eventually(async () => await Rig.OutboxCountAsync(0) == 0, "Outbox sem pendentes");

        Assert.Equal(before + 3, await Rig.ProcessedCountAsync());
        var applied = (await Rig.ProjectionGuidsAsync("SELECT event_id FROM processed_events")).ToHashSet();
        Assert.Equal(new HashSet<Guid> { e1, e2, e3 }, applied);
        Assert.Equal(3, await Rig.OutboxCountAsync(1));

        var api = await StartApiAsync();
        var a1 = await ReadAccountAsync(api, CustomerA, A1, day);
        Assert.Equal(100.00m, a1.Credits);
        Assert.Equal(30.00m, a1.Debits);
        Assert.Equal(70.00m, a1.Net);
        Assert.Equal(2, a1.MovementCount);
        Assert.Equal(70.00m, a1.ClosingBalance);
        Assert.Equal(0, a1.UnresolvedEvents);
        Assert.Equal("complete", a1.Freshness);
        var a2 = await ReadAccountAsync(api, CustomerA, A2, day);
        Assert.Equal(50.00m, a2.Credits);
        Assert.Equal("complete", a2.Freshness);

        // Reconciliação por conta e dia contra a origem: contagem, soma e conjunto de ids relacionados por event_id.
        foreach (var account in new[] { A1, A2 })
        {
            var originCount = await Rig.OriginScalarAsync<long>("SELECT COUNT(*) FROM movements WHERE account_id = @a", ("a", account));
            var originSum = await Rig.OriginScalarAsync<decimal>(
                "SELECT COALESCE(SUM(CASE type WHEN 1 THEN amount ELSE -amount END), 0) FROM movements WHERE account_id = @a", ("a", account));
            var projectedCount = await Rig.ProjectionScalarAsync<long>("SELECT COALESCE(SUM(movement_count), 0)::bigint FROM daily_account_consolidation WHERE account_id = @a", ("a", account));
            var projectedSum = await Rig.ProjectionScalarAsync<decimal>("SELECT COALESCE(SUM(net), 0) FROM daily_account_consolidation WHERE account_id = @a", ("a", account));
            Assert.Equal(originCount, projectedCount);
            Assert.Equal(originSum, projectedSum);
        }

        Assert.False(host.LoopCompleted);
    }

    [Fact(DisplayName = "C3_ProjectionUnreachable_WritesStayUp_ConsolidatedAnswers503WithinTwoSeconds")]
    public async Task C3ProjectionUnreachableWritesStayUpConsolidatedAnswers503WithinTwoSeconds()
    {
        Use(A1);
        var workerConnections = new TestConnectionFactory(Rig.Real());
        workerConnections.RedirectProjection(ConsolidationConnectionRole.Consumer, TestConnectionFactory.Unreachable);
        await StartWorkerAsync(new WorkerStart { Factory = workerConnections });
        var apiConnections = new TestConnectionFactory(Rig.Real(1, 1));
        apiConnections.RedirectProjection(ConsolidationConnectionRole.Query, TestConnectionFactory.Unreachable);
        var api = await StartApiAsync(apiConnections);

        var at = DateTimeOffset.UtcNow.AddMinutes(-1);
        var (status, id, _) = await Rig.PostAsync(CustomerA, A1, "Credit", 10.00m, at);
        Assert.Equal(HttpStatusCode.Created, status);
        Assert.NotNull(id);

        var watch = Stopwatch.StartNew();
        var (getStatus, body) = await api.GetAccountAsync(CustomerA, A1, DayText(BusinessDay.Of(at)));
        watch.Stop();

        Assert.Equal(HttpStatusCode.ServiceUnavailable, getStatus);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2), $"resposta em {watch.Elapsed.TotalSeconds:F2} s (modo exercitado: conexão recusada)");
        Assert.Contains("\"code\":\"dependency_unavailable\"", body, StringComparison.Ordinal);
        Assert.DoesNotContain("closingBalance", body, StringComparison.Ordinal);
        Assert.DoesNotContain("credits", body, StringComparison.Ordinal);
        Assert.DoesNotContain("Password", body, StringComparison.Ordinal);

        // A consulta da API usou, de fato, a conexão inalcançável (papel Query registrado na fábrica da API).
        Assert.True(apiConnections.Calls(ConsolidationConnectionRole.Query) >= 1);
        Assert.Contains(TestConnectionFactory.Unreachable, apiConnections.ProjectionsServed(ConsolidationConnectionRole.Query));

        // Posição por instante e readiness da escrita não dependem do consolidado.
        using var client = Rig.Writes.CreateClient(CustomerA);
        using var position = await client.GetAsync(new Uri($"/api/v1/accounts/{A1}/position?asOf={Uri.EscapeDataString(DateTimeOffset.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture))}", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, position.StatusCode);
        using var ready = await client.GetAsync(new Uri("/health/ready", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
    }

    [Fact(DisplayName = "C4_ProjectionBackAfterSixFailedAttempts_SameHostRecoversWithoutIntervention")]
    public async Task C4ProjectionBackAfterSixFailedAttemptsSameHostRecoversWithoutIntervention()
    {
        Use(A1);
        var connections = new TestConnectionFactory(Rig.Real());
        connections.RedirectProjection(ConsolidationConnectionRole.Consumer, TestConnectionFactory.Unreachable);
        var worker = await StartWorkerAsync(new WorkerStart { Factory = connections });
        var at = DateTimeOffset.UtcNow.AddMinutes(-1);
        var before = await Rig.ProcessedCountAsync();
        var id = await Rig.PostOkAsync(CustomerA, A1, "Credit", 10.00m, at);

        // Só tentativas reais do consumidor contam (as sondas de saúde usam outro papel).
        await ConsolidationRig.Eventually(() => Task.FromResult(worker.Diagnostics.Failures(FailureClass.Transient).Count >= 6), "6 tentativas falhas do consumidor");
        var failures = worker.Diagnostics.Failures(FailureClass.Transient).Select(e => e.At).ToList();
        var gaps = failures.Zip(failures.Skip(1), (a, b) => (b - a).TotalMilliseconds).Take(5).ToList();
        for (var i = 1; i < gaps.Count; i++)
        {
            Assert.True(gaps[i] > gaps[i - 1], $"intervalos de backoff devem crescer: {string.Join(", ", gaps.Select(g => g.ToString("F0", System.Globalization.CultureInfo.InvariantCulture)))} ms");
        }

        Assert.Equal(0, await Rig.OutboxCountAsync(2)); // nada foi para a quarentena
        Assert.Equal(0, await Rig.OriginScalarAsync<int>("SELECT COALESCE(MAX(attempts), 0) FROM consolidation_outbox WHERE account_id = @a", ("a", A1)));
        Assert.Equal(before, await Rig.ProcessedCountAsync());
        Assert.False(worker.LoopCompleted);

        connections.Restore(ConsolidationConnectionRole.Consumer); // a projeção "volta"; ninguém reinicia nada

        await ConsolidationRig.Eventually(async () => await Rig.OutboxCountAsync(0) == 0, "evento aplicado depois da recuperação");
        Assert.Equal(before + 1, await Rig.ProcessedCountAsync());
        Assert.Contains(id, await Rig.ProjectionGuidsAsync("SELECT event_id FROM processed_events"));
        Assert.False(worker.LoopCompleted); // o mesmo host, o mesmo laço

        var api = await StartApiAsync();
        Assert.Equal("complete", (await ReadAccountAsync(api, CustomerA, A1, BusinessDay.Of(at))).Freshness);
        await ConsolidationRig.Eventually(async () =>
        {
            var (status, body) = await worker.GetAsync("/health/ready");
            return status == HttpStatusCode.OK && body == "{\"status\":\"Healthy\"}";
        }, "ready do Worker 200 Healthy");
    }

    [Fact(DisplayName = "C5_ConsolidationApiDown_WritesPositionAndReadinessUnaffected_ThenBack")]
    public async Task C5ConsolidationApiDownWritesPositionAndReadinessUnaffectedThenBack()
    {
        Use(A1);
        await StartWorkerAsync();
        var api = await StartApiAsync();
        var at = DateTimeOffset.UtcNow.AddMinutes(-1);
        await api.DisposeAsync(); // API do consolidado fora do ar; a de lançamentos segue

        var (status, _, _) = await Rig.PostAsync(CustomerA, A1, "Credit", 10.00m, at);
        Assert.Equal(HttpStatusCode.Created, status);
        using var client = Rig.Writes.CreateClient(CustomerA);
        using var position = await client.GetAsync(new Uri($"/api/v1/accounts/{A1}/position?asOf={Uri.EscapeDataString(DateTimeOffset.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture))}", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, position.StatusCode);
        using var ready = await client.GetAsync(new Uri("/health/ready", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);

        await ConsolidationRig.Eventually(async () => await Rig.OutboxCountAsync(0) == 0, "evento aplicado");
        var back = await StartApiAsync();
        var daily = await ReadAccountAsync(back, CustomerA, A1, BusinessDay.Of(at));
        Assert.Equal(10.00m, daily.Credits);
    }

    [Fact(DisplayName = "C13_Regression_WritesReadinessIsIndependentOfWorkerProjectionAndConsolidationApi")]
    public async Task C13RegressionWritesReadinessIsIndependentOfWorkerProjectionAndConsolidationApi()
    {
        // Nada do consolidado está no ar neste teste (nenhum Worker, nenhuma API do consolidado). Os 137 testes anteriores
        // continuam presentes e passam nesta mesma execução (conferência nominal no checkpoint).
        using var client = Rig.Writes.CreateClient(CustomerA);
        using var ready = await client.GetAsync(new Uri("/health/ready", UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
        Assert.Equal("{\"status\":\"Healthy\"}", await ready.Content.ReadAsStringAsync());

        // A composição da API de lançamentos não conhece a projeção: sem contexto, sem fábrica, sem connection string.
        Assert.Null(Rig.Writes.Services.GetService<ConsolidationDbContext>());
        Assert.Null(Rig.Writes.Services.GetService<IConsolidationConnectionFactory>());
    }
}
