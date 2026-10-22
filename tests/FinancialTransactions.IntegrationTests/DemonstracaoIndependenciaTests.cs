using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text.Json;
using FinancialTransactions.Consolidation;
using Xunit.Abstractions;

namespace FinancialTransactions.IntegrationTests;

/// <summary>
/// Demonstração da independência e da recuperação como teste automatizado (antes, durante e depois), um teste por forma de interrupção.
/// Os dois rodam sobre a fixture do consolidado (hosts reais no processo de teste, dois PostgreSQL reais) e imprimem uma tabela com observações
/// reais. NÃO medem 50 req/s: o desempenho está em docs/carga-consolidado.md. Os tempos são da fixture (intervalos do laço reduzidos).
/// No teste 2 a falha é de CONEXÃO injetada por papel, com o PostgreSQL de pé (o container não é parado).
/// </summary>
public sealed class DemonstracaoIndependenciaTests(ConsolidationFixture fixture, ITestOutputHelper output) : ConsolidationTest(fixture)
{
    private enum Interruption
    {
        WorkerStopped,
        ProjectionUnreachable,
    }

    private sealed class Op(string key, Guid customer, Guid account, string type, decimal amount)
    {
        public string Key { get; } = key;

        public Guid Customer { get; } = customer;

        public Guid Account { get; } = account;

        public string Type { get; } = type;

        public decimal Amount { get; } = amount;

        public Guid? MovementId { get; set; }

        public decimal Signed => Type == "Credit" ? Amount : -Amount;
    }

    private readonly List<string> _satisfied = [];

    private static Guid[] Accounts => [A1, A2, B1];

    private static string AccountList => string.Join(",", Accounts.Select(a => $"'{a}'"));

    private static string M(decimal value) => value.ToString("0.00", CultureInfo.InvariantCulture);

    private static Guid OwnerOf(Guid account) => account == B1 ? CustomerB : CustomerA;

    [Fact(DisplayName = "Demonstracao_1_ConsolidacaoInterrompida_WorkerParado_NovosLancamentosAceitos_RetomadaReconciliada")]
    public Task Demonstracao1WorkerParado() => RunAsync(Interruption.WorkerStopped);

    [Fact(DisplayName = "Demonstracao_2_ConsolidacaoInterrompida_ProjecaoInalcancavel_NovosLancamentosAceitos_RetomadaReconciliada")]
    public Task Demonstracao2ProjecaoInalcancavel() => RunAsync(Interruption.ProjectionUnreachable);

    private void Check(bool condition, string description)
    {
        Assert.True(condition, description);
        _satisfied.Add(description);
    }

    private static List<Op> Initial(string tag) =>
    [
        new($"{tag}-i-01", CustomerA, A1, "Credit", 100.00m),
        new($"{tag}-i-02", CustomerA, A1, "Debit", 30.00m),
        new($"{tag}-i-03", CustomerA, A1, "Credit", 20.00m),
        new($"{tag}-i-04", CustomerA, A2, "Credit", 50.00m),
        new($"{tag}-i-05", CustomerA, A2, "Debit", 8.00m),
        new($"{tag}-i-06", CustomerB, B1, "Credit", 10.00m),
    ];

    private static List<Op> Window(string tag) =>
    [
        new($"{tag}-w-01", CustomerA, A1, "Credit", 40.00m),
        new($"{tag}-w-02", CustomerA, A1, "Debit", 15.00m),
        new($"{tag}-w-03", CustomerA, A2, "Credit", 25.00m),
        new($"{tag}-w-04", CustomerB, B1, "Debit", 3.00m),
        new($"{tag}-w-05", CustomerA, A1, "Credit", 12.50m),
        new($"{tag}-w-06", CustomerA, A2, "Debit", 5.50m),
        new($"{tag}-w-07", CustomerB, B1, "Credit", 60.00m),
        new($"{tag}-w-08", CustomerA, A1, "Debit", 7.25m),
        new($"{tag}-w-09", CustomerA, A2, "Credit", 9.99m),
        new($"{tag}-w-10", CustomerB, B1, "Credit", 1.01m),
    ];

    private static (decimal Credits, decimal Debits, long Count) Totals(IEnumerable<Op> ops, params Guid[] accounts)
    {
        var selected = ops.Where(o => accounts.Contains(o.Account)).ToList();
        return (selected.Where(o => o.Type == "Credit").Sum(o => o.Amount), selected.Where(o => o.Type == "Debit").Sum(o => o.Amount), selected.Count);
    }

    private async Task<long> ConfirmedAsync() =>
        (await Rig.OriginScalarAsync<long>("SELECT COUNT(*) FROM movements WHERE account_id = ANY(@a)", ("a", Accounts)));

    private async Task<long> PendingAsync() =>
        (await Rig.OriginScalarAsync<long>("SELECT COUNT(*) FROM consolidation_outbox WHERE status = 0 AND account_id = ANY(@a)", ("a", Accounts)));

    private async Task<long> ProcessedAsync() =>
        (await Rig.ProjectionScalarAsync<long>($"SELECT COUNT(*) FROM processed_events WHERE account_id IN ({AccountList})"));

    private static async Task WaitAsync(Func<Task<bool>> condition, string description, TimeSpan timeout)
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < timeout)
        {
            if (await condition())
            {
                return;
            }

            await Task.Delay(250);
        }

        Assert.Fail($"Condição não observada em {timeout.TotalSeconds:F0} s: {description}");
    }

    private async Task RowAsync(ConsolidationApiHandle api, DateOnly day, Stopwatch clock, string state, IReadOnlyList<Op> confirmed)
    {
        var (status, body) = await api.GetAccountAsync(CustomerA, A1, DayText(day));
        var observed = $"HTTP {(int)status}";
        if (status == HttpStatusCode.OK)
        {
            var daily = ConsolidationRig.Parse<DailyDto>(body);
            observed = $"{M(daily.ClosingBalance)} {daily.Freshness}";
        }

        var processed = (await ProcessedAsync()).ToString(CultureInfo.InvariantCulture);
        var expected = M(confirmed.Where(o => o.Account == A1).Sum(o => o.Signed));
        output.WriteLine($"{clock.Elapsed.TotalSeconds.ToString("F2", CultureInfo.InvariantCulture),7} | {state,-62} | {await ConfirmedAsync(),11} | {await PendingAsync(),8} | {processed,15} | {expected,17} | {observed}");
    }

    private static async Task<(DailyDto A1, DailyDto A2, DailyDto B1, CustomerDailyDto CustomerA, CustomerDailyDto CustomerB)> ReadAllAsync(ConsolidationApiHandle api, DateOnly day)
    {
        var a1 = await ReadAccountAsync(api, CustomerA, A1, day);
        var a2 = await ReadAccountAsync(api, CustomerA, A2, day);
        var b1 = await ReadAccountAsync(api, CustomerB, B1, day);
        var (statusA, bodyA) = await api.GetCustomerAsync(CustomerA, DayText(day));
        var (statusB, bodyB) = await api.GetCustomerAsync(CustomerB, DayText(day));
        Assert.True(statusA == HttpStatusCode.OK && statusB == HttpStatusCode.OK, $"GET por cliente esperado 200, obtido {(int)statusA} e {(int)statusB}");
        return (a1, a2, b1, ConsolidationRig.Parse<CustomerDailyDto>(bodyA), ConsolidationRig.Parse<CustomerDailyDto>(bodyB));
    }

    private void CheckDaily(string name, decimal credits, decimal debits, long count, decimal closing, string freshness, long? unresolved, long? failed, (decimal Credits, decimal Debits, long Count) expected)
    {
        Check(
            credits == expected.Credits && debits == expected.Debits && count == expected.Count && closing == expected.Credits - expected.Debits,
            $"{name}: créditos {M(credits)}, débitos {M(debits)}, {count} movimentos, fechamento {M(closing)} = esperado do teste");
        Check(freshness == "complete" && unresolved == 0 && failed == 0, $"{name}: freshness {freshness}, não resolvidos {unresolved}, em quarentena {failed}");
    }

    private async Task RunAsync(Interruption kind)
    {
        Use(A1, A2, B1);
        var clock = Stopwatch.StartNew();
        var tag = $"demo-{(kind == Interruption.WorkerStopped ? "w" : "p")}-{Guid.NewGuid():N}";
        var at = DateTimeOffset.UtcNow.AddMinutes(-2);
        var day = BusinessDay.Of(at);
        var initial = Initial(tag);
        var window = Window(tag);
        var workerFactory = new TestConnectionFactory(Rig.Real(applicationName: ConsolidationApplicationNames.Worker));
        var apiFactory = new TestConnectionFactory(Rig.Real(1, 1, ConsolidationApplicationNames.Api));

        output.WriteLine(kind == Interruption.WorkerStopped
            ? "DEMONSTRAÇÃO 1: o Worker (único consumidor) é parado; a escrita segue; um Worker novo retoma."
            : "DEMONSTRAÇÃO 2: a conexão com a projeção é tornada inalcançável para o Worker e a API do consolidado (PostgreSQL de pé); a escrita segue; a conexão é restaurada.");
        output.WriteLine("Tempos da fixture (intervalos do laço reduzidos); não são RTO. Não mede 50 req/s.");
        output.WriteLine("    T (s) | estado do componente                                           | confirmados | pendentes | processed_events | saldo esperado A1 | saldo observado A1");

        try
        {
            // ---------- precondição limpa (o teste não depende de outro) ----------
            Check(await ConfirmedAsync() == 0 && await PendingAsync() == 0 && await ProcessedAsync() == 0, "precondição: nenhuma massa preexistente nas contas A1, A2 e B1");

            var worker = await StartWorkerAsync(new WorkerStart { Factory = workerFactory });
            var api = await StartApiAsync(apiFactory);

            // ---------- antes ----------
            foreach (var op in initial)
            {
                var (status, id, body) = await Rig.PostAsync(op.Customer, op.Account, op.Type, op.Amount, at, op.Key);
                Assert.True(status == HttpStatusCode.Created && id is not null, $"POST inicial {op.Key}: esperado 201, obtido {(int)status}: {body}");
                op.MovementId = id;
            }

            var initialIds = initial.Select(o => o.MovementId ?? Guid.Empty).Order().ToList();
            await WaitAsync(async () => await PendingAsync() == 0 && await ProcessedAsync() == 6, "6 ids em processed_events e pending = 0", TimeSpan.FromSeconds(60));
            var processedBefore = (await Rig.ProjectionGuidsAsync($"SELECT event_id FROM processed_events WHERE account_id IN ({AccountList})")).Order().ToList();
            Check(processedBefore.SequenceEqual(initialIds), "antes: processed_events contém exatamente os 6 ids dos lançamentos iniciais");

            var before = await ReadAllAsync(api, day);
            var expectedInitialA1 = Totals(initial, A1);
            var expectedInitialA2 = Totals(initial, A2);
            var expectedInitialB1 = Totals(initial, B1);
            CheckDaily("antes, conta A1", before.A1.Credits, before.A1.Debits, before.A1.MovementCount, before.A1.ClosingBalance, before.A1.Freshness, before.A1.UnresolvedEvents, before.A1.FailedEvents, expectedInitialA1);
            CheckDaily("antes, conta A2", before.A2.Credits, before.A2.Debits, before.A2.MovementCount, before.A2.ClosingBalance, before.A2.Freshness, before.A2.UnresolvedEvents, before.A2.FailedEvents, expectedInitialA2);
            CheckDaily("antes, conta B1", before.B1.Credits, before.B1.Debits, before.B1.MovementCount, before.B1.ClosingBalance, before.B1.Freshness, before.B1.UnresolvedEvents, before.B1.FailedEvents, expectedInitialB1);
            Check(before.A1.Freshness == "complete" && before.A2.Freshness == "complete" && before.B1.Freshness == "complete"
                && before.CustomerA.Freshness == "complete" && before.CustomerB.Freshness == "complete"
                && before.A1.UnresolvedEvents == 0 && before.CustomerA.UnresolvedEvents == 0 && before.CustomerB.UnresolvedEvents == 0,
                "antes: consolidado por conta e por cliente complete, sem eventos não resolvidos");
            Check(before.CustomerA.Credits == expectedInitialA1.Credits + expectedInitialA2.Credits && before.CustomerA.Debits == expectedInitialA1.Debits + expectedInitialA2.Debits,
                "antes: cliente A = soma de A1 e A2");
            await RowAsync(api, day, clock, "S0: Worker, API do consolidado e API de lançamentos no ar; fase live", initial);

            // ---------- interrupção comprovada ----------
            try
            {
                if (kind == Interruption.WorkerStopped)
                {
                    Check(await worker.StopAsync(TimeSpan.FromSeconds(10)), "interrupção: o laço do único Worker terminou dentro do prazo");
                    Check(worker.LoopCompleted, "interrupção: LoopCompleted verdadeiro");
                    var listener = await Assert.ThrowsAnyAsync<HttpRequestException>(() => worker.GetAsync("/health/live"));
                    Check(listener is not null, "interrupção: /health/live do Worker sem resposta (conexão recusada, cliente HTTP válido)");
                    await RowAsync(api, day, clock, "interrupção comprovada: Worker parado, listener recusa conexões", initial);
                }
                else
                {
                    workerFactory.RedirectProjection(ConsolidationConnectionRole.Consumer, TestConnectionFactory.Unreachable);
                    workerFactory.RedirectProjection(ConsolidationConnectionRole.Health, TestConnectionFactory.Unreachable);
                    apiFactory.RedirectProjection(ConsolidationConnectionRole.Query, TestConnectionFactory.Unreachable);
                    apiFactory.RedirectProjection(ConsolidationConnectionRole.Health, TestConnectionFactory.Unreachable);

                    await WaitAsync(async () => (await worker.GetAsync("/health/ready")).Status == HttpStatusCode.ServiceUnavailable, "/health/ready do Worker = 503", TimeSpan.FromSeconds(30));
                    await WaitAsync(async () => (await api.GetAsync("/health/ready", null)).Status == HttpStatusCode.ServiceUnavailable, "/health/ready da API do consolidado = 503", TimeSpan.FromSeconds(30));
                    Check(true, "interrupção: /health/ready do Worker e da API do consolidado em 503");

                    var (getStatus, getBody) = await api.GetAccountAsync(CustomerA, A1, DayText(day));
                    Check(getStatus == HttpStatusCode.ServiceUnavailable && getBody.Contains("\"code\":\"dependency_unavailable\"", StringComparison.Ordinal), "interrupção: consulta ao consolidado = 503 dependency_unavailable");
                    await WaitAsync(() => Task.FromResult(worker.Diagnostics.Failures(FailureClass.Transient).Count >= 1), "tentativa falha do consumidor (classe transitória)", TimeSpan.FromSeconds(30));
                    Check(true, "interrupção: o consumidor registrou falha transitória (backoff), sem quarentena");
                    Check(await Rig.OutboxCountAsync(2) == 0, "interrupção: nenhum evento em quarentena");
                    Check(workerFactory.ProjectionsServed(ConsolidationConnectionRole.Consumer).Contains(TestConnectionFactory.Unreachable)
                        && workerFactory.ProjectionsServed(ConsolidationConnectionRole.Health).Contains(TestConnectionFactory.Unreachable)
                        && apiFactory.ProjectionsServed(ConsolidationConnectionRole.Query).Contains(TestConnectionFactory.Unreachable)
                        && apiFactory.ProjectionsServed(ConsolidationConnectionRole.Health).Contains(TestConnectionFactory.Unreachable),
                        "interrupção: os papéis Consumer/Health (Worker) e Query/Health (API) receberam a conexão inalcançável; a origem ficou intacta");
                    await RowAsync(api, day, clock, "interrupção comprovada: projeção inalcançável por papel (PostgreSQL de pé)", initial);
                }

                using var writeClient = Rig.Writes.CreateClient(CustomerA);
                using var readyBefore = await writeClient.GetAsync(new Uri("/health/ready", UriKind.Relative));
                Check(readyBefore.StatusCode == HttpStatusCode.OK, "antes da janela: readiness da API de lançamentos 200");

                // ---------- durante: a janela da falha ----------
                var confirmed = new List<Op>(initial);
                foreach (var op in window)
                {
                    var (status, id, body) = await Rig.PostAsync(op.Customer, op.Account, op.Type, op.Amount, at, op.Key);
                    Assert.True(status == HttpStatusCode.Created && id is not null, $"POST da janela {op.Key}: esperado 201, obtido {(int)status}: {body}");
                    op.MovementId = id;
                    confirmed.Add(op);
                }

                Check(window.Count(o => o.MovementId is not null) == 10, "durante: 10 de 10 lançamentos novos com 201");
                var windowIds = window.Select(o => o.MovementId ?? Guid.Empty).ToArray();
                var inOrigin = await Rig.OriginGuidsAsync("SELECT id FROM movements WHERE id = ANY(@ids)", ("ids", windowIds));
                var inOutbox = await Rig.OriginGuidsAsync("SELECT event_id FROM consolidation_outbox WHERE status = 0 AND event_id = ANY(@ids)", ("ids", windowIds));
                Check(inOrigin.Count == 10 && inOutbox.Count == 10, "durante: os 10 movimentos existem na origem e as 10 linhas da Outbox estão pendentes (status 0), por conjunto de ids");

                var inProjection = await Rig.ProjectionScalarAsync<long>("SELECT COUNT(*) FROM processed_events WHERE event_id = ANY(@ids)", ("ids", windowIds));
                Check(inProjection == 0, kind == Interruption.WorkerStopped
                    ? "durante: nenhum dos 10 ids em processed_events (consulta direta)"
                    : "durante: nenhum dos 10 ids em processed_events (consulta pelo caminho ADMINISTRATIVO do teste, fora da falha injetada; para os papéis do Worker e da API a projeção está inalcançável)");

                using var position = await writeClient.GetAsync(new Uri($"/api/v1/accounts/{A1}/position?asOf={Uri.EscapeDataString(DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture))}", UriKind.Relative));
                using var positionJson = JsonDocument.Parse(await position.Content.ReadAsStringAsync());
                var expectedA1Balance = confirmed.Where(o => o.Account == A1).Sum(o => o.Signed);
                Check(position.StatusCode == HttpStatusCode.OK && positionJson.RootElement.GetProperty("balance").GetDecimal() == expectedA1Balance,
                    $"durante: posição por instante na origem 200 com saldo {M(expectedA1Balance)} (prova da escrita, não da projeção)");

                if (kind == Interruption.WorkerStopped)
                {
                    var during = await ReadAllAsync(api, day);
                    var perAccount = new Dictionary<Guid, long> { [A1] = 4, [A2] = 3, [B1] = 3 };
                    Check(window.Count(o => o.Account == A1) == 4 && window.Count(o => o.Account == A2) == 3 && window.Count(o => o.Account == B1) == 3, "durante: a janela tem 4 lançamentos em A1, 3 em A2 e 3 em B1");
                    Check(during.A1.Freshness == "stale" && during.A1.UnresolvedEvents == perAccount[A1]
                        && during.A2.Freshness == "stale" && during.A2.UnresolvedEvents == perAccount[A2]
                        && during.B1.Freshness == "stale" && during.B1.UnresolvedEvents == perAccount[B1]
                        && during.CustomerA.Freshness == "stale" && during.CustomerA.UnresolvedEvents == 7
                        && during.CustomerB.Freshness == "stale" && during.CustomerB.UnresolvedEvents == 3,
                        "durante: consolidado 200 stale; não resolvidos A1 4, A2 3, B1 3, cliente A 7, cliente B 3");
                    Check(during.A1.Credits == expectedInitialA1.Credits && during.A1.Debits == expectedInitialA1.Debits && during.A1.MovementCount == expectedInitialA1.Count,
                        "durante: os valores do consolidado ainda são os de antes (a API respondeu, o processamento é que estava interrompido)");
                }
                else
                {
                    var failures = new List<string>();
                    foreach (var (customer, account) in new[] { (CustomerA, A1), (CustomerA, A2), (CustomerB, B1) })
                    {
                        var (s, b) = await api.GetAccountAsync(customer, account, DayText(day));
                        if (s != HttpStatusCode.ServiceUnavailable || !b.Contains("dependency_unavailable", StringComparison.Ordinal))
                        {
                            failures.Add($"conta {account}: {(int)s}");
                        }
                    }

                    foreach (var customer in new[] { CustomerA, CustomerB })
                    {
                        var (s, b) = await api.GetCustomerAsync(customer, DayText(day));
                        if (s != HttpStatusCode.ServiceUnavailable || !b.Contains("dependency_unavailable", StringComparison.Ordinal))
                        {
                            failures.Add($"cliente {customer}: {(int)s}");
                        }
                    }

                    Check(failures.Count == 0, $"durante: as 5 consultas ao consolidado respondem 503 dependency_unavailable (nunca saldo zero){(failures.Count == 0 ? string.Empty : ": " + string.Join("; ", failures))}");
                }

                await RowAsync(api, day, clock, "janela: 10 lançamentos com 201; pendentes na Outbox", confirmed);

                // ---------- retomada: só o interrompido ----------
                var restoredAt = clock.Elapsed;
                if (kind == Interruption.WorkerStopped)
                {
                    var worker2 = await StartWorkerAsync();
                    Check(!worker2.LoopCompleted, "retomada: host novo do Worker sobre os mesmos bancos (nenhum seed, limpeza ou reconstrução)");
                }
                else
                {
                    workerFactory.Restore(ConsolidationConnectionRole.Consumer);
                    workerFactory.Restore(ConsolidationConnectionRole.Health);
                    apiFactory.Restore(ConsolidationConnectionRole.Query);
                    apiFactory.Restore(ConsolidationConnectionRole.Health);
                    Check(!worker.LoopCompleted, "retomada: papéis restaurados nas duas fábricas; nenhum host reiniciado (mesmo laço do Worker)");
                }

                await WaitAsync(
                    async () => await PendingAsync() == 0 && await Rig.ProjectionScalarAsync<long>("SELECT COUNT(*) FROM processed_events WHERE event_id = ANY(@ids)", ("ids", windowIds)) == 10,
                    "pending = 0 e os 10 ids em processed_events",
                    TimeSpan.FromSeconds(120));
                var convergence = clock.Elapsed - restoredAt;
                Check(true, $"retomada: convergência em {convergence.TotalSeconds.ToString("F2", CultureInfo.InvariantCulture)} s (tempo da fixture, resolução de 250 ms)");
                await RowAsync(api, day, clock, "convergência: pending = 0 e os 10 ids na projeção", confirmed);

                // ---------- depois: reconciliação ----------
                var allIds = confirmed.Select(o => o.MovementId ?? Guid.Empty).Order().ToList();
                var movementIds = (await Rig.OriginGuidsAsync("SELECT id FROM movements WHERE account_id = ANY(@a)", ("a", Accounts))).Order().ToList();
                var outboxIds = (await Rig.OriginGuidsAsync("SELECT event_id FROM consolidation_outbox WHERE status = 1 AND account_id = ANY(@a)", ("a", Accounts))).Order().ToList();
                var projectedIds = (await Rig.ProjectionGuidsAsync($"SELECT event_id FROM processed_events WHERE account_id IN ({AccountList})")).Order().ToList();
                Check(allIds.Count == 16 && movementIds.SequenceEqual(allIds) && outboxIds.SequenceEqual(allIds) && projectedIds.SequenceEqual(allIds) && allIds.Distinct().Count() == 16,
                    "depois: movements = Outbox processada = processed_events = os mesmos 16 ids, sem duplicados");

                foreach (var account in Accounts)
                {
                    var expected = Totals(confirmed, account);
                    var originCredits = await Rig.OriginScalarAsync<decimal>("SELECT COALESCE(SUM(amount), 0) FROM movements WHERE account_id = @a AND type = 1", ("a", account));
                    var originDebits = await Rig.OriginScalarAsync<decimal>("SELECT COALESCE(SUM(amount), 0) FROM movements WHERE account_id = @a AND type = 2", ("a", account));
                    var originCount = await Rig.OriginScalarAsync<long>("SELECT COUNT(*) FROM movements WHERE account_id = @a", ("a", account));
                    var projCredits = await Rig.ProjectionScalarAsync<decimal>("SELECT COALESCE(SUM(credits), 0) FROM daily_account_consolidation WHERE account_id = @a", ("a", account));
                    var projDebits = await Rig.ProjectionScalarAsync<decimal>("SELECT COALESCE(SUM(debits), 0) FROM daily_account_consolidation WHERE account_id = @a", ("a", account));
                    var projCount = await Rig.ProjectionScalarAsync<long>("SELECT COALESCE(SUM(movement_count), 0)::bigint FROM daily_account_consolidation WHERE account_id = @a", ("a", account));
                    Check(
                        originCredits == expected.Credits && originDebits == expected.Debits && originCount == expected.Count
                        && projCredits == expected.Credits && projDebits == expected.Debits && projCount == expected.Count,
                        $"depois, conta {account}: origem = projeção = esperado do teste (créditos {M(expected.Credits)}, débitos {M(expected.Debits)}, {expected.Count} movimentos)");
                }

                var after = await ReadAllAsync(api, day);
                var expectedA1 = Totals(confirmed, A1);
                var expectedA2 = Totals(confirmed, A2);
                var expectedB1 = Totals(confirmed, B1);
                CheckDaily("depois, conta A1", after.A1.Credits, after.A1.Debits, after.A1.MovementCount, after.A1.ClosingBalance, after.A1.Freshness, after.A1.UnresolvedEvents, after.A1.FailedEvents, expectedA1);
                CheckDaily("depois, conta A2", after.A2.Credits, after.A2.Debits, after.A2.MovementCount, after.A2.ClosingBalance, after.A2.Freshness, after.A2.UnresolvedEvents, after.A2.FailedEvents, expectedA2);
                CheckDaily("depois, conta B1", after.B1.Credits, after.B1.Debits, after.B1.MovementCount, after.B1.ClosingBalance, after.B1.Freshness, after.B1.UnresolvedEvents, after.B1.FailedEvents, expectedB1);
                Check(after.CustomerA.Credits == expectedA1.Credits + expectedA2.Credits && after.CustomerA.Debits == expectedA1.Debits + expectedA2.Debits && after.CustomerA.MovementCount == expectedA1.Count + expectedA2.Count
                    && after.CustomerB.Credits == expectedB1.Credits && after.CustomerB.Debits == expectedB1.Debits && after.CustomerB.MovementCount == expectedB1.Count,
                    "depois: totais por cliente (A = A1 + A2; B = B1) iguais ao esperado do teste");
                Check(new[] { after.A1.Freshness, after.A2.Freshness, after.B1.Freshness, after.CustomerA.Freshness, after.CustomerB.Freshness }.All(f => f == "complete")
                    && after.A1.UnresolvedEvents == 0 && after.A2.UnresolvedEvents == 0 && after.B1.UnresolvedEvents == 0 && after.CustomerA.UnresolvedEvents == 0 && after.CustomerB.UnresolvedEvents == 0
                    && after.A1.FailedEvents == 0 && after.CustomerA.FailedEvents == 0 && after.CustomerB.FailedEvents == 0,
                    "depois: as 5 consultas complete, unresolvedEvents 0, failedEvents 0");

                // ---------- idempotência ----------
                var repeated = window[0];
                var countsBefore = (await ConfirmedAsync(), await Rig.OriginScalarAsync<long>("SELECT COUNT(*) FROM consolidation_outbox WHERE account_id = ANY(@a)", ("a", Accounts)));
                var (repeatStatus, repeatId, _) = await Rig.PostAsync(repeated.Customer, repeated.Account, repeated.Type, repeated.Amount, at, repeated.Key);
                var countsAfter = (await ConfirmedAsync(), await Rig.OriginScalarAsync<long>("SELECT COUNT(*) FROM consolidation_outbox WHERE account_id = ANY(@a)", ("a", Accounts)));
                var reread = await ReadAllAsync(api, day);
                Check(repeatStatus == HttpStatusCode.OK && repeatId == repeated.MovementId && countsBefore == (16L, 16L) && countsAfter == (16L, 16L)
                    && reread.A1.Credits == after.A1.Credits && reread.A1.Debits == after.A1.Debits && reread.A1.MovementCount == after.A1.MovementCount
                    && reread.CustomerA.Net == after.CustomerA.Net && reread.CustomerB.Net == after.CustomerB.Net,
                    "idempotência: repetir uma operação da janela devolve 200 e o mesmo movementId; 16 movimentos e 16 eventos antes e depois; consolidado inalterado");

                using var readyAfter = await writeClient.GetAsync(new Uri("/health/ready", UriKind.Relative));
                Check(readyAfter.StatusCode == HttpStatusCode.OK, "depois: readiness da API de lançamentos 200");
                await RowAsync(api, day, clock, "depois: reconciliação concluída; repetição sem efeito", confirmed);
            }
            finally
            {
                // Qualquer que seja o desfecho, nenhuma falha injetada permanece nas fábricas.
                workerFactory.Restore(ConsolidationConnectionRole.Consumer);
                workerFactory.Restore(ConsolidationConnectionRole.Health);
                apiFactory.Restore(ConsolidationConnectionRole.Query);
                apiFactory.Restore(ConsolidationConnectionRole.Health);
            }

            output.WriteLine(string.Empty);
            output.WriteLine($"RESUMO: {_satisfied.Count} verificações satisfeitas.");
            foreach (var line in _satisfied)
            {
                output.WriteLine($"  [ok] {line}");
            }
        }
        catch (Exception ex)
        {
            output.WriteLine($"FALHOU depois de {_satisfied.Count} verificações satisfeitas, aos {clock.Elapsed.TotalSeconds.ToString("F2", CultureInfo.InvariantCulture)} s: {ex.GetType().Name}: {ex.Message}");
            throw;
        }
    }
}
