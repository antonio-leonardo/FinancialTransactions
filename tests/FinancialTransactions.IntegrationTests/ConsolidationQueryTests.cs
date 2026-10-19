using System.Diagnostics;
using System.Net;
using FinancialTransactions.Consolidation;

namespace FinancialTransactions.IntegrationTests;

/// <summary>Contrato da API do consolidado: autorização, parâmetro day, fronteiras, estado de atualização e a ordem origem, depois projeção (C11, C14).</summary>
public sealed class ConsolidationQueryTests(ConsolidationFixture fixture) : ConsolidationTest(fixture)
{
    private static string AccountRoute(Guid account, string day) => $"/api/v1/accounts/{account}/consolidation/daily?day={day}";

    private static string CustomerRoute(Guid customer, string day) => $"/api/v1/customers/{customer}/consolidation/daily?day={day}";

    [Fact(DisplayName = "C11a_Authorization_NoToken401_OtherCustomer404_UnknownAccount404")]
    public async Task C11aAuthorizationNoToken401OtherCustomer404UnknownAccount404()
    {
        var api = await StartApiAsync();
        var day = DayText(Today);

        Assert.Equal(HttpStatusCode.Unauthorized, (await api.GetAsync(AccountRoute(A1, day), null)).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await api.GetAsync(CustomerRoute(CustomerA, day), null)).Status);

        // Token do cliente B tentando enxergar o cliente A: 404 em ambas as rotas, sem revelar existência.
        Assert.Equal(HttpStatusCode.NotFound, (await api.GetAccountAsync(CustomerB, A1, day)).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await api.GetAsync(CustomerRoute(CustomerA, day), CustomerB)).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await api.GetAccountAsync(CustomerA, Guid.NewGuid(), day)).Status);
    }

    [Fact(DisplayName = "C11b_UnresolvedEvents_NeverIncludeAnotherCustomersEvents")]
    public async Task C11bUnresolvedEventsNeverIncludeAnotherCustomersEvents()
    {
        Use(A1, B1);
        var at = DateTimeOffset.UtcNow.AddMinutes(-1);
        await Rig.PostOkAsync(CustomerB, B1, "Credit", 10.00m, at); // pendente do cliente B (sem Worker)
        var api = await StartApiAsync();
        var day = BusinessDay.Of(at);

        var a1 = await ReadAccountAsync(api, CustomerA, A1, day);
        Assert.Equal(0, a1.UnresolvedEvents);
        Assert.Equal("complete", a1.Freshness);

        var (status, body) = await api.GetCustomerAsync(CustomerA, DayText(day));
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(0, ConsolidationRig.Parse<CustomerDailyDto>(body).UnresolvedEvents);

        var b1 = await ReadAccountAsync(api, CustomerB, B1, day);
        Assert.Equal(1, b1.UnresolvedEvents);
    }

    [Fact(DisplayName = "C11c_OriginUnreachableDuringAuthorization_Is503DependencyUnavailable")]
    public async Task C11cOriginUnreachableDuringAuthorizationIs503DependencyUnavailable()
    {
        var connections = new TestConnectionFactory(Rig.Real(1, 1));
        connections.RedirectOrigin(ConsolidationConnectionRole.Query, TestConnectionFactory.Unreachable);
        var api = await StartApiAsync(connections);

        var watch = Stopwatch.StartNew();
        var (status, body) = await api.GetAccountAsync(CustomerA, A1, DayText(Today));
        watch.Stop();

        Assert.Equal(HttpStatusCode.ServiceUnavailable, status);
        Assert.Contains("\"code\":\"dependency_unavailable\"", body, StringComparison.Ordinal);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2));
    }

    [Theory(DisplayName = "DayParameter_AnyFormOtherThanStrictIsoDate_Is422ValidationError")]
    [InlineData("2026-9-1")]
    [InlineData("01/09/2026")]
    [InlineData("2026-09-01T00:00:00Z")]
    [InlineData("20260901")]
    [InlineData("hoje")]
    [InlineData("")]
    public async Task DayParameterAnyFormOtherThanStrictIsoDateIs422(string day)
    {
        var api = await StartApiAsync();

        foreach (var route in new[] { AccountRoute(A1, day), CustomerRoute(CustomerA, day) })
        {
            var (status, body) = await api.GetAsync(route, CustomerA);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, status);
            Assert.Contains("\"code\":\"validation_error\"", body, StringComparison.Ordinal);
        }
    }

    [Fact(DisplayName = "DayParameter_Missing_And_Future_Are422_Today_IsOpen_Past_IsClosed")]
    public async Task DayParameterMissingAndFutureAre422TodayIsOpenPastIsClosed()
    {
        var api = await StartApiAsync();

        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await api.GetAsync($"/api/v1/accounts/{A1}/consolidation/daily", CustomerA)).Status);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await api.GetAccountAsync(CustomerA, A1, DayText(Today.AddDays(1)))).Status);

        Assert.Equal("open", (await ReadAccountAsync(api, CustomerA, A1, Today)).DayStatus);
        var past = await ReadAccountAsync(api, CustomerA, A1, Today.AddDays(-1));
        Assert.Equal("closed", past.DayStatus);
        Assert.Equal("BRL", past.Currency);
        Assert.Equal("America/Sao_Paulo", past.BusinessTimeZone);
    }

    [Fact(DisplayName = "DayBoundary_AccountOpenedBeforeEndOfLocalDay_IsIncluded_OpenedAfter_IsHistoricoInsuficiente")]
    public async Task DayBoundaryAccountOpenedBeforeEndOfLocalDayIsIncludedOpenedAfterIsHistoricoInsuficiente()
    {
        var api = await StartApiAsync();

        // A2 abriu em 2026-03-01T00:00Z = 21:00 de 28/02 no horário local: já existia no fim do dia 28/02, não no do dia 27/02.
        Assert.Equal(HttpStatusCode.OK, (await api.GetAccountAsync(CustomerA, A2, "2026-02-28")).Status);
        var (status, body) = await api.GetAccountAsync(CustomerA, A2, "2026-02-27");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, status);
        Assert.Contains("\"code\":\"historico_insuficiente\"", body, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "CustomerRoute_ListsAccountsOpenedAfterTheDayAsExcluded_AndNoneIncludedIsHistoricoInsuficiente")]
    public async Task CustomerRouteListsAccountsOpenedAfterTheDayAsExcludedAndNoneIncludedIsHistoricoInsuficiente()
    {
        var api = await StartApiAsync();

        var (status, body) = await api.GetCustomerAsync(CustomerA, "2026-02-27");
        Assert.Equal(HttpStatusCode.OK, status);
        var customer = ConsolidationRig.Parse<CustomerDailyDto>(body);
        Assert.Equal([A1], customer.Accounts.Select(a => a.AccountId).ToArray());
        var excluded = Assert.Single(customer.ExcludedAccounts);
        Assert.Equal(A2, excluded.AccountId);
        Assert.Equal(DateTimeOffset.Parse("2026-03-01T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture), excluded.OpenedAt);

        var (noneStatus, noneBody) = await api.GetCustomerAsync(CustomerA, "2025-12-30");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, noneStatus);
        Assert.Contains("\"code\":\"historico_insuficiente\"", noneBody, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "Freshness_OriginDiagnosticReadFailsAfterAuthorization_IsUnknownWithNullCounters")]
    public async Task FreshnessOriginDiagnosticReadFailsAfterAuthorizationIsUnknownWithNullCounters()
    {
        Use(A1);
        var at = DateTimeOffset.UtcNow.AddMinutes(-1);
        await Rig.PostOkAsync(CustomerA, A1, "Credit", 10.00m, at);
        var connections = new TestConnectionFactory(Rig.Real(1, 1))
        {
            // Chamada 1 do papel Query = autorização; 2 = leitura diagnóstica da origem; 3 = consulta à projeção.
            Interceptor = (role, number, strings) =>
                role == ConsolidationConnectionRole.Query && number == 2 ? strings with { Origin = TestConnectionFactory.Unreachable } : null,
        };
        var api = await StartApiAsync(connections);

        var daily = await ReadAccountAsync(api, CustomerA, A1, BusinessDay.Of(at));

        Assert.Equal("unknown", daily.Freshness);
        Assert.Null(daily.UnresolvedEvents);
        Assert.Null(daily.FailedEvents);
        Assert.Null(daily.Stale);
        Assert.Null(daily.ObservedAt);
        Assert.Equal(0m, daily.Credits); // a projeção foi lida; o corpo não afirma completude
    }

    [Fact(DisplayName = "C14_RaceOnRefreshState_StaleCountersFromTheOriginReading_FinancialFieldsFromTheLaterProjectionQuery")]
    public async Task C14RaceOnRefreshStateStaleCountersFromTheOriginReadingFinancialFieldsFromTheLaterProjectionQuery()
    {
        Use(A1);
        var gate = new Gate();
        var api = await StartApiAsync(
            failurePoints: new TestApiFailurePoints { BetweenOriginAndProjection = gate },
            configureApi: o => o.RequestBudget = TimeSpan.FromSeconds(60)); // só para a pausa não estourar o orçamento padrão
        var at = DateTimeOffset.UtcNow.AddMinutes(-1);
        var id = await Rig.PostOkAsync(CustomerA, A1, "Credit", 10.00m, at); // Worker parado: evento pendente

        var request = Task.Run(() => api.GetAccountAsync(CustomerA, A1, DayText(BusinessDay.Of(at))));
        await gate.Reached.WaitAsync(TimeSpan.FromSeconds(20)); // a API já leu a origem (viu 1 pendente) e não abriu a projeção

        await StartWorkerAsync(); // host novo, com a API pausada
        await ConsolidationRig.Eventually(
            async () => Contains(await Rig.ProjectionGuidsAsync("SELECT event_id FROM processed_events"), id), "commit na projeção");
        await ConsolidationRig.Eventually(
            async () => await Rig.OriginScalarAsync<short>("SELECT status FROM consolidation_outbox WHERE event_id = @id", ("id", id)) == 1, "confirmação na origem");

        gate.Release();
        var (status, body) = await request;

        Assert.Equal(HttpStatusCode.OK, status);
        var daily = ConsolidationRig.Parse<DailyDto>(body);
        Assert.Equal("stale", daily.Freshness); // descreve a observação inicial, feita antes dos commits
        Assert.Equal(true, daily.Stale);
        Assert.Equal(1, daily.UnresolvedEvents);
        Assert.Equal(10.00m, daily.Credits); // o valor novo: a consulta à projeção abriu depois dos commits
        Assert.Equal(10.00m, daily.ClosingBalance);
        Assert.Equal(1, daily.MovementCount);
    }

    [Fact(DisplayName = "RequestBudget_ExceededWhilePaused_Is503DependencyUnavailable_NotAbandoned")]
    public async Task RequestBudgetExceededWhilePausedIs503DependencyUnavailableNotAbandoned()
    {
        var logs = new CapturingLoggerProvider();
        var api = await StartApiAsync(
            failurePoints: new TestApiFailurePoints { BetweenOriginAndProjection = new Gate() }, // nunca liberada
            logger: logs,
            configureApi: o => o.RequestBudget = TimeSpan.FromMilliseconds(300));

        var watch = Stopwatch.StartNew();
        var (status, body) = await api.GetAccountAsync(CustomerA, A1, DayText(Today));
        watch.Stop();

        Assert.Equal(HttpStatusCode.ServiceUnavailable, status);
        Assert.Contains("\"code\":\"dependency_unavailable\"", body, StringComparison.Ordinal);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(3));
        Assert.Contains(logs.Messages, m => m.Contains("respondido 503", StringComparison.Ordinal));
        Assert.DoesNotContain(logs.Messages, m => m.Contains("abandonada", StringComparison.Ordinal));
    }

    [Fact(DisplayName = "RequestAborted_ByTheClient_IsLoggedAsAbandoned_NotAsDependencyFailure")]
    public async Task RequestAbortedByTheClientIsLoggedAsAbandonedNotAsDependencyFailure()
    {
        var logs = new CapturingLoggerProvider();
        var gate = new Gate();
        var api = await StartApiAsync(
            failurePoints: new TestApiFailurePoints { BetweenOriginAndProjection = gate },
            logger: logs,
            configureApi: o => o.RequestBudget = TimeSpan.FromSeconds(60));
        using var cancel = new CancellationTokenSource();

        var request = api.GetAccountAsync(CustomerA, A1, DayText(Today), cancel.Token);
        await gate.Reached.WaitAsync(TimeSpan.FromSeconds(20));
        await cancel.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);

        await ConsolidationRig.Eventually(() => Task.FromResult(logs.Messages.Any(m => m.Contains("abandonada", StringComparison.Ordinal))), "log de requisição abandonada");
        Assert.DoesNotContain(logs.Messages, m => m.Contains("respondido 503", StringComparison.Ordinal));
    }

    [Fact(DisplayName = "NotReady_WhilePhaseIsNotLive_Is503ConsolidationNotReady_AndReadyIsUnhealthy")]
    public async Task NotReadyWhilePhaseIsNotLiveIs503ConsolidationNotReady()
    {
        // Par próprio: aqui a fase precisa estar fora de live, o que a fixture compartilhada não permite.
        await using var rig = await ConsolidationRig.CreateAsync();
        await using var api = await rig.StartConsolidationApiAsync();
        using var client = new HttpClient { BaseAddress = new Uri(await ApiBaseAsync(api)) };

        var (status, body) = await api.GetAccountAsync(CustomerA, A1, DayText(Today));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, status);
        Assert.Contains("\"code\":\"consolidation_not_ready\"", body, StringComparison.Ordinal);

        using var ready = await client.GetAsync(new Uri("/health/ready", UriKind.Relative));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, ready.StatusCode);
        Assert.Equal("{\"status\":\"Unhealthy\"}", await ready.Content.ReadAsStringAsync());
        using var live = await client.GetAsync(new Uri("/health/live", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
        Assert.Equal("{\"status\":\"Healthy\"}", await live.Content.ReadAsStringAsync());
    }

    private static bool Contains(IReadOnlyList<Guid> ids, Guid id) => ids.Contains(id);

    private static Task<string> ApiBaseAsync(ConsolidationApiHandle api) => Task.FromResult(api.BaseAddress.ToString());
}
