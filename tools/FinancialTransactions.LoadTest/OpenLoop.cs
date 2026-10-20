using System.Globalization;

namespace FinancialTransactions.LoadTest;

/// <summary>Categoria final e exclusiva de cada slot programado. Perda = tudo que não é <see cref="UsefulOnTime"/>.</summary>
internal enum SlotCategory
{
    /// <summary>Status de sucesso, corpo válido e concluído em até o prazo útil desde o instante PROGRAMADO.</summary>
    UsefulOnTime,

    /// <summary>Status de sucesso e corpo válido, porém concluído depois do prazo útil.</summary>
    LateSuccess,

    /// <summary>Resposta recebida com status que não é o de sucesso da rota.</summary>
    HttpError,

    /// <summary>Status de sucesso com corpo que não atende ao contrato do cenário (inclui valor financeiro diferente do oracle).</summary>
    InvalidBody,

    /// <summary>Falha de transporte, timeout de transporte ou conexão recusada.</summary>
    TransportFailure,

    /// <summary>O slot não foi emitido: atrasou além da tolerância de emissão ou a capacidade em voo estava esgotada.</summary>
    NotEmitted,
}

/// <summary>Resultado da validação do corpo de uma resposta de sucesso.</summary>
internal sealed record BodyVerdict(bool Valid, string Reason, string? Freshness, bool IntegrityFailure)
{
    public static BodyVerdict Ok(string? freshness = null) => new(true, string.Empty, freshness, false);

    public static BodyVerdict Bad(string reason, bool integrity = false) => new(false, reason, null, integrity);
}

/// <summary>Um slot da agenda: como montar a requisição e como validar a resposta. O slot não conhece o relógio.</summary>
internal sealed record SlotSpec(string Route, int Target, string Key, IReadOnlySet<int> SuccessStatuses, Func<HttpRequestMessage> CreateRequest, Func<int, byte[], BodyVerdict> Validate);

internal sealed record OpenLoopSettings(
    double RatePerSecond,
    TimeSpan EmissionTolerance,
    TimeSpan UsefulDeadline,
    TimeSpan TransportTimeout,
    int MaxInFlight)
{
    /// <summary>Faixa de emissões por segundo aceita para a prova de taxa (−10% / +10% da taxa programada).</summary>
    public int ProfileLower => (int)Math.Floor(Math.Round(RatePerSecond * 0.9, 6));

    public int ProfileUpper => (int)Math.Ceiling(Math.Round(RatePerSecond * 1.1, 6));
}

internal sealed record SlotResult(
    int Index,
    string Route,
    int Target,
    string Key,
    SlotCategory Category,
    int Status,
    double ScheduledMs,
    double? SentMs,
    double EmissionLatenessMs,
    double? HttpLatencyMs,
    double? SinceScheduleMs,
    string? Freshness,
    int BodyBytes,
    bool BodyValid,
    string Reason,
    bool IntegrityFailure);

internal sealed record OpenLoopResult(
    IReadOnlyList<SlotResult> Slots,
    bool Cancelled,
    bool RedirectSeen,
    double WindowMs,
    IReadOnlyList<int> EmittedPerSecond,
    OpenLoopSettings Settings);

/// <summary>
/// Gerador de chegadas ABERTAS. A agenda é do relógio monotônico (slot i em i / taxa) e nunca espera a resposta anterior: o laço que
/// programa só dorme até o próximo slot, enquanto cada requisição corre em sua própria tarefa. Slot cuja emissão atrasa mais que a
/// tolerância, ou que encontra o teto em voo esgotado, é NÃO EMITIDO: nenhuma rajada compensa o atraso e a janela não é prolongada.
/// </summary>
internal sealed class OpenLoopRunner(HttpClient client, TimeProvider time, OpenLoopSettings settings)
{
    public async Task<OpenLoopResult> RunAsync(IReadOnlyList<SlotSpec> slots, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(slots);
        var results = new SlotResult[slots.Count];
        var frequency = (double)time.TimestampFrequency;
        var start = time.GetTimestamp();
        var inFlight = 0;
        var cancelled = false;
        var redirectSeen = false;
        var tasks = new List<Task>(slots.Count);

        for (var i = 0; i < slots.Count; i++)
        {
            var dueTicks = start + (long)(i * frequency / settings.RatePerSecond);
            var scheduledMs = (dueTicks - start) * 1000.0 / frequency;
            try
            {
                await WaitUntilAsync(dueTicks, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                cancelled = true;
                for (var rest = i; rest < slots.Count; rest++)
                {
                    results[rest] = NotEmitted(slots[rest], rest, (start + (long)(rest * frequency / settings.RatePerSecond) - start) * 1000.0 / frequency, 0, "ensaio cancelado");
                }

                break;
            }

            var now = time.GetTimestamp();
            var latenessMs = (now - dueTicks) * 1000.0 / frequency;
            if (latenessMs > settings.EmissionTolerance.TotalMilliseconds)
            {
                results[i] = NotEmitted(slots[i], i, scheduledMs, latenessMs, "emissão atrasou além da tolerância");
                continue;
            }

            if (Volatile.Read(ref inFlight) >= settings.MaxInFlight)
            {
                results[i] = NotEmitted(slots[i], i, scheduledMs, latenessMs, "capacidade em voo esgotada");
                continue;
            }

            Interlocked.Increment(ref inFlight);
            var index = i;
            tasks.Add(SendAsync());

            async Task SendAsync()
            {
                try
                {
                    var (result, redirect) = await ExecuteAsync(slots[index], index, start, dueTicks, scheduledMs, latenessMs, cancellationToken).ConfigureAwait(false);
                    results[index] = result;
                    if (redirect)
                    {
                        redirectSeen = true;
                    }

                    if (result.Reason == "ensaio cancelado")
                    {
                        cancelled = true;
                    }
                }
                finally
                {
                    Interlocked.Decrement(ref inFlight);
                }
            }
        }

        await Task.WhenAll(tasks).ConfigureAwait(false);
        var windowMs = time.GetElapsedTime(start).TotalMilliseconds;
        var seconds = (int)Math.Ceiling(slots.Count / settings.RatePerSecond);
        var perSecond = new int[Math.Max(seconds, 1)];
        foreach (var r in results.Where(r => r.SentMs is not null))
        {
            var bucket = (int)Math.Floor(r.SentMs.GetValueOrDefault() / 1000.0);
            if (bucket >= 0 && bucket < perSecond.Length)
            {
                perSecond[bucket]++;
            }
        }

        return new OpenLoopResult(results, cancelled, redirectSeen, windowMs, perSecond, settings);
    }

    private async Task WaitUntilAsync(long dueTicks, CancellationToken cancellationToken)
    {
        var frequency = (double)time.TimestampFrequency;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var remainingMs = (dueTicks - time.GetTimestamp()) * 1000.0 / frequency;
            if (remainingMs <= 0)
            {
                return;
            }

            if (remainingMs > 3)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(remainingMs - 2), time, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await Task.Yield();
            }
        }
    }

    private static SlotResult NotEmitted(SlotSpec spec, int index, double scheduledMs, double latenessMs, string reason) =>
        new(index, spec.Route, spec.Target, spec.Key, SlotCategory.NotEmitted, 0, scheduledMs, null, latenessMs, null, null, null, 0, false, reason, false);

    private async Task<(SlotResult Result, bool Redirect)> ExecuteAsync(
        SlotSpec spec,
        int index,
        long start,
        long dueTicks,
        double scheduledMs,
        double latenessMs,
        CancellationToken cancellationToken)
    {
        var frequency = (double)time.TimestampFrequency;
        var sent = time.GetTimestamp();
        var sentMs = (sent - start) * 1000.0 / frequency;
        using var request = spec.CreateRequest();
        using var timeout = new CancellationTokenSource(settings.TransportTimeout, time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linked.Token).ConfigureAwait(false);
            var body = await response.Content.ReadAsByteArrayAsync(linked.Token).ConfigureAwait(false);
            var ended = time.GetTimestamp();
            var httpMs = (ended - sent) * 1000.0 / frequency;
            var sinceScheduleMs = (ended - dueTicks) * 1000.0 / frequency;
            var status = (int)response.StatusCode;
            var redirect = status is >= 300 and < 400;

            if (!spec.SuccessStatuses.Contains(status))
            {
                return (Build(SlotCategory.HttpError, status, null, body.Length, false, $"status {status.ToString(CultureInfo.InvariantCulture)}", false), redirect);
            }

            var verdict = spec.Validate(status, body);
            if (!verdict.Valid)
            {
                return (Build(SlotCategory.InvalidBody, status, verdict.Freshness, body.Length, false, verdict.Reason, verdict.IntegrityFailure), false);
            }

            var category = sinceScheduleMs <= settings.UsefulDeadline.TotalMilliseconds ? SlotCategory.UsefulOnTime : SlotCategory.LateSuccess;
            return (Build(category, status, verdict.Freshness, body.Length, true, string.Empty, false), false);

            SlotResult Build(SlotCategory c, int s, string? freshness, int bytes, bool valid, string reason, bool integrity) =>
                new(index, spec.Route, spec.Target, spec.Key, c, s, scheduledMs, sentMs, latenessMs, httpMs, sinceScheduleMs, freshness, bytes, valid, reason, integrity);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return (new SlotResult(index, spec.Route, spec.Target, spec.Key, SlotCategory.TransportFailure, 0, scheduledMs, sentMs, latenessMs, null, null, null, 0, false, "ensaio cancelado", false), false);
        }
        catch (OperationCanceledException)
        {
            return (new SlotResult(index, spec.Route, spec.Target, spec.Key, SlotCategory.TransportFailure, 0, scheduledMs, sentMs, latenessMs, null, null, null, 0, false, "timeout de transporte", false), false);
        }
        catch (HttpRequestException ex)
        {
            return (new SlotResult(index, spec.Route, spec.Target, spec.Key, SlotCategory.TransportFailure, 0, scheduledMs, sentMs, latenessMs, null, null, null, 0, false, "falha de transporte: " + ex.GetType().Name, false), false);
        }
    }
}
