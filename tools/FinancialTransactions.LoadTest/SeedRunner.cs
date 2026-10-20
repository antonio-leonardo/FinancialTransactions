using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Npgsql;

namespace FinancialTransactions.LoadTest;

/// <summary>Ponto de entrada dos modos novos (consolidation-open e seed-consolidation).</summary>
internal static class ConsolidationProgram
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (!ConsolidationOptions.TryParse(args, out var options, out var error) || options is null)
        {
            await Console.Error.WriteLineAsync(error).ConfigureAwait(false);
            return error.Contains("recusad", StringComparison.Ordinal) ? ExitCodes.Precondition : ExitCodes.Usage;
        }

        return options.Mode == RunMode.SeedConsolidation
            ? await SeedRunner.RunAsync(options).ConfigureAwait(false)
            : await ConsolidationRunner.RunAsync(options).ConfigureAwait(false);
    }
}

/// <summary>
/// Escreve a massa no banco próprio da origem (já migrado) e a valida por cálculo independente antes de qualquer Worker subir.
/// Movimento e evento da Outbox entram na MESMA transação; os POSTs medidos continuam atravessando a API real.
/// </summary>
internal static class SeedRunner
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static async Task<int> RunAsync(ConsolidationOptions o)
    {
        ArgumentNullException.ThrowIfNull(o);
        Directory.CreateDirectory(o.OutDirectory);
        await using var origin = NpgsqlDataSource.Create(o.OriginConnection);

        await using (var check = origin.CreateCommand("SELECT COUNT(*) FROM customers WHERE id::text LIKE 'cccccccc-0000-0000-0000-%'"))
        {
            if (Convert.ToInt64(await check.ExecuteScalarAsync().ConfigureAwait(false), CultureInfo.InvariantCulture) != 0)
            {
                await Console.Error.WriteLineAsync("Pré-condição: o banco já contém clientes de carga; use um banco novo e migrado.").ConfigureAwait(false);
                return ExitCodes.Precondition;
            }
        }

        var clock = Stopwatch.StartNew();
        await using (var connection = await origin.OpenConnectionAsync().ConfigureAwait(false))
        await using (var transaction = await connection.BeginTransactionAsync().ConfigureAwait(false))
        {
            foreach (var sql in new[] { SeedSql.Customers, SeedSql.Accounts, SeedSql.Movements, SeedSql.Outbox })
            {
                await using var command = new NpgsqlCommand(sql, connection, transaction) { CommandTimeout = 300 };
                if (sql == SeedSql.Movements)
                {
                    command.Parameters.AddWithValue("count", (long)Mass.Movements);
                }

                await command.ExecuteNonQueryAsync().ConfigureAwait(false);
            }

            await transaction.CommitAsync().ConfigureAwait(false);
        }

        var seedMs = clock.Elapsed.TotalMilliseconds;
        foreach (var table in new[] { "customers", "accounts", "movements", "consolidation_outbox" })
        {
            await using var analyze = origin.CreateCommand("ANALYZE " + table);
            await analyze.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        // Validação: o banco agrega por dia de negócio nomeado; as fórmulas calculam por outro caminho de código.
        var oracle = await OracleBook.LoadAsync(origin, CancellationToken.None).ConfigureAwait(false);
        var problems = oracle.CompareWithFormulas();
        var movements = await Count(origin, "SELECT COUNT(*) FROM movements").ConfigureAwait(false);
        var events = await Count(origin, "SELECT COUNT(*) FROM consolidation_outbox").ConfigureAwait(false);
        var withoutEvent = await Count(origin, "SELECT COUNT(*) FROM movements m WHERE NOT EXISTS (SELECT 1 FROM consolidation_outbox e WHERE e.event_id = m.id)").ConfigureAwait(false);
        var eventWithoutMovement = await Count(origin, "SELECT COUNT(*) FROM consolidation_outbox e WHERE NOT EXISTS (SELECT 1 FROM movements m WHERE m.id = e.event_id)").ConfigureAwait(false);
        var future = await Count(origin, "SELECT COUNT(*) FROM movements WHERE effective_at > now()").ConfigureAwait(false);
        var outsideRange = await Count(origin, "SELECT COUNT(*) FROM movements WHERE (effective_at AT TIME ZONE 'America/Sao_Paulo')::date NOT BETWEEN date '2026-09-01' AND date '2026-09-30'").ConfigureAwait(false);
        var perAccount = await Row(origin, "SELECT MIN(c), MAX(c), (SELECT SUM(c) FROM (SELECT account_id, COUNT(*) c FROM movements GROUP BY account_id ORDER BY c DESC LIMIT 10) t) FROM (SELECT account_id, COUNT(*) c FROM movements GROUP BY account_id) s").ConfigureAwait(false);
        var perDay = await Row(origin, "SELECT MIN(c), MAX(c), COUNT(*) FROM (SELECT account_id, (effective_at AT TIME ZONE 'America/Sao_Paulo')::date, COUNT(*) c FROM movements GROUP BY 1, 2) s").ConfigureAwait(false);

        var ok = problems.Count == 0 && movements == Mass.Movements && events == movements && withoutEvent == 0 && eventWithoutMovement == 0 && future == 0 && outsideRange == 0;
        var report = new
        {
            ok,
            o.Manifest.RunId,
            database = o.Manifest.OriginDatabase,
            seedMilliseconds = seedMs,
            customers = Mass.Customers,
            accounts = Mass.Accounts,
            movements,
            outboxEvents = events,
            movementsWithoutEvent = withoutEvent,
            eventsWithoutMovement = eventWithoutMovement,
            futureMovements = future,
            movementsOutsideSeptember = outsideRange,
            accountDayRows = oracle.RowCount,
            movementsPerAccount = new { min = perAccount[0], max = perAccount[1], hotAccountsTop10Total = perAccount[2], hotAccountRule = "n múltiplo de 5 → conta 1 + (n/5 mod 10)" },
            movementsPerAccountDay = new { min = perDay[0], max = perDay[1], rows = perDay[2] },
            independentCheck = "agregação SQL por dia America/Sao_Paulo × fórmulas determinísticas em n (Mass.Movement)",
            divergences = problems,
        };
        await File.WriteAllTextAsync(Path.Combine(o.OutDirectory, "seed_validacao.json"), JsonSerializer.Serialize(report, Json)).ConfigureAwait(false);
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"Seed: {movements} movimentos, {events} eventos, {oracle.RowCount} linhas conta/dia em {seedMs:F0} ms; validação {(ok ? "OK" : "FALHOU")}."));
        return ok ? ExitCodes.Approved : ExitCodes.Reconciliation;
    }

    private static async Task<long> Count(NpgsqlDataSource source, string sql)
    {
        await using var command = source.CreateCommand(sql);
        return Convert.ToInt64(await command.ExecuteScalarAsync().ConfigureAwait(false), CultureInfo.InvariantCulture);
    }

    private static async Task<long[]> Row(NpgsqlDataSource source, string sql)
    {
        await using var command = source.CreateCommand(sql);
        await using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        await reader.ReadAsync().ConfigureAwait(false);
        return Enumerable.Range(0, reader.FieldCount).Select(i => Convert.ToInt64(reader.GetValue(i), CultureInfo.InvariantCulture)).ToArray();
    }
}
