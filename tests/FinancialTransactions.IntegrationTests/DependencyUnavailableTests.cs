using System.Diagnostics;
using System.Net;
using System.Text.Json;

namespace FinancialTransactions.IntegrationTests;

/// <summary>
/// Indisponibilidade do banco (porta fechada, prazo curto) e sondas de saúde. Nenhuma resposta pode trazer posição,
/// detalhes de exceção, host ou connection string.
/// </summary>
public sealed class DependencyUnavailableTests(PostgresFixture fixture) : IntegrationTest(fixture)
{
    private const string UnreachableConnection = "Host=127.0.0.1;Port=1;Database=x;Username=ft;Password=x;Timeout=2;Command Timeout=2";
    private const string At = "2026-09-10T12:00:00Z";

    private ApiFactory UnreachableFactory() => new(UnreachableConnection, Fixture.SigningKey);

    private static void AssertNoInfrastructureDetails(string body)
    {
        foreach (var forbidden in new[] { "127.0.0.1", "Port=", "Password", "Npgsql", "Exception", "StackTrace", "   at ", "Host=" })
        {
            Assert.DoesNotContain(forbidden, body, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact(DisplayName = "T13a_PostWhenDatabaseUnreachable_Is503_WithinDeadline")]
    public async Task T13aPostWhenDatabaseUnreachableIs503WithinDeadline()
    {
        using var factory = UnreachableFactory();
        using var client = factory.CreateClient(CustomerA);

        var stopwatch = Stopwatch.StartNew();
        using var response = await PostMovementAsync(client, AccountA1, NewKey(), Credit("10.00", At));
        stopwatch.Stop();

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10), $"503 levou {stopwatch.Elapsed.TotalSeconds:F1} s.");
        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal("dependency_unavailable", await ReadCodeAsync(response));
        using var document = JsonDocument.Parse(body);
        Assert.True(document.RootElement.TryGetProperty("traceId", out var traceId) && !string.IsNullOrEmpty(traceId.GetString()));
        AssertNoInfrastructureDetails(body);
        Assert.Equal(0, await CountMovementsAsync(AccountA1));
    }

    [Fact(DisplayName = "T13b_GetPositionWhenDatabaseUnreachable_Is503_NotZero")]
    public async Task T13bGetPositionWhenDatabaseUnreachableIs503NotZero()
    {
        using var factory = UnreachableFactory();
        using var client = factory.CreateClient(CustomerA);

        using var response = await client.GetAsync(PositionUri(AccountA1, NowUtc()));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("balance", body, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("dependency_unavailable", await ReadCodeAsync(response));
        AssertNoInfrastructureDetails(body);
    }

    [Fact(DisplayName = "T13c_ReadyIs503_LiveIs200_WhenDatabaseUnreachable")]
    public async Task T13cReadyIs503LiveIs200WhenDatabaseUnreachable()
    {
        using var factory = UnreachableFactory();
        using var client = factory.CreateClient();

        var stopwatch = Stopwatch.StartNew();
        using var ready = await client.GetAsync(Relative("/health/ready"));
        stopwatch.Stop();
        using var live = await client.GetAsync(Relative("/health/live"));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, ready.StatusCode);
        Assert.Equal("""{"status":"Unhealthy"}""", await ready.Content.ReadAsStringAsync());
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5), $"readiness levou {stopwatch.Elapsed.TotalSeconds:F1} s.");
        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
        Assert.Equal("""{"status":"Healthy"}""", await live.Content.ReadAsStringAsync());
    }

    [Fact(DisplayName = "T13d_ReadyIs200_WhenDatabaseAvailable")]
    public async Task T13dReadyIs200WhenDatabaseAvailable()
    {
        using var client = Fixture.Factory.CreateClient();

        using var ready = await client.GetAsync(Relative("/health/ready"));

        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
        Assert.Equal("""{"status":"Healthy"}""", await ready.Content.ReadAsStringAsync());
    }

    [Fact(DisplayName = "Health_IsAnonymous_ApiIsNot")]
    public async Task HealthIsAnonymousApiIsNot()
    {
        using var client = Fixture.Factory.CreateClient();

        using var live = await client.GetAsync(Relative("/health/live"));
        using var ready = await client.GetAsync(Relative("/health/ready"));
        using var position = await client.GetAsync(PositionUri(AccountA1, NowUtc()));

        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, position.StatusCode);
    }

    [Fact(DisplayName = "RequestBody_Over16KB_Is413_NothingPersisted")]
    public async Task RequestBodyOver16KbIs413NothingPersisted()
    {
        using var client = ClientFor(CustomerA);
        // Campo desconhecido de 64 KB: JSON válido que só o limite de corpo recusa.
        var json = $$"""{"type":"Credit","amount":10.00,"effectiveAt":"{{At}}","extra":"{{new string('x', 64 * 1024)}}"}""";

        using var response = await PostRawAsync(client, AccountA1, NewKey(), json);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Equal("payload_too_large", await ReadCodeAsync(response));
        Assert.Equal(0, await CountMovementsAsync(AccountA1));
    }
}
