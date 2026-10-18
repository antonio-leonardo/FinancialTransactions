using System.Net;

namespace FinancialTransactions.IntegrationTests;

public sealed class CustomerPositionAndIdempotencyTests(PostgresFixture fixture) : IntegrationTest(fixture)
{
    private async Task SeedTwoAccountsAsync(HttpClient client)
    {
        using var a1 = await PostMovementAsync(client, AccountA1, NewKey(), Credit("50.00", "2026-03-15T12:00:00Z"));
        using var a2 = await PostMovementAsync(client, AccountA2, NewKey(), Credit("25.00", "2026-03-15T12:00:00Z"));
        Assert.Equal(HttpStatusCode.Created, a1.StatusCode);
        Assert.Equal(HttpStatusCode.Created, a2.StatusCode);
    }

    [Fact(DisplayName = "T02d_CustomerPosition_SumsOpenAccounts")]
    public async Task T02dCustomerPositionSumsOpenAccounts()
    {
        using var client = ClientFor(CustomerA);
        await SeedTwoAccountsAsync(client);

        using var response = await client.GetAsync(CustomerPositionUri(CustomerA, "2026-04-01T00:00:00Z"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await ReadAsync<CustomerPositionDto>(response);
        Assert.Equal(75.00m, body.Balance);
        Assert.Equal(2, body.Accounts.Count);
        Assert.Contains(body.Accounts, a => a.AccountId == AccountA1 && a.Balance == 50.00m && a.MovementCount == 1);
        Assert.Contains(body.Accounts, a => a.AccountId == AccountA2 && a.Balance == 25.00m && a.MovementCount == 1);
        Assert.Empty(body.ExcludedAccounts);

        // Escopo: B não enxerga A, A não enxerga B.
        using var clientB = ClientFor(CustomerB);
        using var asB = await clientB.GetAsync(CustomerPositionUri(CustomerA, "2026-04-01T00:00:00Z"));
        Assert.Equal(HttpStatusCode.NotFound, asB.StatusCode);
        Assert.Equal("not_found", await ReadCodeAsync(asB));
        using var aAsksB = await client.GetAsync(CustomerPositionUri(CustomerB, "2026-04-01T00:00:00Z"));
        Assert.Equal(HttpStatusCode.NotFound, aAsksB.StatusCode);
    }

    [Fact(DisplayName = "T02e_CustomerPosition_ExcludesAccountsOpenedAfterAsOf")]
    public async Task T02eCustomerPositionExcludesAccountsOpenedAfterAsOf()
    {
        using var client = ClientFor(CustomerA);
        await SeedTwoAccountsAsync(client);

        using var response = await client.GetAsync(CustomerPositionUri(CustomerA, "2026-02-15T00:00:00Z"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await ReadAsync<CustomerPositionDto>(response);
        // Em 15/02 só A1 existe e o crédito de 15/03 ainda não aconteceu: saldo 0 de uma conta aberta, não história insuficiente.
        Assert.Equal(0.00m, body.Balance);
        var only = Assert.Single(body.Accounts);
        Assert.Equal(AccountA1, only.AccountId);
        Assert.Equal(0, only.MovementCount);
        var excluded = Assert.Single(body.ExcludedAccounts);
        Assert.Equal(AccountA2, excluded.AccountId);
        Assert.Equal(new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero), excluded.OpenedAt);

        using var afterCredit = await client.GetAsync(CustomerPositionUri(CustomerA, "2026-03-20T00:00:00Z"));
        Assert.Equal(HttpStatusCode.OK, afterCredit.StatusCode);
        Assert.Equal(75.00m, (await ReadAsync<CustomerPositionDto>(afterCredit)).Balance);

        using var tooEarly = await client.GetAsync(CustomerPositionUri(CustomerA, "2025-12-15T00:00:00Z"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, tooEarly.StatusCode);
        Assert.Equal("historico_insuficiente", await ReadCodeAsync(tooEarly));
    }

    [Fact(DisplayName = "T05_SameKeySameBody_ReplaysSameMovement")]
    public async Task T05SameKeySameBodyReplaysSameMovement()
    {
        using var client = ClientFor(CustomerA);
        var key = NewKey();
        var body = Credit("10.50", "2026-09-10T12:00:00-03:00", "mesma intenção");

        using var first = await PostMovementAsync(client, AccountA1, key, body);
        using var second = await PostMovementAsync(client, AccountA1, key, body);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var created = await ReadAsync<MovementDto>(first);
        var replayed = await ReadAsync<MovementDto>(second);
        Assert.Equal(created.MovementId, replayed.MovementId);
        Assert.Equal(created, replayed);
        Assert.Equal(1, await CountMovementsAsync(AccountA1));
    }

    [Fact(DisplayName = "T05b_SameKeyDifferentBody_IsConflict_NoEffect")]
    public async Task T05bSameKeyDifferentBodyIsConflictNoEffect()
    {
        using var client = ClientFor(CustomerA);
        var key = NewKey();

        using var first = await PostMovementAsync(client, AccountA1, key, Credit("10.50", "2026-09-10T12:00:00-03:00"));
        using var conflict = await PostMovementAsync(client, AccountA1, key, Credit("99.00", "2026-09-10T12:00:00-03:00"));

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, conflict.StatusCode);
        Assert.Equal("idempotency_key_conflict", await ReadCodeAsync(conflict));
        Assert.Equal(1, await CountMovementsAsync(AccountA1));

        using var position = await client.GetAsync(PositionUri(AccountA1, "2026-09-10T23:59:59Z"));
        Assert.Equal(HttpStatusCode.OK, position.StatusCode);
        Assert.Equal(10.50m, (await ReadAsync<AccountPositionDto>(position)).Balance);
    }
}
