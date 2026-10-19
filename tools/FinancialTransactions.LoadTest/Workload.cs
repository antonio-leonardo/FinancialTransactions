using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace FinancialTransactions.LoadTest;

internal enum Operation
{
    PostNew,
    PostReplay,
    GetAccountPosition,
    GetCustomerPosition,
}

/// <summary>
/// Classificação do resultado. Técnica: 5xx, timeout, exceção de transporte. Negócio esperado: 200 em replay com o
/// mesmo movementId. Inesperado: qualquer outra coisa (por exemplo 422 ou 401 num fluxo que deveria ser válido);
/// conta como defeito da ferramenta ou da API a investigar, nunca como "esperado".
/// </summary>
internal enum Outcome
{
    Success,
    ExpectedBusiness,
    Technical,
    Unexpected,
}

/// <summary>Uma requisição concluída. <see cref="Status"/> 0 = sem resposta (timeout ou transporte); nesse caso não há amostra de latência.</summary>
internal readonly record struct Sample(Operation Operation, int Status, long StartTicks, long LatencyTicks, Outcome Outcome, bool Measured);

/// <summary>Um POST aceito (201). O corpo enviado fica em memória para o replay e não vai para arquivo.</summary>
internal sealed record AcceptedMovement(
    Guid MovementId,
    int AccountNumber,
    string Type,
    decimal Amount,
    DateTimeOffset EffectiveAt,
    string IdempotencyKey,
    string RequestBody,
    bool Measured);

internal sealed record WorkloadResult(IReadOnlyList<Sample> Samples, IReadOnlyList<AcceptedMovement> Accepted, double MeasuredSeconds, long MeasureStartTicks);

/// <summary>
/// Laço fechado: cada trabalhador envia, espera a resposta completa e envia a próxima (sem think time).
/// Mistura: 60% POST novo, 5% POST replay, 25% GET posição por conta, 10% GET posição por cliente.
/// O gerador é determinístico por trabalhador (seed + índice); o intercalamento entre trabalhadores não é.
/// </summary>
internal sealed class Workload(Options options, HttpClient client, TokenBook tokens)
{
    private const string MediaTypeJson = "application/json";
    private readonly List<AcceptedMovement> _accepted = [];
    private readonly Lock _acceptedGate = new();

    public async Task<WorkloadResult> RunAsync()
    {
        var frequency = Stopwatch.Frequency;
        var origin = Stopwatch.GetTimestamp();
        var measureStart = origin + (options.WarmupSeconds * frequency);
        var end = measureStart + (options.DurationSeconds * frequency);

        var tasks = Enumerable.Range(0, options.Workers)
            .Select(worker => Task.Run(() => WorkerAsync(worker, measureStart, end)))
            .ToArray();
        var perWorker = await Task.WhenAll(tasks).ConfigureAwait(false);

        var samples = perWorker.SelectMany(list => list).ToList();
        var lastFinish = samples.Where(s => s.Measured).Select(s => s.StartTicks + s.LatencyTicks).DefaultIfEmpty(end).Max();
        var measuredSeconds = Math.Max(lastFinish - measureStart, 1) / (double)frequency;

        lock (_acceptedGate)
        {
            return new WorkloadResult(samples, _accepted.ToList(), measuredSeconds, measureStart);
        }
    }

    private async Task<List<Sample>> WorkerAsync(int worker, long measureStart, long end)
    {
        var random = new Random(unchecked(options.Seed + worker));
        var samples = new List<Sample>(4096);
        var counter = 0L;

        while (Stopwatch.GetTimestamp() < end)
        {
            var roll = random.Next(100);
            Sample sample;
            if (roll < 60)
            {
                sample = await PostNewAsync(random, worker, counter++, measureStart).ConfigureAwait(false);
            }
            else if (roll < 65)
            {
                // Sem nada aceito ainda, o replay não tem alvo: cai para um POST novo.
                sample = TryPickAccepted(random) is { } target
                    ? await PostReplayAsync(target, measureStart).ConfigureAwait(false)
                    : await PostNewAsync(random, worker, counter++, measureStart).ConfigureAwait(false);
            }
            else if (roll < 90)
            {
                sample = await GetPositionAsync(random, Operation.GetAccountPosition, measureStart).ConfigureAwait(false);
            }
            else
            {
                sample = await GetPositionAsync(random, Operation.GetCustomerPosition, measureStart).ConfigureAwait(false);
            }

            samples.Add(sample);
        }

        return samples;
    }

    private async Task<Sample> PostNewAsync(Random random, int worker, long counter, long measureStart)
    {
        var accountNumber = random.Next(1, Population.AccountCount + 1);
        var type = random.Next(2) == 0 ? "Credit" : "Debit";
        var amount = random.Next(1, 100000) / 100m;
        var effectiveAt = Population.RandomInstant(random);
        var key = string.Create(CultureInfo.InvariantCulture, $"load-{options.Seed}-{worker}-{counter}");
        var body = BuildBody(type, amount, effectiveAt);

        var (status, response, start, latency) = await SendPostAsync(accountNumber, key, body).ConfigureAwait(false);
        var measured = start >= measureStart;

        if (status == 201 && TryParseCreated(response, out var movementId))
        {
            var accepted = new AcceptedMovement(movementId, accountNumber, type, amount, effectiveAt, key, body, measured);
            lock (_acceptedGate)
            {
                _accepted.Add(accepted);
            }

            return new Sample(Operation.PostNew, status, start, latency, Outcome.Success, measured);
        }

        return new Sample(Operation.PostNew, status, start, latency, status == 201 ? Outcome.Unexpected : Classify(status), measured);
    }

    private async Task<Sample> PostReplayAsync(AcceptedMovement original, long measureStart)
    {
        var (status, response, start, latency) = await SendPostAsync(original.AccountNumber, original.IdempotencyKey, original.RequestBody).ConfigureAwait(false);
        var outcome = status == 200 && TryParseCreated(response, out var movementId) && movementId == original.MovementId
            ? Outcome.ExpectedBusiness
            : Classify(status);
        return new Sample(Operation.PostReplay, status, start, latency, outcome, start >= measureStart);
    }

    private async Task<Sample> GetPositionAsync(Random random, Operation operation, long measureStart)
    {
        var accountNumber = random.Next(1, Population.AccountCount + 1);
        var customerNumber = Population.CustomerOf(accountNumber);
        var asOf = FormatInstant(Population.RandomInstant(random));
        var path = operation == Operation.GetAccountPosition
            ? string.Create(CultureInfo.InvariantCulture, $"/api/v1/accounts/{Population.AccountId(accountNumber)}/position?asOf={asOf}")
            : string.Create(CultureInfo.InvariantCulture, $"/api/v1/customers/{Population.CustomerId(customerNumber)}/position?asOf={asOf}");

        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(options.BaseUrl, path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokens.For(customerNumber));
        var (status, _, start, latency) = await SendAsync(request).ConfigureAwait(false);
        return new Sample(operation, status, start, latency, status == 200 ? Outcome.Success : Classify(status), start >= measureStart);
    }

    private async Task<(int Status, byte[] Body, long Start, long Latency)> SendPostAsync(int accountNumber, string key, string body)
    {
        var path = string.Create(CultureInfo.InvariantCulture, $"/api/v1/accounts/{Population.AccountId(accountNumber)}/movements");
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(options.BaseUrl, path))
        {
            Content = new StringContent(body, Encoding.UTF8, MediaTypeJson),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokens.For(Population.CustomerOf(accountNumber)));
        request.Headers.TryAddWithoutValidation("Idempotency-Key", key);
        return await SendAsync(request).ConfigureAwait(false);
    }

    /// <summary>Latência: do envio ao corpo completo recebido. Sem retry. Timeout e falha de transporte viram status 0.</summary>
    private async Task<(int Status, byte[] Body, long Start, long Latency)> SendAsync(HttpRequestMessage request)
    {
        var start = Stopwatch.GetTimestamp();
        try
        {
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseContentRead).ConfigureAwait(false);
            var body = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
            return ((int)response.StatusCode, body, start, Stopwatch.GetTimestamp() - start);
        }
        catch (HttpRequestException)
        {
            return (0, [], start, Stopwatch.GetTimestamp() - start);
        }
        catch (TaskCanceledException)
        {
            return (0, [], start, Stopwatch.GetTimestamp() - start);
        }
    }

    private AcceptedMovement? TryPickAccepted(Random random)
    {
        lock (_acceptedGate)
        {
            return _accepted.Count == 0 ? null : _accepted[random.Next(_accepted.Count)];
        }
    }

    private static Outcome Classify(int status)
    {
        return status is 0 or >= 500 ? Outcome.Technical : Outcome.Unexpected;
    }

    private static string BuildBody(string type, decimal amount, DateTimeOffset effectiveAt)
    {
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{{\"type\":\"{type}\",\"amount\":{amount:F2},\"effectiveAt\":\"{FormatInstant(effectiveAt)}\"}}");
    }

    private static string FormatInstant(DateTimeOffset instant)
    {
        return instant.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
    }

    private static bool TryParseCreated(byte[] body, out Guid movementId)
    {
        movementId = Guid.Empty;
        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.TryGetProperty("movementId", out var element) && element.TryGetGuid(out movementId);
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
