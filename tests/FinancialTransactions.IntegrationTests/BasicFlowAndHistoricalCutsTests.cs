using System.Net;

namespace FinancialTransactions.IntegrationTests;

public sealed class BasicFlowAndHistoricalCutsTests(PostgresFixture fixture) : IntegrationTest(fixture)
{
    [Fact(DisplayName = "T01_CreditAndDebit_PersistOnce_AndPositionMatches")]
    public async Task T01CreditAndDebitPersistOnceAndPositionMatches()
    {
        using var client = ClientFor(CustomerA);
        var effectiveAt = NowUtc();

        using var credit = await PostMovementAsync(client, AccountA1, NewKey(), Credit("100.00", effectiveAt, "depósito"));
        using var debit = await PostMovementAsync(client, AccountA1, NewKey(), Debit("40.00", effectiveAt));

        Assert.Equal(HttpStatusCode.Created, credit.StatusCode);
        Assert.Equal(HttpStatusCode.Created, debit.StatusCode);
        var created = await ReadAsync<MovementDto>(credit);
        Assert.Equal(AccountA1, created.AccountId);
        Assert.Equal("Credit", created.Type);
        Assert.Equal(100.00m, created.Amount);
        Assert.Equal("BRL", created.Currency);
        Assert.NotNull(credit.Headers.Location);
        Assert.EndsWith($"/api/v1/accounts/{AccountA1}/movements/{created.MovementId}", credit.Headers.Location.ToString(), StringComparison.Ordinal);

        using var fetched = await client.GetAsync(credit.Headers.Location);
        Assert.Equal(HttpStatusCode.OK, fetched.StatusCode);
        Assert.Equal(created.MovementId, (await ReadAsync<MovementDto>(fetched)).MovementId);

        Assert.Equal(2, await CountMovementsAsync(AccountA1));

        using var position = await client.GetAsync(PositionUri(AccountA1, NowUtc(1)));
        Assert.Equal(HttpStatusCode.OK, position.StatusCode);
        var body = await ReadAsync<AccountPositionDto>(position);
        Assert.Equal(60.00m, body.Balance);
        Assert.Equal(2, body.MovementCount);
        Assert.Equal("BRL", body.Currency);
    }

    private async Task SeedHistoricalMassAsync(HttpClient client)
    {
        using var first = await PostMovementAsync(client, AccountA1, NewKey(), Credit("100.00", "2026-09-01T09:00:00-03:00"));
        using var second = await PostMovementAsync(client, AccountA1, NewKey(), Credit("30.00", "2026-09-01T10:00:00-03:00"));
        using var third = await PostMovementAsync(client, AccountA1, NewKey(), Debit("20.00", "2026-09-01T11:00:00-03:00"));
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
        Assert.Equal(HttpStatusCode.Created, third.StatusCode);
    }

    private static async Task<decimal> BalanceAtAsync(HttpClient client, Guid accountId, string asOf)
    {
        using var response = await client.GetAsync(PositionUri(accountId, asOf));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await ReadAsync<AccountPositionDto>(response)).Balance;
    }

    [Fact(DisplayName = "T02_TwoHistoricalCuts_ReturnDifferentBalances")]
    public async Task T02TwoHistoricalCutsReturnDifferentBalances()
    {
        using var client = ClientFor(CustomerA);
        await SeedHistoricalMassAsync(client);

        // Falha se a API devolver o saldo atual (110) para 10:30.
        Assert.Equal(130.00m, await BalanceAtAsync(client, AccountA1, "2026-09-01T10:30:00-03:00"));
        Assert.Equal(110.00m, await BalanceAtAsync(client, AccountA1, "2026-09-01T11:30:00-03:00"));
        Assert.Equal(130.00m, await BalanceAtAsync(client, AccountA1, "2026-09-01T10:00:00-03:00"));
        Assert.Equal(100.00m, await BalanceAtAsync(client, AccountA1, "2026-09-01T09:59:59-03:00"));
    }

    [Fact(DisplayName = "T02b_SameInstantDifferentOffset_SameBalance")]
    public async Task T02bSameInstantDifferentOffsetSameBalance()
    {
        using var client = ClientFor(CustomerA);
        await SeedHistoricalMassAsync(client);

        Assert.Equal(130.00m, await BalanceAtAsync(client, AccountA1, "2026-09-01T13:30:00Z"));
    }

    [Fact(DisplayName = "T02c_BeforeOpening_IsInsufficientHistory_NotZero")]
    public async Task T02cBeforeOpeningIsInsufficientHistoryNotZero()
    {
        using var client = ClientFor(CustomerA);
        Assert.Equal(0, await CountMovementsAsync(AccountA1));

        using var before = await client.GetAsync(PositionUri(AccountA1, "2025-12-31T23:59:59Z"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, before.StatusCode);
        Assert.Equal("historico_insuficiente", await ReadCodeAsync(before));

        using var atOpening = await client.GetAsync(PositionUri(AccountA1, "2026-01-01T00:00:00Z"));
        Assert.Equal(HttpStatusCode.OK, atOpening.StatusCode);
        var body = await ReadAsync<AccountPositionDto>(atOpening);
        Assert.Equal(0.00m, body.Balance);
        Assert.Equal(0, body.MovementCount);
    }
}
