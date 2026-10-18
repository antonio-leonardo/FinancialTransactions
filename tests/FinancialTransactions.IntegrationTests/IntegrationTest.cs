using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using Npgsql;

namespace FinancialTransactions.IntegrationTests;

/// <summary>Base dos testes contra a API e o banco reais. Cada teste limpa os movimentos das contas que usou.</summary>
[Collection(PostgresCollectionDefinition.Name)]
public abstract class IntegrationTest(PostgresFixture fixture) : IAsyncLifetime
{
    // IDs fixos do seed (literais, independentes do código de produção).
    protected static readonly Guid CustomerA = Guid.Parse("11111111-1111-1111-1111-111111111111");
    protected static readonly Guid CustomerB = Guid.Parse("22222222-2222-2222-2222-222222222222");
    protected static readonly Guid AccountA1 = Guid.Parse("aaaaaaa1-0000-0000-0000-000000000001");
    protected static readonly Guid AccountA2 = Guid.Parse("aaaaaaa2-0000-0000-0000-000000000002");
    protected static readonly Guid AccountB1 = Guid.Parse("bbbbbbb1-0000-0000-0000-000000000001");

    private readonly HashSet<Guid> _usedAccounts = [];

    protected PostgresFixture Fixture { get; } = fixture;

    protected HttpClient ClientFor(Guid customerId) => Fixture.Factory.CreateClient(customerId);

    protected static Uri Relative(string path) => new(path, UriKind.Relative);

    protected static string NewKey() => "it-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);

    protected static Uri PositionUri(Guid accountId, string asOf) =>
        Relative($"/api/v1/accounts/{accountId}/position?asOf={Uri.EscapeDataString(asOf)}");

    protected static Uri CustomerPositionUri(Guid customerId, string asOf) =>
        Relative($"/api/v1/customers/{customerId}/position?asOf={Uri.EscapeDataString(asOf)}");

    protected async Task<HttpResponseMessage> PostMovementAsync(HttpClient client, Guid accountId, string? idempotencyKey, object body)
    {
        ArgumentNullException.ThrowIfNull(client);
        _usedAccounts.Add(accountId);
        // A requisição só pode ser descartada depois do envio concluído; por isso o await fica aqui dentro.
        using var request = new HttpRequestMessage(HttpMethod.Post, Relative($"/api/v1/accounts/{accountId}/movements"));
        if (idempotencyKey is not null)
        {
            request.Headers.Add("Idempotency-Key", idempotencyKey);
        }

        request.Content = JsonContent.Create(body);
        return await client.SendAsync(request);
    }

    protected static object Credit(string amount, string effectiveAt, string? description = null) =>
        new { type = "Credit", amount = decimal.Parse(amount, CultureInfo.InvariantCulture), effectiveAt, description };

    protected static object Debit(string amount, string effectiveAt, string? description = null) =>
        new { type = "Debit", amount = decimal.Parse(amount, CultureInfo.InvariantCulture), effectiveAt, description };

    protected static string NowUtc(int offsetMinutes = 0) =>
        DateTimeOffset.UtcNow.AddMinutes(offsetMinutes).ToString("O", CultureInfo.InvariantCulture);

    protected static async Task<T> ReadAsync<T>(HttpResponseMessage response)
    {
        ArgumentNullException.ThrowIfNull(response);
        var value = await response.Content.ReadFromJsonAsync<T>();
        Assert.NotNull(value);
        return value;
    }

    /// <summary>Lê a extensão <c>code</c> de um ProblemDetails.</summary>
    protected static async Task<string?> ReadCodeAsync(HttpResponseMessage response)
    {
        ArgumentNullException.ThrowIfNull(response);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.TryGetProperty("code", out var code) ? code.GetString() : null;
    }

    protected async Task<long> CountMovementsAsync(Guid accountId)
    {
        await using var connection = Fixture.OpenTestConnection();
        await using var command = new NpgsqlCommand("SELECT COUNT(*) FROM movements WHERE account_id = @id", connection);
        command.Parameters.AddWithValue("id", accountId);
        var result = await command.ExecuteScalarAsync();
        return Convert.ToInt64(result, CultureInfo.InvariantCulture);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (_usedAccounts.Count == 0)
        {
            return;
        }

        await using var connection = Fixture.OpenTestConnection();
        await using var command = new NpgsqlCommand("DELETE FROM movements WHERE account_id = ANY(@ids)", connection);
        command.Parameters.AddWithValue("ids", _usedAccounts.ToArray());
        await command.ExecuteNonQueryAsync();
    }
}

internal sealed record MovementDto(Guid MovementId, Guid AccountId, string Type, decimal Amount, string Currency, DateTimeOffset EffectiveAt, DateTimeOffset RegisteredAt);

internal sealed record AccountPositionDto(Guid AccountId, DateTimeOffset AsOf, string Currency, decimal Balance, long MovementCount);

internal sealed record CustomerAccountDto(Guid AccountId, decimal Balance, long MovementCount);

internal sealed record ExcludedAccountDto(Guid AccountId, DateTimeOffset OpenedAt);

internal sealed record CustomerPositionDto(Guid CustomerId, DateTimeOffset AsOf, string Currency, decimal Balance, IReadOnlyList<CustomerAccountDto> Accounts, IReadOnlyList<ExcludedAccountDto> ExcludedAccounts);
