using System.Globalization;
using Npgsql;

namespace FinancialTransactions.LoadTest;

internal sealed record AccountDifference(Guid AccountId, decimal ToolSignedSum, long ToolCount, decimal DatabaseSignedSum, long DatabaseCount);

internal sealed record SeedSnapshot(long Count, decimal SignedSum);

internal sealed record ReconciliationReport(
    bool Exact,
    long AcceptedByTool,
    long NonSeedRowsInDatabase,
    bool CountMatches,
    int MissingInDatabase,
    int ExtraInDatabase,
    int FieldMismatches,
    int AccountsCompared,
    IReadOnlyList<AccountDifference> AccountDifferences,
    int DuplicatedIdempotencyKeys,
    long InvalidAmountRows,
    SeedSnapshot SeedBefore,
    SeedSnapshot SeedAfter,
    bool SeedUntouched);

/// <summary>
/// Conferência do banco contra o que a ferramenta registrou como aceito (201). Compara identidades (conjunto de ids,
/// campo a campo) e somas assinadas por conta; um total correto não prova que cada movimento certo está lá.
/// </summary>
internal static class Reconciliation
{
    private const string SeedFilter = "idempotency_key LIKE 'seed-%'";

    public static async Task<(SeedSnapshot Seed, long NonSeed)> SnapshotAsync(NpgsqlDataSource source)
    {
        await using var connection = await source.OpenConnectionAsync().ConfigureAwait(false);
        var seed = await ScalarSeedAsync(connection).ConfigureAwait(false);
        await using var command = new NpgsqlCommand($"SELECT count(*) FROM movements WHERE NOT ({SeedFilter})", connection);
        var nonSeed = Convert.ToInt64(await command.ExecuteScalarAsync().ConfigureAwait(false), CultureInfo.InvariantCulture);
        return (seed, nonSeed);
    }

    public static async Task<ReconciliationReport> RunAsync(NpgsqlDataSource source, IReadOnlyList<AcceptedMovement> accepted, SeedSnapshot seedBefore)
    {
        await using var connection = await source.OpenConnectionAsync().ConfigureAwait(false);

        // (b) identidades: conjunto de ids e campos de cada linha não-seed.
        var byId = accepted.ToDictionary(a => a.MovementId);
        var seen = new HashSet<Guid>();
        var extra = 0;
        var mismatches = 0;
        long nonSeedRows = 0;
        await using (var rows = new NpgsqlCommand(
            $"SELECT id, account_id, type, amount, effective_at, idempotency_key FROM movements WHERE NOT ({SeedFilter})",
            connection))
        await using (var reader = await rows.ExecuteReaderAsync().ConfigureAwait(false))
        {
            while (await reader.ReadAsync().ConfigureAwait(false))
            {
                nonSeedRows++;
                var id = reader.GetGuid(0);
                if (!byId.TryGetValue(id, out var expected))
                {
                    extra++;
                    continue;
                }

                seen.Add(id);
                var matches = reader.GetGuid(1) == Population.AccountId(expected.AccountNumber)
                    && reader.GetInt16(2) == (expected.Type == "Credit" ? 1 : 2)
                    && reader.GetDecimal(3) == expected.Amount
                    && reader.GetFieldValue<DateTimeOffset>(4).UtcTicks == expected.EffectiveAt.UtcTicks
                    && reader.GetString(5) == expected.IdempotencyKey;
                if (!matches)
                {
                    mismatches++;
                }
            }
        }

        var missing = byId.Keys.Count(id => !seen.Contains(id));

        // (c) soma assinada e contagem por conta, SQL contra a soma calculada pela ferramenta.
        var database = new Dictionary<Guid, (decimal Sum, long Count)>();
        await using (var sums = new NpgsqlCommand(
            $"SELECT account_id, SUM(CASE type WHEN 1 THEN amount ELSE -amount END), count(*) FROM movements WHERE NOT ({SeedFilter}) GROUP BY account_id",
            connection))
        await using (var reader = await sums.ExecuteReaderAsync().ConfigureAwait(false))
        {
            while (await reader.ReadAsync().ConfigureAwait(false))
            {
                database[reader.GetGuid(0)] = (reader.GetDecimal(1), reader.GetInt64(2));
            }
        }

        var tool = accepted
            .GroupBy(a => Population.AccountId(a.AccountNumber))
            .ToDictionary(g => g.Key, g => (Sum: g.Sum(a => a.Type == "Credit" ? a.Amount : -a.Amount), Count: (long)g.Count()));

        var differences = new List<AccountDifference>();
        for (var account = 1; account <= Population.AccountCount; account++)
        {
            var id = Population.AccountId(account);
            var t = tool.GetValueOrDefault(id);
            var d = database.GetValueOrDefault(id);
            if (t.Sum != d.Sum || t.Count != d.Count)
            {
                differences.Add(new AccountDifference(id, t.Sum, t.Count, d.Sum, d.Count));
            }
        }

        // (d) nenhuma (conta, chave) com mais de uma linha: replay não cria linha.
        long duplicated;
        await using (var dup = new NpgsqlCommand(
            $"SELECT count(*) FROM (SELECT account_id, idempotency_key FROM movements WHERE NOT ({SeedFilter}) GROUP BY account_id, idempotency_key HAVING count(*) > 1) AS d",
            connection))
        {
            duplicated = Convert.ToInt64(await dup.ExecuteScalarAsync().ConfigureAwait(false), CultureInfo.InvariantCulture);
        }

        // (e) valor positivo e com no máximo duas casas, em todas as linhas (seed incluído).
        long invalid;
        await using (var bad = new NpgsqlCommand("SELECT count(*) FROM movements WHERE amount <= 0 OR scale(amount) > 2", connection))
        {
            invalid = Convert.ToInt64(await bad.ExecuteScalarAsync().ConfigureAwait(false), CultureInfo.InvariantCulture);
        }

        var seedAfter = await ScalarSeedAsync(connection).ConfigureAwait(false);
        var seedUntouched = seedAfter == seedBefore;
        var countMatches = nonSeedRows == accepted.Count;
        var exact = countMatches && missing == 0 && extra == 0 && mismatches == 0 && differences.Count == 0
            && duplicated == 0 && invalid == 0 && seedUntouched;

        return new ReconciliationReport(
            exact,
            accepted.Count,
            nonSeedRows,
            countMatches,
            missing,
            extra,
            mismatches,
            Population.AccountCount,
            differences,
            (int)duplicated,
            invalid,
            seedBefore,
            seedAfter,
            seedUntouched);
    }

    private static async Task<SeedSnapshot> ScalarSeedAsync(NpgsqlConnection connection)
    {
        await using var command = new NpgsqlCommand(
            $"SELECT count(*), COALESCE(SUM(CASE type WHEN 1 THEN amount ELSE -amount END), 0) FROM movements WHERE {SeedFilter}",
            connection);
        await using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        await reader.ReadAsync().ConfigureAwait(false);
        return new SeedSnapshot(reader.GetInt64(0), reader.GetDecimal(1));
    }
}
