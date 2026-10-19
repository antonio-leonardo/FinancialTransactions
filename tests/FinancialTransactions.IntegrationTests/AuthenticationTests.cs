using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace FinancialTransactions.IntegrationTests;

public sealed class AuthenticationTests(PostgresFixture fixture) : IntegrationTest(fixture)
{
    [Fact(DisplayName = "T11c_InvalidTokens_Are401_NothingPersisted")]
    public async Task T11cInvalidTokensAre401NothingPersisted()
    {
        var factory = Fixture.Factory;
        var otherKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48));
        var tokens = new Dictionary<string, string>
        {
            ["expirado"] = factory.CreateToken(CustomerA, TimeSpan.FromMinutes(-30)),
            ["audiência errada"] = factory.CreateToken(CustomerA, TimeSpan.FromMinutes(10), new TokenVariation(Audience: "outra-api")),
            ["sem customer_id"] = factory.CreateToken(CustomerA, TimeSpan.FromMinutes(10), new TokenVariation(IncludeCustomerId: false)),
            ["outra chave"] = factory.CreateToken(CustomerA, TimeSpan.FromMinutes(10), new TokenVariation(SigningKey: otherKey)),
        };

        foreach (var (reason, token) in tokens)
        {
            using var client = factory.CreateClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

            using var post = await PostMovementAsync(client, AccountA1, NewKey(), Credit("10.00", "2026-09-10T12:00:00Z"));
            using var get = await client.GetAsync(PositionUri(AccountA1, "2026-09-10T23:59:59Z"));

            Assert.True(post.StatusCode == HttpStatusCode.Unauthorized, $"POST com token {reason}: {post.StatusCode}");
            Assert.True(get.StatusCode == HttpStatusCode.Unauthorized, $"GET com token {reason}: {get.StatusCode}");
            Assert.Equal(0, await CountMovementsAsync(AccountA1));
        }
    }

    [Fact(DisplayName = "T11c_ControlToken_IsAccepted")]
    public async Task T11cControlTokenIsAccepted()
    {
        // Controle: o mesmo caminho com token válido funciona, para que os 401 acima não sejam artefato do teste.
        using var client = Fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            Fixture.Factory.CreateToken(CustomerA, TimeSpan.FromMinutes(10), new TokenVariation()));

        using var post = await PostMovementAsync(client, AccountA1, NewKey(), Credit("10.00", "2026-09-10T12:00:00Z"));

        Assert.Equal(HttpStatusCode.Created, post.StatusCode);
        Assert.Equal(1, await CountMovementsAsync(AccountA1));
    }
}
