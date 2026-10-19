using System.Globalization;
using System.Net;

namespace FinancialTransactions.IntegrationTests;

/// <summary>Fronteiras de entrada (T03), precisão e seleção por conta e por cliente (T12), e replay após truncamento.</summary>
public sealed class BoundaryAndSelectionTests(PostgresFixture fixture) : IntegrationTest(fixture)
{
    private const string Cut = "2026-09-10T23:59:59Z";

    [Fact(DisplayName = "T03_EffectiveAtEqualToOpenedAt_IsAccepted")]
    public async Task T03EffectiveAtEqualToOpenedAtIsAccepted()
    {
        using var client = ClientFor(CustomerA);

        // A1 abre em 2026-01-01T00:00:00Z (seed).
        using var atOpening = await PostMovementAsync(client, AccountA1, NewKey(), Credit("1.00", "2026-01-01T00:00:00Z"));
        using var beforeOpening = await PostMovementAsync(client, AccountA1, NewKey(), Credit("1.00", "2025-12-31T23:59:59Z"));

        Assert.Equal(HttpStatusCode.Created, atOpening.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, beforeOpening.StatusCode);
        Assert.Equal("effective_at_before_opening", await ReadCodeAsync(beforeOpening));
        Assert.Equal(1, await CountMovementsAsync(AccountA1));
    }

    [Fact(DisplayName = "T03_Description_140Accepted_141Rejected")]
    public async Task T03Description140Accepted141Rejected()
    {
        using var client = ClientFor(CustomerA);

        using var ok = await PostMovementAsync(client, AccountA1, NewKey(), Credit("1.00", "2026-09-10T12:00:00Z", new string('d', 140)));
        using var tooLong = await PostMovementAsync(client, AccountA1, NewKey(), Credit("1.00", "2026-09-10T12:00:00Z", new string('d', 141)));

        Assert.Equal(HttpStatusCode.Created, ok.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, tooLong.StatusCode);
        Assert.Equal("validation_error", await ReadCodeAsync(tooLong));
        Assert.Equal(1, await CountMovementsAsync(AccountA1));
    }

    [Fact(DisplayName = "T03_Amount_CeilingAccepted_CeilingPlusCentRejected")]
    public async Task T03AmountCeilingAcceptedCeilingPlusCentRejected()
    {
        using var client = ClientFor(CustomerA);

        using var ceiling = await PostMovementAsync(client, AccountA1, NewKey(), Credit("1000000000.00", "2026-09-10T12:00:00Z"));
        using var over = await PostMovementAsync(client, AccountA1, NewKey(), Credit("1000000000.01", "2026-09-10T12:00:00Z"));

        Assert.Equal(HttpStatusCode.Created, ceiling.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, over.StatusCode);
        Assert.Equal("validation_error", await ReadCodeAsync(over));
        Assert.Equal(1, await CountMovementsAsync(AccountA1));

        using var position = await client.GetAsync(PositionUri(AccountA1, Cut));
        Assert.Equal(1_000_000_000.00m, (await ReadAsync<AccountPositionDto>(position)).Balance);
    }

    [Fact(DisplayName = "T03_IdempotencyKey_128Accepted_129Rejected")]
    public async Task T03IdempotencyKey128Accepted129Rejected()
    {
        using var client = ClientFor(CustomerA);

        using var ok = await PostMovementAsync(client, AccountA1, new string('k', 128), Credit("1.00", "2026-09-10T12:00:00Z"));
        using var tooLong = await PostMovementAsync(client, AccountA1, new string('k', 129), Credit("1.00", "2026-09-10T12:00:00Z"));

        Assert.Equal(HttpStatusCode.Created, ok.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, tooLong.StatusCode);
        Assert.Equal("idempotency_key_required", await ReadCodeAsync(tooLong));
        Assert.Equal(1, await CountMovementsAsync(AccountA1));
    }

    [Fact(DisplayName = "T03_NonexistentAccount_Is404")]
    public async Task T03NonexistentAccountIs404()
    {
        using var client = ClientFor(CustomerA);
        var missing = Guid.NewGuid();

        using var post = await PostMovementAsync(client, missing, NewKey(), Credit("1.00", "2026-09-10T12:00:00Z"));
        using var position = await client.GetAsync(PositionUri(missing, Cut));

        Assert.Equal(HttpStatusCode.NotFound, post.StatusCode);
        Assert.Equal("not_found", await ReadCodeAsync(post));
        Assert.Equal(HttpStatusCode.NotFound, position.StatusCode);
        Assert.Equal(0, await CountMovementsAsync(missing));
    }

    [Fact(DisplayName = "T03_ExistingAccountWithoutMovements_IsZero")]
    public async Task T03ExistingAccountWithoutMovementsIsZero()
    {
        using var client = ClientFor(CustomerA);

        using var response = await client.GetAsync(PositionUri(AccountA1, Cut));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await ReadAsync<AccountPositionDto>(response);
        Assert.Equal(0.00m, body.Balance);
        Assert.Equal(0, body.MovementCount);
    }

    [Fact(DisplayName = "T12_HundredCents_SumExactlyOneReal")]
    public async Task T12HundredCentsSumExactlyOneReal()
    {
        using var client = ClientFor(CustomerA);

        for (var i = 0; i < 100; i++)
        {
            using var response = await PostMovementAsync(client, AccountA1, NewKey(), Credit("0.01", "2026-09-10T12:00:00Z"));
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        }

        using var position = await client.GetAsync(PositionUri(AccountA1, Cut));
        var body = await ReadAsync<AccountPositionDto>(position);
        Assert.Equal(1.00m, body.Balance);
        Assert.Equal(100, body.MovementCount);
        Assert.Equal(100, await CountMovementsAsync(AccountA1));
    }

    [Fact(DisplayName = "T12_Selection_OtherAccountAndOtherCustomerDoNotLeak")]
    public async Task T12SelectionOtherAccountAndOtherCustomerDoNotLeak()
    {
        using var clientA = ClientFor(CustomerA);
        using var clientB = ClientFor(CustomerB);

        using var a1 = await PostMovementAsync(clientA, AccountA1, NewKey(), Credit("10.00", "2026-09-10T12:00:00Z"));
        using var a2 = await PostMovementAsync(clientA, AccountA2, NewKey(), Credit("200.00", "2026-09-10T12:00:00Z"));
        using var b1 = await PostMovementAsync(clientB, AccountB1, NewKey(), Credit("3000.00", "2026-09-10T12:00:00Z"));
        Assert.Equal(HttpStatusCode.Created, a1.StatusCode);
        Assert.Equal(HttpStatusCode.Created, a2.StatusCode);
        Assert.Equal(HttpStatusCode.Created, b1.StatusCode);

        using var positionA1 = await clientA.GetAsync(PositionUri(AccountA1, Cut));
        var onlyA1 = await ReadAsync<AccountPositionDto>(positionA1);
        Assert.Equal(10.00m, onlyA1.Balance);
        Assert.Equal(1, onlyA1.MovementCount);

        using var customerA = await clientA.GetAsync(CustomerPositionUri(CustomerA, Cut));
        var consolidated = await ReadAsync<CustomerPositionDto>(customerA);
        Assert.Equal(210.00m, consolidated.Balance);
        Assert.Equal(2, consolidated.Accounts.Count);
        Assert.DoesNotContain(consolidated.Accounts, a => a.AccountId == AccountB1);

        using var customerB = await clientB.GetAsync(CustomerPositionUri(CustomerB, Cut));
        Assert.Equal(3000.00m, (await ReadAsync<CustomerPositionDto>(customerB)).Balance);
    }

    [Fact(DisplayName = "Fingerprint_ReplayWithSixDigitsReturnedBy201_Is200")]
    public async Task FingerprintReplayWithSixDigitsReturnedBy201Is200()
    {
        using var client = ClientFor(CustomerA);
        var key = NewKey();

        // Sete dígitos de fração: o 201 devolve seis (microssegundos); um cliente que reconstrói o corpo a partir
        // da resposta precisa obter 200 com o mesmo movimento, não 422.
        using var first = await PostRawAsync(client, AccountA1, key, """{"type":"Credit","amount":10.00,"effectiveAt":"2026-09-10T12:00:00.1234567Z"}""");
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var created = await ReadAsync<MovementDto>(first);
        var sixDigits = created.EffectiveAt.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", CultureInfo.InvariantCulture);
        Assert.Equal("2026-09-10T12:00:00.123456Z", sixDigits);

        using var replay = await PostRawAsync(client, AccountA1, key, $$"""{"type":"Credit","amount":10.00,"effectiveAt":"{{sixDigits}}"}""");

        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal(created.MovementId, (await ReadAsync<MovementDto>(replay)).MovementId);
        Assert.Equal(1, await CountMovementsAsync(AccountA1));
    }
}
