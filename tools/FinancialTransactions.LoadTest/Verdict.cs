using System.Globalization;

namespace FinancialTransactions.LoadTest;

internal enum RunOutcome
{
    /// <summary>Gerador válido, perda no orçamento e nenhuma falha de integridade.</summary>
    Approved,

    /// <summary>Gerador válido, mas a perda excedeu o orçamento ou houve falha de integridade.</summary>
    Rejected,

    /// <summary>Gerador inválido ou ensaio incompleto: não prova nem refuta o requisito.</summary>
    Invalid,
}

internal sealed record RouteSummary(
    string Route,
    int Programmed,
    int Emitted,
    int Useful,
    int Late,
    int HttpErrors,
    int InvalidBodies,
    int Transport,
    int NotEmitted,
    int IntegrityFailures,
    IReadOnlyDictionary<string, int> Statuses,
    IReadOnlyDictionary<string, int> Freshness,
    IReadOnlyDictionary<string, int> PerTarget,
    int LatencySamples,
    double? HttpP50Ms,
    double? HttpP95Ms,
    double? HttpP99Ms,
    double? HttpMaxMs,
    double? ScheduledP50Ms,
    double? ScheduledP95Ms,
    double? ScheduledP99Ms,
    double? ScheduledMaxMs,
    double LossRate,
    bool WithinBudget);

internal sealed record Evaluation(RunOutcome Outcome, IReadOnlyList<string> InvalidReasons, RouteSummary Total, IReadOnlyList<RouteSummary> Routes, double AchievedRatePerSecond, IReadOnlyList<int> EmittedPerSecond);

/// <summary>Classificação D3: contagens exclusivas, perda sobre o denominador PROGRAMADO e decisão final.</summary>
internal static class Verdicts
{
    /// <summary>Orçamento de perda em pontos percentuais (D3-P08): até 5% das consultas programadas.</summary>
    public const int LossBudgetPercent = 5;

    /// <summary>Perda dentro do orçamento: (programadas − úteis) / programadas ≤ 5%, em aritmética inteira (150 de 3.000 aprova; 151 reprova).</summary>
    public static bool WithinBudget(int programmed, int useful)
    {
        if (programmed <= 0)
        {
            return false;
        }

        return ((long)(programmed - useful) * 100) <= (long)programmed * LossBudgetPercent;
    }

    public static Evaluation Evaluate(OpenLoopResult run, bool requireProfile)
    {
        ArgumentNullException.ThrowIfNull(run);
        var routes = run.Slots.GroupBy(s => s.Route, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => Summarize(g.Key, g.ToList()))
            .ToList();
        var total = Summarize("total", run.Slots);

        var reasons = new List<string>();
        if (run.Cancelled)
        {
            reasons.Add("ensaio cancelado");
        }

        if (run.RedirectSeen)
        {
            reasons.Add("redirecionamento recebido (recusado: a ferramenta não o segue)");
        }

        if (total.Programmed == 0)
        {
            reasons.Add("nenhum slot programado");
        }

        if (total.NotEmitted > 0)
        {
            reasons.Add(string.Create(CultureInfo.InvariantCulture, $"{total.NotEmitted} slot(s) não emitido(s): a prova de taxa fica invalidada"));
        }

        if (total.Emitted == 0)
        {
            reasons.Add("nenhuma amostra");
        }

        if (requireProfile)
        {
            var outside = run.EmittedPerSecond
                .Select((count, second) => (count, second))
                .Where(x => x.count < run.Settings.ProfileLower || x.count > run.Settings.ProfileUpper)
                .ToList();
            if (outside.Count > 0)
            {
                reasons.Add(string.Create(
                    CultureInfo.InvariantCulture,
                    $"{outside.Count} segundo(s) fora de {run.Settings.ProfileLower}–{run.Settings.ProfileUpper} emissões: {string.Join(", ", outside.Take(10).Select(x => $"s{x.second}={x.count}"))}"));
            }
        }

        var outcome = reasons.Count > 0
            ? RunOutcome.Invalid
            : routes.All(r => r.WithinBudget) && total.WithinBudget && total.IntegrityFailures == 0 ? RunOutcome.Approved : RunOutcome.Rejected;
        var achieved = run.WindowMs > 0 ? total.Emitted / (run.WindowMs / 1000.0) : 0;
        return new Evaluation(outcome, reasons, total, routes, achieved, run.EmittedPerSecond);
    }

    private static RouteSummary Summarize(string route, IReadOnlyList<SlotResult> slots)
    {
        int Count(SlotCategory c) => slots.Count(s => s.Category == c);
        var emitted = slots.Count(s => s.Category != SlotCategory.NotEmitted);
        var useful = Count(SlotCategory.UsefulOnTime);
        var withResponse = slots.Where(s => s.Status != 0 && s.HttpLatencyMs is not null).ToList();
        var http = withResponse.Select(s => s.HttpLatencyMs.GetValueOrDefault()).Order().ToList();
        var scheduled = withResponse.Select(s => s.SinceScheduleMs.GetValueOrDefault()).Order().ToList();
        var programmed = slots.Count;

        return new RouteSummary(
            route,
            programmed,
            emitted,
            useful,
            Count(SlotCategory.LateSuccess),
            Count(SlotCategory.HttpError),
            Count(SlotCategory.InvalidBody),
            Count(SlotCategory.TransportFailure),
            Count(SlotCategory.NotEmitted),
            slots.Count(s => s.IntegrityFailure),
            slots.Where(s => s.Status != 0).GroupBy(s => s.Status.ToString(CultureInfo.InvariantCulture)).OrderBy(g => g.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count()),
            slots.Where(s => s.Freshness is not null).GroupBy(s => s.Freshness ?? string.Empty).OrderBy(g => g.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count()),
            slots.Where(s => s.Category != SlotCategory.NotEmitted).GroupBy(s => s.Target.ToString(CultureInfo.InvariantCulture)).OrderBy(g => g.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count()),
            http.Count,
            Stats.Percentile(http, 0.50),
            Stats.Percentile(http, 0.95),
            Stats.Percentile(http, 0.99),
            http.Count == 0 ? null : http[^1],
            Stats.Percentile(scheduled, 0.50),
            Stats.Percentile(scheduled, 0.95),
            Stats.Percentile(scheduled, 0.99),
            scheduled.Count == 0 ? null : scheduled[^1],
            programmed == 0 ? 0 : (programmed - useful) / (double)programmed,
            WithinBudget(programmed, useful));
    }
}
