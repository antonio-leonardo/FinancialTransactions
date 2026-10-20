using System.Globalization;
using System.Text.Json;
using FinancialTransactions.LoadTest;
using Npgsql;

// Ferramenta de ensaio de carga (P04). Uso:
//   dotnet run -c Release --project tools/FinancialTransactions.LoadTest -- \
//     --base-url http://127.0.0.1:<porta> --admin-connection "<conexão do banco ft_load_*>" --out <pasta> \
//     [--duration-seconds 60] [--warmup-seconds 10] [--workers 32] [--seed 20261019]
// A chave de assinatura vem de FT__Auth__SigningKey (a mesma da API). Nada sensível é gravado.
// Saída: resumo.json, aceitos.jsonl, amostras.csv e reconciliacao.json.
// Códigos: 0 ok; 1 uso; 3 pré-condição; 10 erro técnico ou resposta inesperada; 20 reconciliação divergente.
const int ExitUsage = 1;
const int ExitPrecondition = 3;
const int ExitUnexpected = 10;
const int ExitReconciliation = 20;

// Modos novos (P08, parte C): chegadas abertas contra o consolidado e seed da massa. Sem --mode, vale o laço fechado histórico do P04.
if (args.Contains("--mode", StringComparer.Ordinal))
{
    return await ConsolidationProgram.RunAsync(args).ConfigureAwait(false);
}

var jsonOptions = new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

if (!Options.TryParse(args, out var options, out var parseError) || options is null)
{
    Console.Error.WriteLine(parseError);
    return ExitUsage;
}

if (!TokenBook.TryCreate(Environment.GetEnvironmentVariable("FT__Auth__SigningKey"), out var tokens, out var tokenError) || tokens is null)
{
    Console.Error.WriteLine(tokenError);
    return ExitUsage;
}

Directory.CreateDirectory(options.OutDirectory);

await using var dataSource = NpgsqlDataSource.Create(options.AdminConnection);
var (seedBefore, nonSeedBefore) = await Reconciliation.SnapshotAsync(dataSource).ConfigureAwait(false);
if (nonSeedBefore != 0 || seedBefore.Count == 0)
{
    Console.Error.WriteLine($"Pré-condição: banco deve ter só movimentos seed (seed={seedBefore.Count}, não-seed={nonSeedBefore}).");
    return ExitPrecondition;
}

using var handler = new SocketsHttpHandler
{
    PooledConnectionLifetime = TimeSpan.FromMinutes(10),
    MaxConnectionsPerServer = options.Workers,
};
using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };

Console.WriteLine($"Ensaio: {options.Workers} trabalhadores, {options.WarmupSeconds}s de aquecimento + {options.DurationSeconds}s medidos, seed {options.Seed}.");
var result = await new Workload(options, client, tokens).RunAsync().ConfigureAwait(false);

// Os 201 do aquecimento também criaram linhas: entram na reconciliação.
var accepted = result.Accepted;
var operations = Enum.GetValues<Operation>();
var summaries = operations.Select(o => Stats.Summarize(o, result.Samples, result.MeasuredSeconds)).ToList();
var measured = result.Samples.Where(s => s.Measured).ToList();
var technicalAll = result.Samples.Count(s => s.Outcome == Outcome.Technical);
var unexpectedAll = result.Samples.Count(s => s.Outcome == Outcome.Unexpected);

var postOps = new[] { Operation.PostNew, Operation.PostReplay };
var postP95 = Stats.CombinedPercentile(result.Samples, postOps, 0.95, out var postN);
var postNew = summaries.Single(s => s.Operation == nameof(Operation.PostNew));
var getAccount = summaries.Single(s => s.Operation == nameof(Operation.GetAccountPosition));

var report = await Reconciliation.RunAsync(dataSource, accepted, seedBefore).ConfigureAwait(false);

var summary = new
{
    parameters = new
    {
        baseUrlHost = options.BaseUrl.Host,
        options.Workers,
        options.WarmupSeconds,
        options.DurationSeconds,
        options.Seed,
        mix = new { postNew = 60, postReplay = 5, getAccountPosition = 25, getCustomerPosition = 10 },
        httpTimeoutSeconds = 30,
        percentileRule = "sorted samples, index ceil(p*n)-1; latency = send to full body; only requests with a response",
    },
    totals = new
    {
        requestsWarmupDiscarded = result.Samples.Count - measured.Count,
        requestsMeasured = measured.Count,
        measuredSeconds = result.MeasuredSeconds,
        throughputPerSecond = measured.Count / result.MeasuredSeconds,
        technicalErrorsMeasured = measured.Count(s => s.Outcome == Outcome.Technical),
        technicalErrorsIncludingWarmup = technicalAll,
        technicalErrorRate = measured.Count == 0 ? 0 : measured.Count(s => s.Outcome == Outcome.Technical) / (double)measured.Count,
        unexpectedIncludingWarmup = unexpectedAll,
        acceptedCreatedIncludingWarmup = accepted.Count,
    },
    operations = summaries,
    hypotheses = new
    {
        HL1_zeroTechnicalErrors = technicalAll == 0,
        HL2_exactReconciliation = report.Exact,
        HL3_getAccountP95Below250ms = getAccount.P95Ms is { } p95a && p95a < 250,
        HL4_postP95Below400ms = postNew.P95Ms is { } p95n && p95n < 400 && postP95 is { } p95p && p95p < 400,
        postCombinedLatencySamples = postN,
        postCombinedP95Ms = postP95,
    },
};

await File.WriteAllTextAsync(Path.Combine(options.OutDirectory, "resumo.json"), JsonSerializer.Serialize(summary, jsonOptions)).ConfigureAwait(false);
await File.WriteAllTextAsync(Path.Combine(options.OutDirectory, "reconciliacao.json"), JsonSerializer.Serialize(report, jsonOptions)).ConfigureAwait(false);
await File.WriteAllTextAsync(Path.Combine(options.OutDirectory, "amostras.csv"), Stats.SamplesCsv(result.Samples, result.MeasureStartTicks)).ConfigureAwait(false);

var lines = accepted.Select(a => JsonSerializer.Serialize(
    new
    {
        movementId = a.MovementId,
        accountId = Population.AccountId(a.AccountNumber),
        type = a.Type,
        amount = a.Amount,
        effectiveAt = a.EffectiveAt,
        idempotencyKey = a.IdempotencyKey,
        phase = a.Measured ? "medicao" : "aquecimento",
    },
    new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
await File.WriteAllLinesAsync(Path.Combine(options.OutDirectory, "aceitos.jsonl"), lines).ConfigureAwait(false);

Console.WriteLine(string.Create(
    CultureInfo.InvariantCulture,
    $"Medidas {measured.Count} requisições em {result.MeasuredSeconds:F1}s; técnicos={technicalAll}; inesperados={unexpectedAll}; aceitos={accepted.Count}; reconciliação exata={report.Exact}."));

if (!report.Exact)
{
    return ExitReconciliation;
}

return technicalAll > 0 || unexpectedAll > 0 ? ExitUnexpected : 0;
