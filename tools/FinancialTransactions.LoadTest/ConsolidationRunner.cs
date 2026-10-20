using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Npgsql;

namespace FinancialTransactions.LoadTest;

/// <summary>Códigos de saída do modo consolidado. 0 só para ensaio válido E aprovado.</summary>
internal static class ExitCodes
{
    public const int Approved = 0;
    public const int Usage = 1;
    public const int Precondition = 3;
    public const int Rejected = 10;
    public const int Reconciliation = 20;
    public const int Invalid = 30;

    /// <summary>Um só mapeamento do resultado para o código de saída (o JSON de resumo usa o mesmo valor). Inválido prevalece; depois, reconciliação; depois, reprovação.</summary>
    public static int For(RunOutcome outcome, bool reconciliationOrResolutionFailed) =>
        outcome switch
        {
            RunOutcome.Invalid => Invalid,
            _ when reconciliationOrResolutionFailed => Reconciliation,
            RunOutcome.Rejected => Rejected,
            _ => Approved,
        };
}

internal sealed record BacklogPoint(double TMs, long Pending, long Quarantine, double OldestPendingSeconds);

internal sealed record ProbeRow(double TMs, int Target, int Status, double LatencyMs, string Freshness);

internal sealed record LedgerEntry(int Index, string Key, int AccountNumber, string Type, long Cents, string EffectiveAt, string FirstResult, int FirstStatus, Guid? MovementId, string Resolution);

internal sealed record FinalReconciliationReport(
    bool Exact,
    long OriginMovements,
    long ExpectedMovements,
    int ExpectedRows,
    int OriginRows,
    int ProjectionRows,
    int OriginDifferences,
    int ProjectionDifferences,
    int ProjectionNetMismatches,
    int LedgerIdsMissingInOrigin,
    int LedgerIdsExtraInOrigin,
    int DuplicatedKeys,
    long OutboxEvents,
    long OutboxPending,
    long OutboxQuarantine,
    long ProcessedEvents,
    int ApiChecks,
    int ApiMismatches,
    IReadOnlyList<string> Samples);

/// <summary>Orquestra um ensaio do consolidado em chegadas abertas: pré-condições, aquecimento, janela medida, escrita, convergência e reconciliação.</summary>
internal static class ConsolidationRunner
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static async Task<int> RunAsync(ConsolidationOptions o)
    {
        ArgumentNullException.ThrowIfNull(o);
        if (!TokenBook.TryCreate(Environment.GetEnvironmentVariable("FT__Auth__SigningKey"), out var tokens, out var tokenError) || tokens is null)
        {
            await Console.Error.WriteLineAsync(tokenError).ConfigureAwait(false);
            return ExitCodes.Usage;
        }

        Directory.CreateDirectory(o.OutDirectory);
        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cancellation.Cancel();
        };

        await using var origin = NpgsqlDataSource.Create(o.OriginConnection);
        await using var projection = NpgsqlDataSource.Create(o.ProjectionConnection);
        using var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            MaxConnectionsPerServer = Math.Max(o.MaxInFlightGet, o.MaxInFlightPost) + 8,
            ConnectTimeout = TimeSpan.FromSeconds(2),
        };
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };

        // ---- pré-condições: nada começa sobre massa duvidosa
        var exactMode = o.Scenario is "L1" or "L2";
        OracleBook oracle;
        try
        {
            oracle = await OracleBook.LoadAsync(origin, cancellation.Token).ConfigureAwait(false);
        }
        catch (NpgsqlException ex)
        {
            await Console.Error.WriteLineAsync($"Pré-condição: origem inalcançável ({ex.GetType().Name}).").ConfigureAwait(false);
            return ExitCodes.Precondition;
        }

        var problems = oracle.CompareWithFormulas();
        var state = await ReadStateAsync(origin, projection, cancellation.Token).ConfigureAwait(false);
        var readiness = new List<string>();
        foreach (var url in o.ReadUrls.Concat(o.WriteUrl is null ? [] : [o.WriteUrl]))
        {
            var ready = await ReadyAsync(client, url, cancellation.Token).ConfigureAwait(false);
            readiness.Add(string.Create(CultureInfo.InvariantCulture, $"{url.Authority}={ready}"));
        }

        var preconditionFailures = new List<string>();
        preconditionFailures.AddRange(problems);
        if (state.Movements != o.Manifest.ExpectedSeedMovements)
        {
            preconditionFailures.Add($"movimentos na origem {state.Movements} × manifesto {o.Manifest.ExpectedSeedMovements}");
        }

        if (state.Pending != 0 || state.Quarantine != 0)
        {
            preconditionFailures.Add($"pendentes={state.Pending}, quarentena={state.Quarantine}");
        }

        if (state.Processed != state.Outbox || state.Outbox != state.Movements)
        {
            preconditionFailures.Add($"movimentos={state.Movements}, eventos={state.Outbox}, recibos={state.Processed}");
        }

        if (state.Phase != "live")
        {
            preconditionFailures.Add($"fase do bootstrap = {state.Phase ?? "ausente"}");
        }

        preconditionFailures.AddRange(readiness.Where(r => !r.EndsWith("=200", StringComparison.Ordinal)).Select(r => "readiness " + r));
        await WriteJsonAsync(o, "precondicoes.json", new { ok = preconditionFailures.Count == 0, failures = preconditionFailures, state, readiness, seedRows = oracle.RowCount }).ConfigureAwait(false);
        if (preconditionFailures.Count > 0)
        {
            await Console.Error.WriteLineAsync("Pré-condição não atendida: " + string.Join("; ", preconditionFailures)).ConfigureAwait(false);
            return ExitCodes.Precondition;
        }

        // ---- manifesto de execução, gravado ANTES da primeira requisição
        var getSettings = new OpenLoopSettings(o.ArrivalRate, o.EmissionTolerance, o.UsefulDeadline, o.GetTransportTimeout, o.MaxInFlightGet);
        var postSettings = new OpenLoopSettings(o.WriteRate, o.EmissionTolerance, o.UsefulDeadline, o.PostTransportTimeout, o.MaxInFlightPost);
        await WriteJsonAsync(
            o,
            "manifesto_execucao.json",
            new
            {
                o.Manifest.RunId,
                o.Scenario,
                o.Repetition,
                o.Seed,
                getRatePerSecond = o.ArrivalRate,
                postRatePerSecond = o.Scenario is "L3" or "F08" ? o.WriteRate : 0,
                durationSeconds = o.DurationSeconds,
                warmupSeconds = o.Scenario == "F08" ? 0 : o.WarmupSeconds,
                readTargets = o.ReadUrls.Select(u => u.Authority).ToArray(),
                writeTarget = o.WriteUrl?.Authority,
                usefulDeadlineMs = o.UsefulDeadline.TotalMilliseconds,
                emissionToleranceMs = o.EmissionTolerance.TotalMilliseconds,
                getTransportTimeoutMs = o.GetTransportTimeout.TotalMilliseconds,
                postTransportTimeoutMs = o.PostTransportTimeout.TotalMilliseconds,
                maxInFlightGet = o.MaxInFlightGet,
                maxInFlightPost = o.MaxInFlightPost,
                convergenceTimeoutSeconds = o.ConvergenceTimeout.TotalSeconds,
                lossBudgetPercent = Verdicts.LossBudgetPercent,
                profileBandPerSecond = new { lower = getSettings.ProfileLower, upper = getSettings.ProfileUpper },
                percentileRule = "amostras ordenadas, índice ceil(p·n)−1; latência HTTP = envio ao corpo completo; latência desde a programação = instante programado ao corpo completo; só requisições com resposta",
                massa = new { customers = Mass.Customers, accounts = Mass.Accounts, movements = Mass.Movements, firstDay = Mass.FirstDay.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), days = Mass.Days, hotAccounts = Mass.HotAccounts },
                startedUtc = DateTimeOffset.UtcNow,
            }).ConfigureAwait(false);

        var getCount = (int)Math.Round(o.ArrivalRate * o.DurationSeconds);
        var postCount = (int)Math.Round(o.WriteRate * o.DurationSeconds);
        var withWrites = o.Scenario is "L3" or "F08";

        // ---- aquecimento: só leituras, fora dos slots medidos
        object? warmupSummary = null;
        if (o.Scenario != "F08" && o.WarmupSeconds > 0)
        {
            var warmSlots = Scenarios.Reads(o, tokens, exactMode ? oracle : null, (int)Math.Round(o.ArrivalRate * o.WarmupSeconds), seedOffset: 7919);
            var warm = await new OpenLoopRunner(client, TimeProvider.System, getSettings).RunAsync(warmSlots, cancellation.Token).ConfigureAwait(false);
            var warmEval = Verdicts.Evaluate(warm, requireProfile: false);
            warmupSummary = new { warmEval.Total.Programmed, warmEval.Total.Emitted, warmEval.Total.Useful, warmEval.Total.NotEmitted, warmEval.Total.InvalidBodies, note = "descartado do aceite; só aquece conexões, pools e planos" };
            await WriteJsonAsync(o, "aquecimento.json", warmupSummary).ConfigureAwait(false);
        }

        // ---- janela medida
        var intents = withWrites ? Scenarios.Intents(o, postCount) : [];
        var confirmed = new ConcurrentDictionary<int, Guid>();
        var getSlots = o.Scenario == "F08" ? [] : Scenarios.Reads(o, tokens, exactMode ? oracle : null, getCount, seedOffset: 0);
        var postSlots = withWrites ? Scenarios.Writes(o, tokens, intents, confirmed) : [];

        using var windowToken = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token);
        var sampler = new BacklogSampler(origin);
        var samplerTask = withWrites ? sampler.RunAsync(windowToken.Token) : Task.CompletedTask;
        var probes = new ConcurrentBag<ProbeRow>();
        var probeTask = o.Scenario == "F08" ? ProbeAsync(client, o, tokens, probes, windowToken.Token) : Task.CompletedTask;

        if (o.StartSignalFile is not null)
        {
            await File.WriteAllTextAsync(o.StartSignalFile, DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture)).ConfigureAwait(false);
        }

        var windowStartUtc = DateTimeOffset.UtcNow;
        var getTask = getSlots.Count == 0 ? Task.FromResult<OpenLoopResult?>(null) : RunWindowAsync(new OpenLoopRunner(client, TimeProvider.System, getSettings), getSlots, cancellation.Token);
        var postTask = postSlots.Count == 0 ? Task.FromResult<OpenLoopResult?>(null) : RunWindowAsync(new OpenLoopRunner(client, TimeProvider.System, postSettings), postSlots, cancellation.Token);
        var getRun = await getTask.ConfigureAwait(false);
        var postRun = await postTask.ConfigureAwait(false);
        var producerEndUtc = DateTimeOffset.UtcNow;
        if (o.Scenario == "F08")
        {
            await windowToken.CancelAsync().ConfigureAwait(false);
            await probeTask.ConfigureAwait(false);
        }

        var getEval = getRun is null ? null : Verdicts.Evaluate(getRun, requireProfile: true);
        var postEval = postRun is null ? null : Verdicts.Evaluate(postRun, requireProfile: true);

        // ---- escrita: resolução fora da janela, convergência e reconciliação
        ConvergenceResult? convergence = null;
        FinalReconciliationReport? reconciliation = null;
        var ledger = new List<LedgerEntry>();
        if (withWrites && postRun is not null)
        {
            ledger = await ResolveAsync(client, o, tokens, intents, postRun, confirmed, cancellation.Token).ConfigureAwait(false);
            convergence = await WaitConvergenceAsync(origin, projection, o.ConvergenceTimeout, producerEndUtc, cancellation.Token).ConfigureAwait(false);
            await windowToken.CancelAsync().ConfigureAwait(false);
            await samplerTask.ConfigureAwait(false);
            reconciliation = await ReconcileWithRetryAsync(origin, projection, client, o, tokens, intents, confirmed, cancellation.Token).ConfigureAwait(false);
        }

        // ---- saídas
        if (getRun is not null)
        {
            await File.WriteAllTextAsync(Path.Combine(o.OutDirectory, "amostras_get.csv"), SlotCsv(getRun.Slots)).ConfigureAwait(false);
        }

        if (postRun is not null)
        {
            await File.WriteAllTextAsync(Path.Combine(o.OutDirectory, "amostras_post.csv"), SlotCsv(postRun.Slots)).ConfigureAwait(false);
            await File.WriteAllLinesAsync(Path.Combine(o.OutDirectory, "ledger.jsonl"), ledger.Select(l => JsonSerializer.Serialize(l, Json))).ConfigureAwait(false);
            await File.WriteAllTextAsync(Path.Combine(o.OutDirectory, "backlog.csv"), BacklogCsv(sampler.Points)).ConfigureAwait(false);
        }

        if (!probes.IsEmpty)
        {
            await File.WriteAllTextAsync(Path.Combine(o.OutDirectory, "probes.csv"), ProbeCsv(probes)).ConfigureAwait(false);
        }

        // ---- decisão
        var unresolved = ledger.Count(l => l.MovementId is null);
        var outcome = Decide(o, getEval, postEval, unresolved, convergence, reconciliation, out var invalidReasons, out var rejectReasons);
        var exit = ExitCodes.For(outcome, reconciliation is { Exact: false } || convergence is { Converged: false } || unresolved > 0);

        await WriteJsonAsync(
            o,
            "resumo.json",
            new
            {
                o.Manifest.RunId,
                o.Scenario,
                o.Repetition,
                outcome = outcome.ToString(),
                exitCode = exit,
                invalidReasons,
                rejectReasons,
                windowStartUtc,
                producerEndUtc,
                get = getEval is null ? null : new { getEval.Total, getEval.Routes, getEval.AchievedRatePerSecond, emittedPerSecond = getEval.EmittedPerSecond, getEval.InvalidReasons, windowMs = getRun?.WindowMs },
                post = postEval is null ? null : new { postEval.Total, postEval.AchievedRatePerSecond, emittedPerSecond = postEval.EmittedPerSecond, postEval.InvalidReasons, windowMs = postRun?.WindowMs },
                posts = withWrites ? new { planned = intents.Count, confirmed = ledger.Count(l => l.MovementId is not null), unresolved, resolvedAfterWindow = ledger.Count(l => l.Resolution != "janela") } : null,
                backlog = withWrites ? new { points = sampler.Points.Count, maxPending = sampler.Points.Select(p => p.Pending).DefaultIfEmpty(0).Max(), maxQuarantine = sampler.Points.Select(p => p.Quarantine).DefaultIfEmpty(0).Max(), maxOldestPendingSeconds = sampler.Points.Select(p => p.OldestPendingSeconds).DefaultIfEmpty(0).Max() } : null,
                convergence,
                reconciliation,
                probes = probes.IsEmpty ? null : ProbeSummary(probes),
                warmup = warmupSummary,
            }).ConfigureAwait(false);

        Console.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"{o.Scenario} r{o.Repetition}: {outcome}; GET programadas={getEval?.Total.Programmed ?? 0} úteis={getEval?.Total.Useful ?? 0} perda={(getEval?.Total.LossRate ?? 0) * 100:F2}%; POST confirmados={ledger.Count(l => l.MovementId is not null)}/{intents.Count}; saída={exit}."));
        return exit;
    }

    private static async Task<OpenLoopResult?> RunWindowAsync(OpenLoopRunner runner, IReadOnlyList<SlotSpec> slots, CancellationToken cancellationToken) =>
        await runner.RunAsync(slots, cancellationToken).ConfigureAwait(false);

    private static RunOutcome Decide(
        ConsolidationOptions o,
        Evaluation? get,
        Evaluation? post,
        int unresolved,
        ConvergenceResult? convergence,
        FinalReconciliationReport? reconciliation,
        out List<string> invalidReasons,
        out List<string> rejectReasons)
    {
        invalidReasons = [];
        rejectReasons = [];
        if (get is { Outcome: RunOutcome.Invalid })
        {
            invalidReasons.AddRange(get.InvalidReasons.Select(r => "GET: " + r));
        }

        if (post is { Outcome: RunOutcome.Invalid })
        {
            invalidReasons.AddRange(post.InvalidReasons.Select(r => "POST: " + r));
        }

        if (invalidReasons.Count > 0)
        {
            return RunOutcome.Invalid;
        }

        if (get is { Outcome: RunOutcome.Rejected })
        {
            rejectReasons.Add(string.Create(CultureInfo.InvariantCulture, $"GET: perda {get.Total.LossRate * 100:F2}% ou integridade ({get.Total.IntegrityFailures} falhas)"));
        }

        if (o.Scenario is "L3" or "F08" && post is not null)
        {
            // POST fora do orçamento de 5%, mas toda escrita planejada precisa ter sido aceita com 201 na janela (F08: "POSTs continuam 201").
            if (post.Total.HttpErrors + post.Total.InvalidBodies + post.Total.Transport > 0)
            {
                rejectReasons.Add($"POST: {post.Total.HttpErrors} erro(s) HTTP, {post.Total.InvalidBodies} corpo(s) inválido(s), {post.Total.Transport} falha(s) de transporte na janela");
            }

            if (unresolved > 0)
            {
                rejectReasons.Add($"POST: {unresolved} operação(ões) sem resolução");
            }

            if (convergence is { Converged: false })
            {
                rejectReasons.Add("convergência não ocorreu no prazo");
            }

            if (reconciliation is { Exact: false })
            {
                rejectReasons.Add("reconciliação final divergente");
            }
        }

        return rejectReasons.Count > 0 ? RunOutcome.Rejected : RunOutcome.Approved;
    }

    private sealed record StateRow(long Movements, long Outbox, long Pending, long Quarantine, long Processed, string? Phase);

    private static async Task<StateRow> ReadStateAsync(NpgsqlDataSource origin, NpgsqlDataSource projection, CancellationToken cancellationToken)
    {
        var movements = await ScalarAsync(origin, "SELECT COUNT(*) FROM movements", cancellationToken).ConfigureAwait(false);
        var outbox = await ScalarAsync(origin, "SELECT COUNT(*) FROM consolidation_outbox", cancellationToken).ConfigureAwait(false);
        var pending = await ScalarAsync(origin, "SELECT COUNT(*) FROM consolidation_outbox WHERE status = 0", cancellationToken).ConfigureAwait(false);
        var quarantine = await ScalarAsync(origin, "SELECT COUNT(*) FROM consolidation_outbox WHERE status = 2", cancellationToken).ConfigureAwait(false);
        var processed = await ScalarAsync(projection, "SELECT COUNT(*) FROM processed_events", cancellationToken).ConfigureAwait(false);
        await using var command = projection.CreateCommand("SELECT phase FROM consolidation_bootstrap WHERE id = 1");
        var phase = (await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)) as string;
        return new StateRow(movements, outbox, pending, quarantine, processed, phase);
    }

    private static async Task<long> ScalarAsync(NpgsqlDataSource source, string sql, CancellationToken cancellationToken)
    {
        await using var command = source.CreateCommand(sql);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
    }

    private static async Task<int> ReadyAsync(HttpClient client, Uri baseUrl, CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            using var response = await client.GetAsync(new Uri(baseUrl, "/health/ready"), timeout.Token).ConfigureAwait(false);
            return (int)response.StatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        {
            return 0;
        }
    }

    // ---------------------------------------------------------------- escrita: resolução e convergência

    /// <summary>
    /// Depois da janela, toda intenção sem 201 confirmado é repetida com a MESMA chave e o MESMO corpo, uma a uma, fora do orçamento. 201 ou 200 com
    /// o mesmo movementId confirmam; qualquer outra coisa deixa a intenção sem resolução (e reprova). A falha original nunca vira sucesso de desempenho.
    /// </summary>
    private static async Task<List<LedgerEntry>> ResolveAsync(
        HttpClient client,
        ConsolidationOptions o,
        TokenBook tokens,
        List<PostIntent> intents,
        OpenLoopResult run,
        ConcurrentDictionary<int, Guid> confirmed,
        CancellationToken cancellationToken)
    {
        var url = o.WriteUrl ?? throw new InvalidOperationException("URL de escrita ausente.");
        var entries = new List<LedgerEntry>(intents.Count);
        foreach (var intent in intents)
        {
            var slot = run.Slots[intent.Index];
            var first = slot.Category == SlotCategory.NotEmitted ? "não emitido" : slot.Category.ToString();
            Guid? id = confirmed.TryGetValue(intent.Index, out var known) ? known : null;
            var resolution = "janela";
            if (id is null)
            {
                resolution = "pendente";
                for (var attempt = 0; attempt < 3 && id is null; attempt++)
                {
                    using var request = Scenarios.Post(url, intent, tokens);
                    try
                    {
                        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                        timeout.CancelAfter(TimeSpan.FromSeconds(10));
                        using var response = await client.SendAsync(request, timeout.Token).ConfigureAwait(false);
                        var body = await response.Content.ReadAsByteArrayAsync(timeout.Token).ConfigureAwait(false);
                        if (response.StatusCode is HttpStatusCode.Created or HttpStatusCode.OK && Scenarios.TryMovementId(body, out var movementId))
                        {
                            id = movementId;
                            resolution = response.StatusCode == HttpStatusCode.Created ? "repetição 201" : "repetição 200 (replay)";
                        }
                    }
                    catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
                    {
                        resolution = "sem resposta na repetição";
                    }
                }
            }

            entries.Add(new LedgerEntry(
                intent.Index,
                intent.Key,
                intent.AccountNumber,
                intent.Type,
                intent.Cents,
                intent.EffectiveAt.ToString("O", CultureInfo.InvariantCulture),
                first,
                slot.Status,
                id,
                resolution));
            if (id is { } resolved)
            {
                confirmed[intent.Index] = resolved;
            }
        }

        return entries;
    }

    internal sealed record ConvergenceResult(bool Converged, double SecondsAfterProducerEnd, double TimeoutSeconds, long PendingAtEnd, long QuarantineAtEnd, long ProcessedEvents, long OutboxEvents);

    private static async Task<ConvergenceResult> WaitConvergenceAsync(NpgsqlDataSource origin, NpgsqlDataSource projection, TimeSpan timeout, DateTimeOffset producerEnd, CancellationToken cancellationToken)
    {
        while (true)
        {
            StateRow state;
            try
            {
                state = await ReadStateAsync(origin, projection, cancellationToken).ConfigureAwait(false);
            }
            catch (NpgsqlException) when (!cancellationToken.IsCancellationRequested)
            {
                // Banco de projeção ou origem ainda voltando (F08): a leitura incerta nunca vira "convergiu"; tenta de novo até o prazo.
                var elapsed = (DateTimeOffset.UtcNow - producerEnd).TotalSeconds;
                if (elapsed >= timeout.TotalSeconds)
                {
                    return new ConvergenceResult(false, elapsed, timeout.TotalSeconds, -1, -1, -1, -1);
                }

                NpgsqlConnection.ClearAllPools();
                await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken).ConfigureAwait(false);
                continue;
            }

            var seconds = (DateTimeOffset.UtcNow - producerEnd).TotalSeconds;
            var done = state.Pending == 0 && state.Quarantine == 0 && state.Processed == state.Outbox && state.Outbox == state.Movements;
            if (done || seconds >= timeout.TotalSeconds)
            {
                return new ConvergenceResult(done, seconds, timeout.TotalSeconds, state.Pending, state.Quarantine, state.Processed, state.Outbox);
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken).ConfigureAwait(false);
        }
    }

    // ---------------------------------------------------------------- reconciliação final: ledger × origem × projeção

    /// <summary>A reconciliação lê os dois bancos; uma conexão do pool quebrada por reinício do container (F08) é refeita, nunca ignorada.</summary>
    private static async Task<FinalReconciliationReport> ReconcileWithRetryAsync(
        NpgsqlDataSource origin,
        NpgsqlDataSource projection,
        HttpClient client,
        ConsolidationOptions o,
        TokenBook tokens,
        List<PostIntent> intents,
        ConcurrentDictionary<int, Guid> confirmed,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await ReconcileAsync(origin, projection, client, o, tokens, intents, confirmed, cancellationToken).ConfigureAwait(false);
            }
            catch (NpgsqlException) when (attempt < 5 && !cancellationToken.IsCancellationRequested)
            {
                NpgsqlConnection.ClearAllPools();
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
            }
        }
    }
    private static async Task<FinalReconciliationReport> ReconcileAsync(
        NpgsqlDataSource origin,
        NpgsqlDataSource projection,
        HttpClient client,
        ConsolidationOptions o,
        TokenBook tokens,
        List<PostIntent> intents,
        ConcurrentDictionary<int, Guid> confirmed,
        CancellationToken cancellationToken)
    {
        // Esperado = fórmulas da massa + operações confirmadas do ledger (nenhuma consulta ao banco nem à API).
        var expected = new Dictionary<(Guid, DateOnly), DayTotals>();
        foreach (var ((account, day), totals) in Mass.Expected())
        {
            expected[(Population.AccountId(account), Mass.Day(day))] = totals;
        }

        foreach (var intent in intents.Where(i => confirmed.ContainsKey(i.Index)))
        {
            var key = (Population.AccountId(intent.AccountNumber), Mass.Day(intent.LocalDay));
            var entry = new DayTotals(intent.Type == "Credit" ? intent.Cents : 0, intent.Type == "Debit" ? intent.Cents : 0, 1);
            expected[key] = expected.TryGetValue(key, out var current) ? current.Add(entry) : entry;
        }

        var originBook = await OracleBook.LoadAsync(origin, cancellationToken).ConfigureAwait(false);
        var originRows = originBook.Rows().ToDictionary(p => p.Key, p => p.Value);
        var projectionRows = new Dictionary<(Guid, DateOnly), DayTotals>();
        var netMismatches = 0;
        await using (var command = projection.CreateCommand("SELECT account_id, business_day, credits, debits, net, movement_count FROM daily_account_consolidation"))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var totals = new DayTotals(OracleBook.Cents(reader.GetDecimal(2)), OracleBook.Cents(reader.GetDecimal(3)), reader.GetInt64(5));
                if (OracleBook.Cents(reader.GetDecimal(4)) != totals.NetCents)
                {
                    netMismatches++;
                }

                projectionRows[(reader.GetGuid(0), DateOnly.FromDateTime(reader.GetDateTime(1)))] = totals;
            }
        }

        var samples = new List<string>();
        var originDiff = Differences(expected, originRows, "origem", samples);
        var projectionDiff = Differences(expected, projectionRows, "projeção", samples);

        var movements = await ScalarAsync(origin, "SELECT COUNT(*) FROM movements", cancellationToken).ConfigureAwait(false);
        var outbox = await ScalarAsync(origin, "SELECT COUNT(*) FROM consolidation_outbox", cancellationToken).ConfigureAwait(false);
        var pending = await ScalarAsync(origin, "SELECT COUNT(*) FROM consolidation_outbox WHERE status = 0", cancellationToken).ConfigureAwait(false);
        var quarantine = await ScalarAsync(origin, "SELECT COUNT(*) FROM consolidation_outbox WHERE status = 2", cancellationToken).ConfigureAwait(false);
        var processed = await ScalarAsync(projection, "SELECT COUNT(*) FROM processed_events", cancellationToken).ConfigureAwait(false);
        var duplicated = (int)await ScalarAsync(origin, "SELECT COUNT(*) FROM (SELECT idempotency_key FROM movements GROUP BY idempotency_key HAVING COUNT(*) > 1) d", cancellationToken).ConfigureAwait(false);

        var originIds = new HashSet<Guid>();
        await using (var command = origin.CreateCommand("SELECT id FROM movements WHERE idempotency_key LIKE 'p08c-w-%'"))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                originIds.Add(reader.GetGuid(0));
            }
        }

        var ledgerIds = confirmed.Values.ToHashSet();
        var missingIds = ledgerIds.Count(id => !originIds.Contains(id));
        var extraIds = originIds.Count(id => !ledgerIds.Contains(id));
        var expectedMovements = o.Manifest.ExpectedSeedMovements + (long)ledgerIds.Count;

        // Leitura final pela API, depois da convergência: exata e complete sobre o estado esperado.
        var finalBook = OracleBook.FromRows(expected);
        var random = new Random(unchecked(o.Seed + 424_242));
        var apiMismatches = 0;
        var checks = 0;
        for (; checks < o.FinalApiChecks; checks++)
        {
            var accountNumber = 1 + random.Next(Mass.Accounts);
            var day = Mass.Day(1 + random.Next(Mass.Days));
            var slot = Scenarios.AccountRead(o.ReadUrls[checks % o.ReadUrls.Count], checks % o.ReadUrls.Count, accountNumber, day, tokens, finalBook, checks);
            using var request = slot.CreateRequest();
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(10));
                using var response = await client.SendAsync(request, timeout.Token).ConfigureAwait(false);
                var body = await response.Content.ReadAsByteArrayAsync(timeout.Token).ConfigureAwait(false);
                if (response.StatusCode != HttpStatusCode.OK || !slot.Validate(200, body).Valid)
                {
                    apiMismatches++;
                    if (samples.Count < 30)
                    {
                        samples.Add($"api conta {accountNumber} dia {day:yyyy-MM-dd}: status {(int)response.StatusCode} ou corpo diferente do esperado");
                    }
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
            {
                apiMismatches++;
            }
        }

        var exact = originDiff == 0 && projectionDiff == 0 && netMismatches == 0 && missingIds == 0 && extraIds == 0 && duplicated == 0
            && movements == expectedMovements && outbox == movements && processed == movements && pending == 0 && quarantine == 0 && apiMismatches == 0;
        return new FinalReconciliationReport(exact, movements, expectedMovements, expected.Count, originRows.Count, projectionRows.Count, originDiff, projectionDiff, netMismatches, missingIds, extraIds, duplicated, outbox, pending, quarantine, processed, checks, apiMismatches, samples);
    }

    private static int Differences(Dictionary<(Guid, DateOnly), DayTotals> expected, Dictionary<(Guid, DateOnly), DayTotals> actual, string label, List<string> samples)
    {
        var differences = 0;
        foreach (var (key, want) in expected)
        {
            if (!actual.TryGetValue(key, out var got) || got != want)
            {
                differences++;
                if (samples.Count < 30)
                {
                    samples.Add(string.Create(CultureInfo.InvariantCulture, $"{label}: {key.Item1} {key.Item2:yyyy-MM-dd} esperado {want} obtido {(actual.TryGetValue(key, out var g) ? g.ToString() : "ausente")}"));
                }
            }
        }

        differences += actual.Keys.Count(k => !expected.ContainsKey(k));
        return differences;
    }

    // ---------------------------------------------------------------- F08: sondas de leitura por alvo

    private static async Task ProbeAsync(HttpClient client, ConsolidationOptions o, TokenBook tokens, ConcurrentBag<ProbeRow> probes, CancellationToken cancellationToken)
    {
        var clock = Stopwatch.StartNew();
        while (!cancellationToken.IsCancellationRequested)
        {
            var tick = clock.Elapsed;
            var tasks = o.ReadUrls.Select(async (url, index) =>
            {
                var slot = Scenarios.AccountRead(url, index, 1, Mass.Day(15), tokens, null, 0);
                using var request = slot.CreateRequest();
                var sent = Stopwatch.GetTimestamp();
                try
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    timeout.CancelAfter(o.GetTransportTimeout);
                    using var response = await client.SendAsync(request, timeout.Token).ConfigureAwait(false);
                    var body = await response.Content.ReadAsByteArrayAsync(timeout.Token).ConfigureAwait(false);
                    var verdict = (int)response.StatusCode == 200 ? slot.Validate(200, body) : BodyVerdict.Bad("status");
                    probes.Add(new ProbeRow(tick.TotalMilliseconds, index, (int)response.StatusCode, Stopwatch.GetElapsedTime(sent).TotalMilliseconds, verdict.Valid ? verdict.Freshness ?? "-" : "inválido"));
                }
                catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
                {
                    probes.Add(new ProbeRow(tick.TotalMilliseconds, index, 0, Stopwatch.GetElapsedTime(sent).TotalMilliseconds, "sem resposta"));
                }
            }).ToList();
            await Task.WhenAll(tasks).ConfigureAwait(false);
            var wait = TimeSpan.FromSeconds(1) - (clock.Elapsed - tick);
            if (wait > TimeSpan.Zero)
            {
                try
                {
                    await Task.Delay(wait, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

    }

    private static List<object> ProbeSummary(IEnumerable<ProbeRow> probes) =>
        probes.GroupBy(p => p.Target).OrderBy(g => g.Key).Select(g => (object)new
        {
            target = g.Key,
            samples = g.Count(),
            ok200 = g.Count(p => p.Status == 200),
            unavailable503 = g.Count(p => p.Status == 503),
            noResponse = g.Count(p => p.Status == 0),
            otherStatus = g.Count(p => p.Status is not (200 or 503 or 0)),
            freshness = g.Where(p => p.Status == 200).GroupBy(p => p.Freshness).ToDictionary(x => x.Key, x => x.Count()),
            maxLatencyMs = g.Max(p => p.LatencyMs),
        }).ToList();

    // ---------------------------------------------------------------- arquivos

    private static async Task WriteJsonAsync(ConsolidationOptions o, string name, object? value) =>
        await File.WriteAllTextAsync(Path.Combine(o.OutDirectory, name), JsonSerializer.Serialize(value, Json)).ConfigureAwait(false);

    private static string SlotCsv(IReadOnlyList<SlotResult> slots)
    {
        var builder = new StringBuilder("index,route,target,key,category,status,scheduled_ms,sent_ms,emission_lateness_ms,http_ms,since_schedule_ms,freshness,body_bytes,body_valid,integrity_failure,reason\n");
        foreach (var s in slots)
        {
            builder.Append(CultureInfo.InvariantCulture, $"{s.Index},{s.Route},{s.Target},{s.Key},{s.Category},{s.Status},{s.ScheduledMs:F3},{(s.SentMs is null ? string.Empty : s.SentMs.Value.ToString("F3", CultureInfo.InvariantCulture))},{s.EmissionLatenessMs:F3},{(s.HttpLatencyMs is null ? string.Empty : s.HttpLatencyMs.Value.ToString("F3", CultureInfo.InvariantCulture))},{(s.SinceScheduleMs is null ? string.Empty : s.SinceScheduleMs.Value.ToString("F3", CultureInfo.InvariantCulture))},{s.Freshness},{s.BodyBytes},{s.BodyValid},{s.IntegrityFailure},{s.Reason.Replace(',', ';')}\n");
        }

        return builder.ToString();
    }

    private static string BacklogCsv(IEnumerable<BacklogPoint> points)
    {
        var builder = new StringBuilder("t_ms,pending,quarantine,oldest_pending_s\n");
        foreach (var p in points)
        {
            builder.Append(CultureInfo.InvariantCulture, $"{p.TMs:F0},{p.Pending},{p.Quarantine},{p.OldestPendingSeconds:F2}\n");
        }

        return builder.ToString();
    }

    private static string ProbeCsv(IEnumerable<ProbeRow> probes)
    {
        var builder = new StringBuilder("t_ms,target,status,latency_ms,freshness\n");
        foreach (var p in probes.OrderBy(p => p.TMs).ThenBy(p => p.Target))
        {
            builder.Append(CultureInfo.InvariantCulture, $"{p.TMs:F0},{p.Target},{p.Status},{p.LatencyMs:F1},{p.Freshness}\n");
        }

        return builder.ToString();
    }
}

/// <summary>Amostra a Outbox da origem a cada segundo (pendentes, quarentena, idade da pendência mais antiga): o backlog ao longo da escrita.</summary>
internal sealed class BacklogSampler(NpgsqlDataSource origin)
{
    private readonly ConcurrentQueue<BacklogPoint> _points = new();

    public IReadOnlyList<BacklogPoint> Points => _points.ToList();

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var clock = Stopwatch.StartNew();
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await using var command = origin.CreateCommand(
                    """
                    SELECT (SELECT COUNT(*) FROM consolidation_outbox WHERE status = 0),
                           (SELECT COUNT(*) FROM consolidation_outbox WHERE status = 2),
                           COALESCE((SELECT EXTRACT(EPOCH FROM (statement_timestamp() - MIN(created_at))) FROM consolidation_outbox WHERE status = 0), 0)
                    """);
                await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    _points.Enqueue(new BacklogPoint(clock.Elapsed.TotalMilliseconds, reader.GetInt64(0), reader.GetInt64(1), Convert.ToDouble(reader.GetValue(2), CultureInfo.InvariantCulture)));
                }

                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (NpgsqlException)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), CancellationToken.None).ConfigureAwait(false);
            }
        }
    }
}
