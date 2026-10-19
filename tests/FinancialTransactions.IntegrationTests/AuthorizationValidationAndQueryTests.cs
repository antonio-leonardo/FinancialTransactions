using System.Net;
using FinancialTransactions.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FinancialTransactions.IntegrationTests;

public sealed class AuthorizationValidationAndQueryTests(PostgresFixture fixture) : IntegrationTest(fixture)
{
    [Fact(DisplayName = "T11_NoToken_Is401")]
    public async Task T11NoTokenIs401()
    {
        using var anonymous = Fixture.Factory.CreateClient();

        using var post = await PostMovementAsync(anonymous, AccountA1, NewKey(), Credit("1.00", NowUtc()));
        using var getMovement = await anonymous.GetAsync(Relative($"/api/v1/accounts/{AccountA1}/movements/{Guid.NewGuid()}"));
        using var getPosition = await anonymous.GetAsync(PositionUri(AccountA1, NowUtc()));
        using var getCustomer = await anonymous.GetAsync(CustomerPositionUri(CustomerA, NowUtc()));

        Assert.Equal(HttpStatusCode.Unauthorized, post.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, getMovement.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, getPosition.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, getCustomer.StatusCode);
        Assert.Equal(0, await CountMovementsAsync(AccountA1));
    }

    [Fact(DisplayName = "T11b_OtherCustomer_Is404_EvenOnReplay")]
    public async Task T11bOtherCustomerIs404EvenOnReplay()
    {
        using var clientA = ClientFor(CustomerA);
        using var clientB = ClientFor(CustomerB);
        var body = Credit("10.00", "2026-09-10T12:00:00Z");

        using var intruder = await PostMovementAsync(clientB, AccountA1, NewKey(), body);
        Assert.Equal(HttpStatusCode.NotFound, intruder.StatusCode);
        Assert.Equal("not_found", await ReadCodeAsync(intruder));
        Assert.Equal(0, await CountMovementsAsync(AccountA1));

        var key = NewKey();
        using var legit = await PostMovementAsync(clientA, AccountA1, key, body);
        Assert.Equal(HttpStatusCode.Created, legit.StatusCode);
        var created = await ReadAsync<MovementDto>(legit);

        // Repetição da mesma chave e corpo por outro cliente: escopo verificado antes da idempotência.
        using var replayByB = await PostMovementAsync(clientB, AccountA1, key, body);
        Assert.Equal(HttpStatusCode.NotFound, replayByB.StatusCode);
        using var readByB = await clientB.GetAsync(Relative($"/api/v1/accounts/{AccountA1}/movements/{created.MovementId}"));
        Assert.Equal(HttpStatusCode.NotFound, readByB.StatusCode);
        using var positionByB = await clientB.GetAsync(PositionUri(AccountA1, NowUtc()));
        Assert.Equal(HttpStatusCode.NotFound, positionByB.StatusCode);

        Assert.Equal(1, await CountMovementsAsync(AccountA1));
    }

    [Fact(DisplayName = "Validation_RejectsScaleFutureAndBeforeOpening")]
    public async Task ValidationRejectsScaleFutureAndBeforeOpening()
    {
        using var client = ClientFor(CustomerA);

        using var scale = await PostMovementAsync(client, AccountA1, NewKey(), Credit("10.005", NowUtc()));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, scale.StatusCode);
        Assert.Equal("validation_error", await ReadCodeAsync(scale));

        using var future = await PostMovementAsync(client, AccountA1, NewKey(), Credit("10.00", NowUtc(10)));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, future.StatusCode);
        Assert.Equal("effective_at_in_future", await ReadCodeAsync(future));

        using var beforeOpening = await PostMovementAsync(client, AccountA1, NewKey(), Credit("10.00", "2025-12-31T00:00:00Z"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, beforeOpening.StatusCode);
        Assert.Equal("effective_at_before_opening", await ReadCodeAsync(beforeOpening));

        using var noKey = await PostMovementAsync(client, AccountA1, null, Credit("10.00", NowUtc()));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, noKey.StatusCode);
        Assert.Equal("idempotency_key_required", await ReadCodeAsync(noKey));

        using var badType = await PostMovementAsync(client, AccountA1, NewKey(), new { type = "Transfer", amount = 1.00m, effectiveAt = NowUtc() });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, badType.StatusCode);
        Assert.Equal("validation_error", await ReadCodeAsync(badType));

        using var asOfFuture = await client.GetAsync(PositionUri(AccountA1, NowUtc(10)));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, asOfFuture.StatusCode);
        Assert.Equal("validation_error", await ReadCodeAsync(asOfFuture));

        using var asOfMissing = await client.GetAsync(Relative($"/api/v1/accounts/{AccountA1}/position"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, asOfMissing.StatusCode);
        Assert.Equal("validation_error", await ReadCodeAsync(asOfMissing));

        Assert.Equal(0, await CountMovementsAsync(AccountA1));
    }

    [Fact(DisplayName = "PositionQuery_AggregatesInDatabase")]
    public async Task PositionQueryAggregatesInDatabase()
    {
        await using var scope = Fixture.Factory.Services.CreateAsyncScope();
        var queries = scope.ServiceProvider.GetRequiredService<PositionQueries>();

        var sql = queries.AccountAggregateQuery(AccountA1, DateTimeOffset.UtcNow).ToQueryString();

        Assert.Contains("SUM(", sql, StringComparison.Ordinal);
        Assert.Contains("effective_at <=", sql, StringComparison.Ordinal);
        Assert.Contains("COUNT(*)", sql, StringComparison.Ordinal);

        // O sinal é fragmento de texto no SQL; conta e corte são parâmetros, nunca texto.
        Assert.Contains("CASE m.type WHEN 1 THEN m.amount ELSE -m.amount END", sql, StringComparison.Ordinal);
        Assert.Contains("m.account_id = @p0", sql, StringComparison.Ordinal);
        Assert.Contains("m.effective_at <= @p1", sql, StringComparison.Ordinal);    }
}
