using System.Net;

namespace FinancialTransactions.IntegrationTests;

public sealed class LateInformationTests(PostgresFixture fixture) : IntegrationTest(fixture)
{
    private static async Task<decimal> BalanceAtAsync(HttpClient client, string asOf)
    {
        using var response = await client.GetAsync(PositionUri(AccountA1, asOf));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await ReadAsync<AccountPositionDto>(response)).Balance;
    }

    [Fact(DisplayName = "T04_LateInformation_ChangesHistoricalPositions")]
    public async Task T04LateInformationChangesHistoricalPositions()
    {
        using var client = ClientFor(CustomerA);

        using var origin = await PostMovementAsync(client, AccountA1, NewKey(), Credit("100.00", "2026-09-01T09:00:00Z"));
        using var plus = await PostMovementAsync(client, AccountA1, NewKey(), Credit("30.00", "2026-09-01T10:00:00Z"));
        using var minus = await PostMovementAsync(client, AccountA1, NewKey(), Debit("20.00", "2026-09-01T11:00:00Z"));
        Assert.Equal(HttpStatusCode.Created, origin.StatusCode);
        Assert.Equal(HttpStatusCode.Created, plus.StatusCode);
        Assert.Equal(HttpStatusCode.Created, minus.StatusCode);

        Assert.Equal(130.00m, await BalanceAtAsync(client, "2026-09-01T10:30:00Z"));
        Assert.Equal(110.00m, await BalanceAtAsync(client, "2026-09-01T11:30:00Z"));

        // Informação tardia: efeito às 09:30, registrada depois dos demais (H9).
        using var late = await PostMovementAsync(client, AccountA1, NewKey(), Credit("5.00", "2026-09-01T09:30:00Z"));
        Assert.Equal(HttpStatusCode.Created, late.StatusCode);

        Assert.Equal(135.00m, await BalanceAtAsync(client, "2026-09-01T10:30:00Z"));
        Assert.Equal(115.00m, await BalanceAtAsync(client, "2026-09-01T11:30:00Z"));
        Assert.Equal(4, await CountMovementsAsync(AccountA1));

        var lateDto = await ReadAsync<MovementDto>(late);
        foreach (var earlier in new[] { origin, plus, minus })
        {
            var dto = await ReadAsync<MovementDto>(earlier);
            Assert.True(lateDto.RegisteredAt > dto.RegisteredAt, "registeredAt do tardio deve ser posterior ao dos demais.");
        }

        Assert.Equal(new DateTimeOffset(2026, 9, 1, 9, 30, 0, TimeSpan.Zero), lateDto.EffectiveAt);
    }
}
