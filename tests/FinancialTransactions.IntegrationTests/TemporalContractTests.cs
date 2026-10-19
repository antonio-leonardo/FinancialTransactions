using System.Globalization;
using System.Net;

namespace FinancialTransactions.IntegrationTests;

/// <summary>Contrato temporal de entrada (gramática fechada de effectiveAt e asOf). Corpos enviados como JSON cru.</summary>
public sealed class TemporalContractTests(PostgresFixture fixture) : IntegrationTest(fixture)
{
    [Fact(DisplayName = "F1a_PostEffectiveAtWithoutOffset_Is422_NothingPersisted")]
    public async Task F1aPostEffectiveAtWithoutOffsetIs422NothingPersisted()
    {
        using var client = ClientFor(CustomerA);

        using var response = await PostRawAsync(client, AccountA1, NewKey(), """{ "type": "Credit", "amount": 10.00, "effectiveAt": "2026-09-01T09:00:00" }""");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("validation_error", await ReadCodeAsync(response));
        Assert.Equal(0, await CountMovementsAsync(AccountA1));
    }

    [Fact(DisplayName = "F1b_PostEffectiveAtNumber_Is422")]
    public async Task F1bPostEffectiveAtNumberIs422()
    {
        using var client = ClientFor(CustomerA);

        using var response = await PostRawAsync(client, AccountA1, NewKey(), """{ "type": "Credit", "amount": 10.00, "effectiveAt": 20260901 }""");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("validation_error", await ReadCodeAsync(response));
        Assert.Equal(0, await CountMovementsAsync(AccountA1));
    }

    [Fact(DisplayName = "F1c_PostZAndOffsetSameInstant_ReplayAcrossRepresentations")]
    public async Task F1cPostZAndOffsetSameInstantReplayAcrossRepresentations()
    {
        using var client = ClientFor(CustomerA);
        var key = NewKey();

        using var first = await PostRawAsync(client, AccountA1, key, """{ "type": "Credit", "amount": 10.00, "effectiveAt": "2026-09-01T09:00:00-03:00" }""");
        using var second = await PostRawAsync(client, AccountA1, key, """{ "type": "Credit", "amount": 10.00, "effectiveAt": "2026-09-01T12:00:00Z" }""");

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var created = await ReadAsync<MovementDto>(first);
        var replayed = await ReadAsync<MovementDto>(second);
        Assert.Equal(created.MovementId, replayed.MovementId);
        Assert.Equal(new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.Zero), created.EffectiveAt);
        Assert.Equal(1, await CountMovementsAsync(AccountA1));
    }

    [Fact(DisplayName = "F1d_GetAsOfHourOnly_DateOnly_Regional_Text_Are422")]
    public async Task F1dGetAsOfHourOnlyDateOnlyRegionalTextAre422()
    {
        using var client = ClientFor(CustomerA);
        var hourOnly = DateTimeOffset.UtcNow.AddMinutes(-30).ToString("HH:mm", CultureInfo.InvariantCulture);

        foreach (var asOf in new[] { hourOnly, "2026-09-01", "09/01/2026", "Sep 1 2026" })
        {
            using var response = await client.GetAsync(PositionUri(AccountA1, asOf));
            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
            Assert.Equal("validation_error", await ReadCodeAsync(response));
        }

        // Codificação explícita, como um cliente HTTP enviaria.
        using var regional = await client.GetAsync(Relative($"/api/v1/accounts/{AccountA1}/position?asOf=09%2F01%2F2026"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, regional.StatusCode);
        using var text = await client.GetAsync(Relative($"/api/v1/accounts/{AccountA1}/position?asOf=Sep%201%202026"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, text.StatusCode);
    }

    [Fact(DisplayName = "F1e_GetAsOfCompleteWithoutOffset_IsUtc")]
    public async Task F1eGetAsOfCompleteWithoutOffsetIsUtc()
    {
        using var client = ClientFor(CustomerA);

        using var response = await client.GetAsync(Relative($"/api/v1/accounts/{AccountA1}/position?asOf=2026-09-01T12:00:00"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await ReadAsync<AccountPositionDto>(response);
        Assert.Equal(new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.Zero), body.AsOf);
        Assert.Equal(TimeSpan.Zero, body.AsOf.Offset);
        var raw = await response.Content.ReadAsStringAsync();
        Assert.Contains("\"asOf\":\"2026-09-01T12:00:00+00:00\"", raw, StringComparison.Ordinal);
    }
}
