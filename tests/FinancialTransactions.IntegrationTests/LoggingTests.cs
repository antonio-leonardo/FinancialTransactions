using System.Net;
using System.Net.Http.Headers;
using Npgsql;

namespace FinancialTransactions.IntegrationTests;

/// <summary>Os logs carregam estado real (movementId) e nunca segredos, chaves de idempotência ou corpos.</summary>
public sealed class LoggingTests(PostgresFixture fixture) : IntegrationTest(fixture)
{
    private const string At = "2026-09-10T12:00:00Z";
    private const string SecretDescription = "corpo-sigiloso-p03";

    [Fact(DisplayName = "Logs_DoNotContainSecretsOrBodies")]
    public async Task LogsDoNotContainSecretsOrBodies()
    {
        var capture = new CapturingLoggerProvider();
        using var factory = Fixture.Factory.WithLoggerProvider(capture);
        var token = Fixture.Factory.CreateToken(CustomerA, TimeSpan.FromMinutes(10));
        var badToken = Fixture.Factory.CreateToken(CustomerA, TimeSpan.FromMinutes(10), new TokenVariation(SigningKey: "outra-chave-de-assinatura-com-mais-de-32-bytes"));
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var key = NewKey();
        var invalidKey = NewKey();
        var validBody = $$"""{"type":"Credit","amount":10.00,"effectiveAt":"{{At}}","description":"{{SecretDescription}}"}""";
        var invalidBody = $$"""{"type":"Transfer","amount":10.00,"effectiveAt":"{{At}}","description":"{{SecretDescription}}"}""";

        using var created = await PostRawAsync(client, AccountA1, key, validBody);
        using var replay = await PostRawAsync(client, AccountA1, key, validBody);
        using var invalid = await PostRawAsync(client, AccountA1, invalidKey, invalidBody);
        using var anonymous = factory.CreateClient();
        anonymous.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", badToken);
        using var unauthorized = await anonymous.GetAsync(PositionUri(AccountA1, NowUtc()));

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, invalid.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);

        var movementId = (await ReadAsync<MovementDto>(created)).MovementId.ToString();
        var password = new NpgsqlConnectionStringBuilder(Fixture.ConnectionString).Password;
        var messages = capture.Messages;
        Assert.NotEmpty(messages);

        var forbidden = new[] { token, badToken, key, invalidKey, "ft_local_dev_only", validBody, invalidBody, SecretDescription, Fixture.SigningKey };
        foreach (var message in messages)
        {
            foreach (var secret in forbidden)
            {
                Assert.DoesNotContain(secret, message, StringComparison.Ordinal);
            }

            if (!string.IsNullOrEmpty(password))
            {
                Assert.DoesNotContain(password, message, StringComparison.Ordinal);
            }
        }

        Assert.Contains(messages, message => message.Contains(movementId, StringComparison.Ordinal));
    }
}
