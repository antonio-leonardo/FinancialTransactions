using System.Globalization;
using Npgsql;

namespace FinancialTransactions.LoadTest;

/// <summary>Valores em centavos inteiros: nunca ponto flutuante para dinheiro.</summary>
internal readonly record struct DayTotals(long CreditCents, long DebitCents, long Count)
{
    public long NetCents => CreditCents - DebitCents;

    public DayTotals Add(DayTotals other) => new(CreditCents + other.CreditCents, DebitCents + other.DebitCents, Count + other.Count);
}

/// <summary>
/// Massa reproduzível do ensaio P08: 200 clientes, 400 contas, 100.000 movimentos entre 01/09/2026 e 30/09/2026 (horário de Brasília),
/// com fórmulas determinísticas em n. As MESMAS fórmulas estão no SQL do seed (<see cref="SeedSql"/>); a validação compara o que o banco
/// agrega (por dia de negócio America/Sao_Paulo) com o que estas fórmulas calculam, em dois caminhos de código independentes.
/// </summary>
internal static class Mass
{
    public const int Customers = 200;
    public const int Accounts = 400;
    public const int Movements = 100_000;
    public const int Days = 30;
    public const int HotAccounts = 10;

    /// <summary>Primeiro dia (local) da massa.</summary>
    public static readonly DateOnly FirstDay = new(2026, 9, 1);

    public static DateOnly Day(int dayNumber) => FirstDay.AddDays(dayNumber - 1);

    /// <summary>Conta (1..400), tipo (1 crédito, 2 débito), centavos (1..99999) e segundo do mês local, para o movimento n (1..100000).</summary>
    public static (int Account, int Type, long Cents, long SecondOfMonth) Movement(long n)
    {
        // 20% dos movimentos vão às 10 contas quentes; o resto se espalha pelas 400.
        var account = n % 5 == 0 ? 1 + (int)((n / 5) % HotAccounts) : 1 + (int)((((n - (n / 5)) * 37) + 11) % Accounts);
        var type = ((n * 13) % 7) < 4 ? 1 : 2;
        var cents = 1 + (((n * 7919) + 17) % 99_999);
        var second = ((n * 104_729) + 12_345) % (Days * 86_400L);
        return (account, type, cents, second);
    }

    /// <summary>Totais esperados por (conta, dia 1..30) calculados só pelas fórmulas.</summary>
    public static Dictionary<(int Account, int Day), DayTotals> Expected()
    {
        var map = new Dictionary<(int, int), DayTotals>();
        for (long n = 1; n <= Movements; n++)
        {
            var (account, type, cents, second) = Movement(n);
            var day = 1 + (int)(second / 86_400);
            var entry = new DayTotals(type == 1 ? cents : 0, type == 2 ? cents : 0, 1);
            map[(account, day)] = map.TryGetValue((account, day), out var current) ? current.Add(entry) : entry;
        }

        return map;
    }
}

/// <summary>SQL do seed. Movimento e evento da Outbox são gravados na MESMA transação, com o mesmo id (event_id = id do movimento).</summary>
internal static class SeedSql
{
    public const string Customers = """
        INSERT INTO customers (id, name, created_at)
        SELECT ('cccccccc-0000-0000-0000-' || lpad(to_hex(n), 12, '0'))::uuid,
               'Carga ' || lpad(n::text, 3, '0'),
               timestamptz '2026-01-01 00:00:00+00'
        FROM generate_series(1, 200) AS n
        """;

    public const string Accounts = """
        INSERT INTO accounts (id, customer_id, currency, opened_at)
        SELECT ('dddddddd-0000-0000-0000-' || lpad(to_hex(n), 12, '0'))::uuid,
               ('cccccccc-0000-0000-0000-' || lpad(to_hex((n + 1) / 2), 12, '0'))::uuid,
               'BRL',
               timestamptz '2026-01-01 00:00:00+00'
        FROM generate_series(1, 400) AS n
        """;

    public const string Movements = """
        WITH m AS (
            SELECT n,
                   CASE WHEN n % 5 = 0 THEN 1 + ((n / 5) % 10) ELSE 1 + (((n - n / 5) * 37 + 11) % 400) END AS acct,
                   CASE WHEN (n * 13) % 7 < 4 THEN 1 ELSE 2 END AS typ,
                   1 + ((n * 7919 + 17) % 99999) AS cents,
                   timestamptz '2026-09-01 00:00:00-03' + (((n * 104729 + 12345) % 2592000) * interval '1 second') AS at
            FROM generate_series(1::bigint, @count::bigint) AS n)
        INSERT INTO movements (id, account_id, type, amount, currency, effective_at, registered_at, idempotency_key, request_fingerprint, description)
        SELECT md5('p08c-mov-' || n)::uuid,
               ('dddddddd-0000-0000-0000-' || lpad(to_hex(acct::int), 12, '0'))::uuid,
               typ::smallint,
               cents::numeric / 100,
               'BRL',
               at,
               at,
               'p08c-seed-' || n,
               encode(sha256(convert_to('p08c-seed-' || n, 'UTF8')), 'hex'),
               NULL
        FROM m
        """;

    public const string Outbox = """
        INSERT INTO consolidation_outbox (event_id, account_id, customer_id, type, amount, effective_at, created_at, origin, status, attempts)
        SELECT m.id, m.account_id, a.customer_id, m.type, m.amount, m.effective_at, m.registered_at, 0, 0, 0
        FROM movements m
        JOIN accounts a ON a.id = m.account_id
        ORDER BY m.registered_at, m.id
        """;

    /// <summary>Agregação independente por dia de negócio nomeado do PostgreSQL (fonte do oracle dos GETs).</summary>
    public const string Aggregates = """
        SELECT account_id,
               (effective_at AT TIME ZONE 'America/Sao_Paulo')::date AS business_day,
               COALESCE(SUM(CASE WHEN type = 1 THEN amount END), 0),
               COALESCE(SUM(CASE WHEN type = 2 THEN amount END), 0),
               COUNT(*)
        FROM movements
        GROUP BY account_id, (effective_at AT TIME ZONE 'America/Sao_Paulo')::date
        """;
}

/// <summary>
/// Oracle financeiro dos GETs: agregação SQL da ORIGEM por conta e dia. Vale para massa estável (L1/L2) e é a referência da
/// reconciliação final de L3 somada ao ledger. Nunca é calculado pela API, pelo applier nem por BusinessDay.
/// </summary>
internal sealed class OracleBook
{
    private readonly Dictionary<Guid, SortedDictionary<DateOnly, DayTotals>> _days = [];

    public static async Task<OracleBook> LoadAsync(NpgsqlDataSource origin, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(origin);
        var book = new OracleBook();
        await using var command = origin.CreateCommand(SeedSql.Aggregates);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var account = reader.GetGuid(0);
            var day = DateOnly.FromDateTime(reader.GetDateTime(1));
            var totals = new DayTotals(Cents(reader.GetDecimal(2)), Cents(reader.GetDecimal(3)), reader.GetInt64(4));
            if (!book._days.TryGetValue(account, out var map))
            {
                map = [];
                book._days[account] = map;
            }

            map[day] = totals;
        }

        return book;
    }

    public static long Cents(decimal value) => decimal.ToInt64(decimal.Round(value * 100m, 0, MidpointRounding.ToEven));

    public static OracleBook FromRows(IEnumerable<KeyValuePair<(Guid Account, DateOnly Day), DayTotals>> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var book = new OracleBook();
        foreach (var ((account, day), totals) in rows)
        {
            if (!book._days.TryGetValue(account, out var map))
            {
                map = [];
                book._days[account] = map;
            }

            map[day] = totals;
        }

        return book;
    }

    public int RowCount => _days.Values.Sum(d => d.Count);

    public DayTotals Day(Guid account, DateOnly day) =>
        _days.TryGetValue(account, out var map) && map.TryGetValue(day, out var totals) ? totals : default;

    /// <summary>Fechamento: soma acumulada do líquido até o dia, inclusive.</summary>
    public long ClosingCents(Guid account, DateOnly day) =>
        _days.TryGetValue(account, out var map) ? map.Where(p => p.Key <= day).Sum(p => p.Value.NetCents) : 0;

    public IEnumerable<KeyValuePair<(Guid Account, DateOnly Day), DayTotals>> Rows() =>
        _days.SelectMany(a => a.Value.Select(d => new KeyValuePair<(Guid, DateOnly), DayTotals>((a.Key, d.Key), d.Value)));

    /// <summary>Compara o que o banco agrega com o que as fórmulas calculam. Devolve as divergências (vazio = seed válido).</summary>
    public List<string> CompareWithFormulas()
    {
        var expected = Mass.Expected();
        var problems = new List<string>();
        foreach (var ((account, day), want) in expected)
        {
            var got = Day(Population.AccountId(account), Mass.Day(day));
            if (got != want && problems.Count < 20)
            {
                problems.Add(string.Create(CultureInfo.InvariantCulture, $"conta {account} dia {day}: banco {got} × fórmula {want}"));
            }
        }

        if (RowCount != expected.Count)
        {
            problems.Add(string.Create(CultureInfo.InvariantCulture, $"linhas conta/dia: banco {RowCount} × fórmula {expected.Count}"));
        }

        return problems;
    }
}
