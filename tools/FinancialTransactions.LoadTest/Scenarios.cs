using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace FinancialTransactions.LoadTest;

/// <summary>Validação do corpo das respostas do consolidado. Modo exato (massa estável) e modo de contrato (escrita em curso).</summary>
internal static class BodyChecks
{
    private const string TimeZone = "America/Sao_Paulo";

    public static BodyVerdict Account(byte[] body, Guid account, DateOnly day, OracleBook? exact)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            var header = Header(root, "accountId", account, day);
            if (header is not null)
            {
                return BodyVerdict.Bad(header);
            }

            var credits = Money(root, "credits");
            var debits = Money(root, "debits");
            var net = Money(root, "net");
            var closing = Money(root, "closingBalance");
            var count = root.GetProperty("movementCount").GetInt64();
            if (credits is null || debits is null || net is null || closing is null || credits < 0 || debits < 0 || count < 0)
            {
                return BodyVerdict.Bad("valores financeiros ausentes, negativos ou com mais de duas casas");
            }

            if (net != credits - debits)
            {
                return BodyVerdict.Bad("aritmética interna: net diferente de credits − debits", integrity: true);
            }

            var freshness = Freshness(root, exact is not null);
            if (freshness.Problem is not null)
            {
                return BodyVerdict.Bad(freshness.Problem);
            }

            if (exact is not null)
            {
                var want = exact.Day(account, day);
                if (credits != want.CreditCents || debits != want.DebitCents || net != want.NetCents || count != want.Count || closing != exact.ClosingCents(account, day))
                {
                    return BodyVerdict.Bad("valor financeiro diferente do oracle", integrity: true);
                }
            }

            return BodyVerdict.Ok(freshness.Value);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            return BodyVerdict.Bad("corpo malformado");
        }
    }

    public static BodyVerdict Customer(byte[] body, Guid customer, int customerNumber, DateOnly day, OracleBook? exact)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            var header = Header(root, "customerId", customer, day);
            if (header is not null)
            {
                return BodyVerdict.Bad(header);
            }

            var credits = Money(root, "credits");
            var debits = Money(root, "debits");
            var net = Money(root, "net");
            var closing = Money(root, "closingBalance");
            var count = root.GetProperty("movementCount").GetInt64();
            if (credits is null || debits is null || net is null || closing is null || credits < 0 || debits < 0 || count < 0)
            {
                return BodyVerdict.Bad("valores financeiros ausentes, negativos ou com mais de duas casas");
            }

            if (net != credits - debits)
            {
                return BodyVerdict.Bad("aritmética interna: net diferente de credits − debits", integrity: true);
            }

            var accounts = root.GetProperty("accounts");
            if (accounts.GetArrayLength() != 2 || root.GetProperty("excludedAccounts").GetArrayLength() != 0)
            {
                return BodyVerdict.Bad("seleção de contas diferente das duas contas do cliente");
            }

            long sumCredits = 0, sumDebits = 0, sumCount = 0, sumClosing = 0;
            var expectedIds = new HashSet<Guid> { Population.AccountId((2 * customerNumber) - 1), Population.AccountId(2 * customerNumber) };
            foreach (var item in accounts.EnumerateArray())
            {
                var id = item.GetProperty("accountId").GetGuid();
                if (!expectedIds.Remove(id))
                {
                    return BodyVerdict.Bad("conta fora da titularidade do cliente");
                }

                var c = Money(item, "credits");
                var d = Money(item, "debits");
                var n = Money(item, "net");
                var cl = Money(item, "closingBalance");
                if (c is null || d is null || n is null || cl is null || n != c - d)
                {
                    return BodyVerdict.Bad("aritmética interna de uma conta do cliente", integrity: true);
                }

                sumCredits += c.GetValueOrDefault();
                sumDebits += d.GetValueOrDefault();
                sumClosing += cl.GetValueOrDefault();
                sumCount += item.GetProperty("movementCount").GetInt64();
                if (exact is not null)
                {
                    var want = exact.Day(id, day);
                    if (c != want.CreditCents || d != want.DebitCents || n != want.NetCents || item.GetProperty("movementCount").GetInt64() != want.Count || cl != exact.ClosingCents(id, day))
                    {
                        return BodyVerdict.Bad("valor financeiro de uma conta diferente do oracle", integrity: true);
                    }
                }
            }

            if (sumCredits != credits || sumDebits != debits || sumCount != count || sumClosing != closing)
            {
                return BodyVerdict.Bad("totais do cliente diferentes da soma das contas", integrity: true);
            }

            var freshness = Freshness(root, exact is not null);
            return freshness.Problem is not null ? BodyVerdict.Bad(freshness.Problem) : BodyVerdict.Ok(freshness.Value);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            return BodyVerdict.Bad("corpo malformado");
        }
    }

    private static string? Header(JsonElement root, string idProperty, Guid id, DateOnly day)
    {
        if (root.GetProperty(idProperty).GetGuid() != id)
        {
            return "identidade diferente da consultada";
        }

        if (root.GetProperty("day").GetString() != day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
        {
            return "dia diferente do consultado";
        }

        if (root.GetProperty("currency").GetString() != "BRL" || root.GetProperty("businessTimeZone").GetString() != TimeZone)
        {
            return "moeda ou fuso diferentes do contrato";
        }

        return root.GetProperty("dayStatus").GetString() is "open" or "closed" ? null : "dayStatus fora do contrato";
    }

    /// <summary>Centavos inteiros de um número JSON; nulo se tiver mais de duas casas.</summary>
    private static long? Money(JsonElement element, string property)
    {
        var value = element.GetProperty(property).GetDecimal();
        var cents = value * 100m;
        return cents == decimal.Truncate(cents) ? decimal.ToInt64(cents) : null;
    }

    /// <summary>
    /// Regras de freshness. Contrato: complete ⇒ nenhuma pendência no recorte; stale ⇒ há pendência ou quarentena; unknown ⇒ contadores
    /// desconhecidos. No estado estável (modo exato) só vale complete, sem quarentena.
    /// </summary>
    private static (string? Value, string? Problem) Freshness(JsonElement root, bool requireComplete)
    {
        var freshness = root.GetProperty("freshness").GetString();
        var stale = root.GetProperty("stale");
        var unresolved = root.GetProperty("unresolvedEvents");
        var failed = root.GetProperty("failedEvents");
        switch (freshness)
        {
            case "complete":
                if (stale.ValueKind != JsonValueKind.False || unresolved.ValueKind != JsonValueKind.Number || unresolved.GetInt64() != 0)
                {
                    return (null, "freshness complete incoerente com os contadores");
                }

                if (requireComplete && (failed.ValueKind != JsonValueKind.Number || failed.GetInt64() != 0))
                {
                    return (null, "quarentena no recorte em massa estável");
                }

                return (freshness, null);
            case "stale":
                if (requireComplete)
                {
                    return (null, "freshness stale em massa estável");
                }

                return stale.ValueKind == JsonValueKind.True && unresolved.ValueKind == JsonValueKind.Number && unresolved.GetInt64() > 0
                    ? (freshness, null)
                    : (null, "freshness stale incoerente com os contadores");
            case "unknown":
                if (requireComplete)
                {
                    return (null, "freshness unknown em massa estável");
                }

                return stale.ValueKind == JsonValueKind.Null && unresolved.ValueKind == JsonValueKind.Null
                    ? (freshness, null)
                    : (null, "freshness unknown incoerente com os contadores");
            default:
                return (null, "freshness fora do contrato");
        }
    }
}

/// <summary>Intenção de escrita: tudo o que é preciso para repetir exatamente a mesma operação (mesma chave, mesmo corpo).</summary>
internal sealed record PostIntent(int Index, string Key, int AccountNumber, string Type, long Cents, DateTimeOffset EffectiveAt, string Body)
{
    public int LocalDay => DateOnly.FromDateTime(EffectiveAt.ToOffset(TimeSpan.FromHours(-3)).DateTime).DayNumber - Mass.FirstDay.DayNumber + 1;
}

/// <summary>Montagem das agendas de leitura e escrita dos cenários L1, L2, L3 e F08.</summary>
internal static class Scenarios
{
    private static readonly HashSet<int> Ok200 = [200];
    private static readonly HashSet<int> Created201 = [201];

    /// <summary>Contas quentes pesam 25% das consultas por conta; o resto é uniforme. Mesma agenda para a mesma semente.</summary>
    public static IReadOnlyList<SlotSpec> Reads(ConsolidationOptions options, TokenBook tokens, OracleBook? exact, int count, int seedOffset)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(tokens);
        var random = new Random(unchecked(options.Seed + seedOffset));
        var slots = new List<SlotSpec>(count);
        for (var i = 0; i < count; i++)
        {
            var target = i % options.ReadUrls.Count;
            var day = Mass.Day(1 + random.Next(Mass.Days));
            var byCustomer = options.Scenario switch
            {
                "L2" => true,
                "L3" => i % 5 == 4,
                _ => false,
            };
            slots.Add(byCustomer
                ? CustomerRead(options.ReadUrls[target], target, 1 + random.Next(Mass.Customers), day, tokens, exact, i)
                : AccountRead(options.ReadUrls[target], target, random.Next(4) == 0 ? 1 + random.Next(Mass.HotAccounts) : 1 + random.Next(Mass.Accounts), day, tokens, exact, i));
        }

        return slots;
    }

    public static SlotSpec AccountRead(Uri baseUrl, int target, int accountNumber, DateOnly day, TokenBook tokens, OracleBook? exact, int index)
    {
        var account = Population.AccountId(accountNumber);
        var customerNumber = Population.CustomerOf(accountNumber);
        var path = string.Create(CultureInfo.InvariantCulture, $"/api/v1/accounts/{account}/consolidation/daily?day={day:yyyy-MM-dd}");
        return new SlotSpec(
            "account",
            target,
            string.Create(CultureInfo.InvariantCulture, $"g{index}"),
            Ok200,
            () => Get(baseUrl, path, tokens.For(customerNumber)),
            (_, body) => BodyChecks.Account(body, account, day, exact));
    }

    public static SlotSpec CustomerRead(Uri baseUrl, int target, int customerNumber, DateOnly day, TokenBook tokens, OracleBook? exact, int index)
    {
        var customer = Population.CustomerId(customerNumber);
        var path = string.Create(CultureInfo.InvariantCulture, $"/api/v1/customers/{customer}/consolidation/daily?day={day:yyyy-MM-dd}");
        return new SlotSpec(
            "customer",
            target,
            string.Create(CultureInfo.InvariantCulture, $"g{index}"),
            Ok200,
            () => Get(baseUrl, path, tokens.For(customerNumber)),
            (_, body) => BodyChecks.Customer(body, customer, customerNumber, day, exact));
    }

    /// <summary>Escritas planejadas: chave estável por slot (repetida pelo slot, jamais reutilizada por outro), corpo determinístico.</summary>
    public static List<PostIntent> Intents(ConsolidationOptions options, int count)
    {
        ArgumentNullException.ThrowIfNull(options);
        var random = new Random(unchecked(options.Seed ^ 0x5A5A5A));
        var intents = new List<PostIntent>(count);
        var origin = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.FromHours(-3));
        for (var i = 0; i < count; i++)
        {
            var account = 1 + random.Next(Mass.Accounts);
            var type = random.Next(2) == 0 ? "Credit" : "Debit";
            var cents = 1 + random.Next(99_999);
            var effective = origin.AddSeconds(random.NextInt64(0, Mass.Days * 86_400L)).ToUniversalTime();
            var key = string.Create(CultureInfo.InvariantCulture, $"p08c-w-{options.Scenario}-r{options.Repetition}-{i:D5}");
            var amount = string.Create(CultureInfo.InvariantCulture, $"{cents / 100}.{cents % 100:D2}");
            var body = string.Create(
                CultureInfo.InvariantCulture,
                $"{{\"type\":\"{type}\",\"amount\":{amount},\"effectiveAt\":\"{effective:yyyy-MM-dd'T'HH:mm:ss'Z'}\"}}");
            intents.Add(new PostIntent(i, key, account, type, cents, effective, body));
        }

        return intents;
    }

    /// <summary>Agenda de POSTs; o movementId devolvido pelo 201 é guardado no ledger.</summary>
    public static IReadOnlyList<SlotSpec> Writes(ConsolidationOptions options, TokenBook tokens, IReadOnlyList<PostIntent> intents, ConcurrentDictionary<int, Guid> confirmed)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(tokens);
        ArgumentNullException.ThrowIfNull(intents);
        ArgumentNullException.ThrowIfNull(confirmed);
        var url = options.WriteUrl ?? throw new InvalidOperationException("URL de escrita ausente.");
        return intents.Select(intent => new SlotSpec(
            "post",
            0,
            intent.Key,
            Created201,
            () => Post(url, intent, tokens),
            (_, body) =>
            {
                if (TryMovementId(body, out var id))
                {
                    confirmed[intent.Index] = id;
                    return BodyVerdict.Ok();
                }

                return BodyVerdict.Bad("201 sem movementId");
            })).ToList();
    }

    public static HttpRequestMessage Post(Uri baseUrl, PostIntent intent, TokenBook tokens)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        ArgumentNullException.ThrowIfNull(intent);
        ArgumentNullException.ThrowIfNull(tokens);
        var path = string.Create(CultureInfo.InvariantCulture, $"/api/v1/accounts/{Population.AccountId(intent.AccountNumber)}/movements");
        var request = new HttpRequestMessage(HttpMethod.Post, new Uri(baseUrl, path))
        {
            Content = new StringContent(intent.Body, Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokens.For(Population.CustomerOf(intent.AccountNumber)));
        request.Headers.TryAddWithoutValidation("Idempotency-Key", intent.Key);
        return request;
    }

    public static bool TryMovementId(byte[] body, out Guid id)
    {
        id = Guid.Empty;
        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.TryGetProperty("movementId", out var element) && element.TryGetGuid(out id);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static HttpRequestMessage Get(Uri baseUrl, string path, string token)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, new Uri(baseUrl, path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }
}
