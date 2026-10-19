using System.Globalization;
using System.Net;

namespace FinancialTransactions.IntegrationTests;

/// <summary>Repetição simultânea e operações distintas simultâneas, com largada coordenada (sem atraso temporizado).</summary>
public sealed class ConcurrentIdempotencyTests(PostgresFixture fixture) : IntegrationTest(fixture)
{
    private const string OriginAt = "2026-09-10T09:00:00Z";
    private const string LaterAt = "2026-09-10T10:00:00Z";
    private const string Cut = "2026-09-10T23:59:59Z";

    private async Task<decimal> BalanceAsync(Guid accountId)
    {
        using var client = ClientFor(CustomerA);
        using var response = await client.GetAsync(PositionUri(accountId, Cut));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await ReadAsync<AccountPositionDto>(response)).Balance;
    }

    [Fact(DisplayName = "T06_SameKeySameBody_Concurrent_OneCreatedRestReplayed")]
    public async Task T06SameKeySameBodyConcurrentOneCreatedRestReplayed()
    {
        var key = NewKey();
        var body = Credit("10.00", LaterAt, "mesma intenção");

        var responses = await FireConcurrentlyAsync(20, CustomerA, (client, _) => PostMovementAsync(client, AccountA1, key, body));
        var snapshots = await SnapshotAsync(responses);

        Assert.Equal(1, snapshots.Count(s => s.Status == HttpStatusCode.Created));
        Assert.Equal(19, snapshots.Count(s => s.Status == HttpStatusCode.OK));
        Assert.Equal(0, snapshots.Count(s => s.Status == HttpStatusCode.Conflict));
        Assert.Equal(20, snapshots.Count(s => s.Status is HttpStatusCode.Created or HttpStatusCode.OK));
        Assert.Single(snapshots.Select(s => s.Movement.MovementId).Distinct());
        Assert.Equal(1, await CountMovementsAsync(AccountA1));
        Assert.Equal(10.00m, await BalanceAsync(AccountA1));
    }

    [Fact(DisplayName = "T06b_SameKeyDifferentBodies_Concurrent_OneCreatedRestConflict")]
    public async Task T06bSameKeyDifferentBodiesConcurrentOneCreatedRestConflict()
    {
        var key = NewKey();

        // Valores de 1,00 a 20,00: cada corpo é uma intenção diferente sob a mesma identidade.
        var responses = await FireConcurrentlyAsync(20, CustomerA, (client, index) =>
            PostMovementAsync(client, AccountA1, key, Credit((index + 1).ToString("D", CultureInfo.InvariantCulture) + ".00", LaterAt)));
        var snapshots = await SnapshotAsync(responses);

        var created = Assert.Single(snapshots, s => s.Status == HttpStatusCode.Created);
        var conflicts = snapshots.Where(s => s.Status == HttpStatusCode.UnprocessableEntity).ToList();
        Assert.Equal(19, conflicts.Count);
        Assert.All(conflicts, c => Assert.Equal("idempotency_key_conflict", c.Code));
        Assert.Equal(1, await CountMovementsAsync(AccountA1));

        // O valor persistido é o do 201.
        Assert.Equal(created.Movement.Amount, await BalanceAsync(AccountA1));
    }

    [Fact(DisplayName = "T07b_DistinctKeysSameContent_AreTwoLegitimateMovements")]
    public async Task T07bDistinctKeysSameContentAreTwoLegitimateMovements()
    {
        using var client = ClientFor(CustomerA);
        var body = Credit("10.00", LaterAt);

        using var first = await PostMovementAsync(client, AccountA1, NewKey(), body);
        using var second = await PostMovementAsync(client, AccountA1, NewKey(), body);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
        var a = await ReadAsync<MovementDto>(first);
        var b = await ReadAsync<MovementDto>(second);
        Assert.NotEqual(a.MovementId, b.MovementId);
        Assert.Equal(2, await CountMovementsAsync(AccountA1));
        Assert.Equal(20.00m, await BalanceAsync(AccountA1));
    }

    [Fact(DisplayName = "T08_DistinctOperations_Concurrent_BothPreserved")]
    public async Task T08DistinctOperationsConcurrentBothPreserved()
    {
        using var setup = ClientFor(CustomerA);
        using var origin = await PostMovementAsync(setup, AccountA1, NewKey(), Credit("100.00", OriginAt));
        Assert.Equal(HttpStatusCode.Created, origin.StatusCode);

        var amounts = new[] { "10.00", "20.00" };
        var responses = await FireConcurrentlyAsync(2, CustomerA, (client, index) =>
            PostMovementAsync(client, AccountA1, NewKey(), Credit(amounts[index], LaterAt)));
        var snapshots = await SnapshotAsync(responses);

        Assert.All(snapshots, s => Assert.Equal(HttpStatusCode.Created, s.Status));
        Assert.Equal(2, snapshots.Select(s => s.Movement.MovementId).Distinct().Count());
        Assert.Equal(3, await CountMovementsAsync(AccountA1));
        Assert.Equal(130.00m, await BalanceAsync(AccountA1));
    }

    [Fact(DisplayName = "T08b_TwentyDistinctCredits_Concurrent_AllPreserved")]
    public async Task T08bTwentyDistinctCreditsConcurrentAllPreserved()
    {
        using var setup = ClientFor(CustomerA);
        using var origin = await PostMovementAsync(setup, AccountA1, NewKey(), Credit("100.00", OriginAt));
        Assert.Equal(HttpStatusCode.Created, origin.StatusCode);

        var responses = await FireConcurrentlyAsync(20, CustomerA, (client, _) =>
            PostMovementAsync(client, AccountA1, NewKey(), Credit("1.00", LaterAt)));
        var snapshots = await SnapshotAsync(responses);

        Assert.All(snapshots, s => Assert.Equal(HttpStatusCode.Created, s.Status));
        Assert.Equal(20, snapshots.Select(s => s.Movement.MovementId).Distinct().Count());
        Assert.Equal(21, await CountMovementsAsync(AccountA1));
        Assert.Equal(120.00m, await BalanceAsync(AccountA1));
    }
}
