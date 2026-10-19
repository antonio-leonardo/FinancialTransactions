using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace FinancialTransactions.LoadTest;

internal sealed record OperationSummary(
    string Operation,
    int RequestsMeasured,
    IReadOnlyDictionary<string, int> StatusCounts,
    IReadOnlyDictionary<string, int> OutcomeCounts,
    int LatencySamples,
    double? P50Ms,
    double? P95Ms,
    double? P99Ms,
    double? MaxMs,
    double ThroughputPerSecond);

internal static class Stats
{
    /// <summary>
    /// Percentil por ordenação das amostras: índice ceil(p × n) − 1 sobre o vetor ordenado. Sem interpolação nem
    /// estimativa. Devolve nulo sem amostras (nenhum número de latência sem amostra).
    /// </summary>
    public static double? Percentile(IReadOnlyList<double> sortedAscending, double p)
    {
        var n = sortedAscending.Count;
        if (n == 0)
        {
            return null;
        }

        var index = (int)Math.Ceiling(p * n) - 1;
        return sortedAscending[Math.Clamp(index, 0, n - 1)];
    }

    public static double ToMilliseconds(long ticks)
    {
        return ticks * 1000.0 / Stopwatch.Frequency;
    }

    /// <summary>Resumo por operação sobre as amostras medidas (aquecimento descartado). Latência só de requisições com resposta completa.</summary>
    public static OperationSummary Summarize(Operation operation, IReadOnlyList<Sample> samples, double measuredSeconds)
    {
        var mine = samples.Where(s => s.Measured && s.Operation == operation).ToList();
        var latencies = mine.Where(s => s.Status != 0).Select(s => ToMilliseconds(s.LatencyTicks)).Order().ToList();

        return new OperationSummary(
            operation.ToString(),
            mine.Count,
            mine.GroupBy(s => s.Status == 0 ? "sem_resposta" : s.Status.ToString(CultureInfo.InvariantCulture))
                .OrderBy(g => g.Key, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.Count()),
            mine.GroupBy(s => s.Outcome.ToString())
                .OrderBy(g => g.Key, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.Count()),
            latencies.Count,
            Percentile(latencies, 0.50),
            Percentile(latencies, 0.95),
            Percentile(latencies, 0.99),
            latencies.Count == 0 ? null : latencies[^1],
            mine.Count / measuredSeconds);
    }

    /// <summary>Latência de várias operações juntas, para metas definidas sobre "POST" (novo + replay).</summary>
    public static double? CombinedPercentile(IReadOnlyList<Sample> samples, Operation[] operations, double p, out int n)
    {
        var latencies = samples
            .Where(s => s.Measured && s.Status != 0 && operations.Contains(s.Operation))
            .Select(s => ToMilliseconds(s.LatencyTicks))
            .Order()
            .ToList();
        n = latencies.Count;
        return Percentile(latencies, p);
    }

    /// <summary>Amostras medidas, uma por linha, para que a auditoria recalcule os percentis.</summary>
    public static string SamplesCsv(IReadOnlyList<Sample> samples, long measureStartTicks)
    {
        var builder = new StringBuilder("operation,status,outcome,start_ms,latency_ms\n");
        foreach (var s in samples.Where(s => s.Measured).OrderBy(s => s.StartTicks))
        {
            builder.Append(s.Operation).Append(',')
                .Append(s.Status.ToString(CultureInfo.InvariantCulture)).Append(',')
                .Append(s.Outcome).Append(',')
                .Append(ToMilliseconds(s.StartTicks - measureStartTicks).ToString("F3", CultureInfo.InvariantCulture)).Append(',')
                .Append(ToMilliseconds(s.LatencyTicks).ToString("F3", CultureInfo.InvariantCulture)).Append('\n');
        }

        return builder.ToString();
    }
}
