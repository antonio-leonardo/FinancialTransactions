using System.Net;

namespace FinancialTransactions.IntegrationTests;

/// <summary>Falhas junto ao commit, provocadas por ponto de falha que só existe no host de teste.</summary>
public sealed class CommitFailureTests(PostgresFixture fixture) : IntegrationTest(fixture)
{
    private const string At = "2026-09-10T12:00:00Z";

    [Fact(DisplayName = "T10a_FailureBeforeCommit_NothingPersisted_RetryCreates")]
    public async Task T10aFailureBeforeCommitNothingPersistedRetryCreates()
    {
        var points = new ScriptedFailurePoints(FailurePoint.BeforeCommit);
        using var factory = Fixture.Factory.WithFailurePoints(points);
        using var client = Fixture.Factory.AuthenticatedClient(factory, CustomerA);
        var key = NewKey();
        var body = Credit("10.00", At);

        using var failed = await PostMovementAsync(client, AccountA1, key, body);
        Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode);
        Assert.Equal("internal_error", await ReadCodeAsync(failed));
        Assert.Equal(1, points.FiredCount);
        Assert.Equal(0, await CountMovementsAsync(AccountA1));

        // A falha não consumiu a identidade: a repetição da mesma chave cria, não conflita.
        using var retry = await PostMovementAsync(client, AccountA1, key, body);
        Assert.Equal(HttpStatusCode.Created, retry.StatusCode);
        Assert.Equal(1, await CountMovementsAsync(AccountA1));
    }

    [Fact(DisplayName = "T10b_FailureAfterCommit_Persisted_RetryReplays")]
    public async Task T10bFailureAfterCommitPersistedRetryReplays()
    {
        var points = new ScriptedFailurePoints(FailurePoint.AfterCommit);
        using var factory = Fixture.Factory.WithFailurePoints(points);
        using var client = Fixture.Factory.AuthenticatedClient(factory, CustomerA);
        var key = NewKey();
        var body = Credit("10.00", At);

        using var failed = await PostMovementAsync(client, AccountA1, key, body);
        Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode);
        Assert.Equal("internal_error", await ReadCodeAsync(failed));
        Assert.Equal(1, points.FiredCount);
        Assert.Equal(1, await CountMovementsAsync(AccountA1));

        // O cliente não recebeu o 201, mas o fato existe: a repetição devolve o mesmo movimento.
        using var retry = await PostMovementAsync(client, AccountA1, key, body);
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        var replayed = await ReadAsync<MovementDto>(retry);
        Assert.Equal(1, await CountMovementsAsync(AccountA1));

        using var read = await client.GetAsync(Relative($"/api/v1/accounts/{AccountA1}/movements/{replayed.MovementId}"));
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
    }
}
