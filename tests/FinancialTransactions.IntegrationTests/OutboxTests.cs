using System.Globalization;
using System.Net;
using Npgsql;

namespace FinancialTransactions.IntegrationTests;

/// <summary>
/// Outbox transacional (P07, I3): o evento nasce na transação do movimento. Os esperados vêm de SQL direto e dos valores
/// enviados no POST, não de código de produção.
/// </summary>
public sealed class OutboxTests(PostgresFixture fixture) : IntegrationTest(fixture)
{
    private const string At = "2026-09-10T12:00:00Z";

    private sealed record OutboxRow(long Sequence, Guid EventId, Guid CustomerId, short Type, decimal Amount, short Origin, Guid? BootstrapId, short Status, int Attempts, string? LastError);

    private async Task<IReadOnlyList<OutboxRow>> ReadOutboxAsync(Guid accountId)
    {
        await using var connection = Fixture.OpenTestConnection();
        await using var command = new NpgsqlCommand(
            "SELECT sequence, event_id, customer_id, type, amount, origin, bootstrap_id, status, attempts, last_error " +
            "FROM consolidation_outbox WHERE account_id = @id ORDER BY sequence", connection);
        command.Parameters.AddWithValue("id", accountId);
        await using var reader = await command.ExecuteReaderAsync();
        var rows = new List<OutboxRow>();
        while (await reader.ReadAsync())
        {
            rows.Add(new OutboxRow(
                reader.GetInt64(0),
                reader.GetGuid(1),
                reader.GetGuid(2),
                reader.GetInt16(3),
                reader.GetDecimal(4),
                reader.GetInt16(5),
                reader.IsDBNull(6) ? null : reader.GetGuid(6),
                reader.GetInt16(7),
                reader.GetInt32(8),
                reader.IsDBNull(9) ? null : reader.GetString(9)));
        }

        return rows;
    }

    /// <summary>Movimentos da conta sem evento e eventos da conta sem movimento, ambos por identidade (event_id = id do movimento).</summary>
    private async Task<(long MovementsWithoutEvent, long EventsWithoutMovement)> CountOrphansAsync(Guid accountId)
    {
        await using var connection = Fixture.OpenTestConnection();
        await using var command = new NpgsqlCommand(
            "SELECT " +
            "(SELECT COUNT(*) FROM movements m WHERE m.account_id = @id AND NOT EXISTS (SELECT 1 FROM consolidation_outbox o WHERE o.event_id = m.id)), " +
            "(SELECT COUNT(*) FROM consolidation_outbox o WHERE o.account_id = @id AND NOT EXISTS (SELECT 1 FROM movements m WHERE m.id = o.event_id))",
            connection);
        command.Parameters.AddWithValue("id", accountId);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return (reader.GetInt64(0), reader.GetInt64(1));
    }

    [Fact(DisplayName = "Outbox_Post_CreatesOnePendingOnlineEventWithMovementIdentity")]
    public async Task OutboxPostCreatesOnePendingOnlineEventWithMovementIdentity()
    {
        using var client = ClientFor(CustomerA);

        using var response = await PostMovementAsync(client, AccountA2, NewKey(), Debit("12.34", At));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var movement = await ReadAsync<MovementDto>(response);
        var rows = await ReadOutboxAsync(AccountA2);
        var row = Assert.Single(rows);
        Assert.Equal(movement.MovementId, row.EventId);
        Assert.Equal(CustomerA, row.CustomerId);
        Assert.Equal(2, row.Type); // Debit = 2 (valor literal da coluna smallint)
        Assert.Equal(12.34m, row.Amount);
        Assert.Equal(0, row.Origin);   // online
        Assert.Null(row.BootstrapId);  // fora do conjunto inicial
        Assert.Equal(0, row.Status);   // pendente
        Assert.Equal(0, row.Attempts);
        Assert.Null(row.LastError);
        Assert.True(row.Sequence > 0);
    }

    [Fact(DisplayName = "C10_ReplayHttp_ReturnsOkAndCreatesNoNewOutbox")]
    public async Task C10ReplayHttpReturnsOkAndCreatesNoNewOutbox()
    {
        using var client = ClientFor(CustomerA);
        var key = NewKey();
        var body = Credit("10.00", At);

        using var first = await PostMovementAsync(client, AccountA1, key, body);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var before = await ReadOutboxAsync(AccountA1);

        using var replay = await PostMovementAsync(client, AccountA1, key, body);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);

        // Mesma chave com conteúdo diferente: 422 sem efeito, e também sem evento.
        using var conflict = await PostMovementAsync(client, AccountA1, key, Credit("11.00", At));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, conflict.StatusCode);

        var after = await ReadOutboxAsync(AccountA1);
        Assert.Single(after);
        Assert.Equal(before[0].Sequence, after[0].Sequence);
        Assert.Equal(1, await CountMovementsAsync(AccountA1));
    }

    [Fact(DisplayName = "Outbox_FailureBeforeCommit_LeavesNoOutbox_RetryCreatesExactlyOne")]
    public async Task OutboxFailureBeforeCommitLeavesNoOutboxRetryCreatesExactlyOne()
    {
        var points = new ScriptedFailurePoints(FailurePoint.BeforeCommit);
        using var factory = Fixture.Factory.WithFailurePoints(points);
        using var client = Fixture.Factory.AuthenticatedClient(factory, CustomerA);
        var key = NewKey();
        var body = Credit("10.00", At);

        using var failed = await PostMovementAsync(client, AccountA1, key, body);

        Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode);
        Assert.Equal(1, points.FiredCount);
        Assert.Equal(0, await CountMovementsAsync(AccountA1));
        Assert.Equal(0, await CountOutboxAsync(AccountA1));

        using var retry = await PostMovementAsync(client, AccountA1, key, body);
        Assert.Equal(HttpStatusCode.Created, retry.StatusCode);
        Assert.Equal(1, await CountMovementsAsync(AccountA1));
        Assert.Equal(1, await CountOutboxAsync(AccountA1));
    }

    [Fact(DisplayName = "C15a_FailureAfterCommit_MovementAndEventExistTogether")]
    public async Task C15aFailureAfterCommitMovementAndEventExistTogether()
    {
        var points = new ScriptedFailurePoints(FailurePoint.AfterCommit);
        using var factory = Fixture.Factory.WithFailurePoints(points);
        using var client = Fixture.Factory.AuthenticatedClient(factory, CustomerA);
        var key = NewKey();

        using var failed = await PostMovementAsync(client, AccountA1, key, Credit("10.00", At));

        // O cliente recebeu 500, mas o commit já tinha acontecido: o fato e o evento existem, 1 para 1, pela mesma identidade.
        Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode);
        Assert.Equal(1, points.FiredCount);
        Assert.Equal(1, await CountMovementsAsync(AccountA1));
        Assert.Equal(1, await CountOutboxAsync(AccountA1));
        Assert.Equal((0L, 0L), await CountOrphansAsync(AccountA1));

        // A repetição devolve o mesmo movimento e não cria segundo evento.
        using var replay = await PostMovementAsync(client, AccountA1, key, Credit("10.00", At));
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal(1, await CountOutboxAsync(AccountA1));
        Assert.Equal((0L, 0L), await CountOrphansAsync(AccountA1));
    }

    [Fact(DisplayName = "C15b_FailureBeforeCommit_NeitherMovementNorEventExist")]
    public async Task C15bFailureBeforeCommitNeitherMovementNorEventExist()
    {
        var points = new ScriptedFailurePoints(FailurePoint.BeforeCommit);
        using var factory = Fixture.Factory.WithFailurePoints(points);
        using var client = Fixture.Factory.AuthenticatedClient(factory, CustomerA);

        using var failed = await PostMovementAsync(client, AccountA1, NewKey(), Credit("10.00", At));

        Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode);
        Assert.Equal(0, await CountMovementsAsync(AccountA1));
        Assert.Equal(0, await CountOutboxAsync(AccountA1));
        Assert.Equal((0L, 0L), await CountOrphansAsync(AccountA1));
    }

    [Fact(DisplayName = "C15c_ManyAccepted_EveryMovementHasExactlyOneEvent")]
    public async Task C15cManyAcceptedEveryMovementHasExactlyOneEvent()
    {
        var responses = await FireConcurrentlyAsync(10, CustomerA, (client, index) =>
            PostMovementAsync(client, AccountA2, NewKey(), Credit((index + 1).ToString("0.00", CultureInfo.InvariantCulture), At)));
        var snapshots = await SnapshotAsync(responses);

        Assert.All(snapshots, s => Assert.Equal(HttpStatusCode.Created, s.Status));
        Assert.Equal(10, await CountMovementsAsync(AccountA2));
        Assert.Equal(10, await CountOutboxAsync(AccountA2));
        Assert.Equal((0L, 0L), await CountOrphansAsync(AccountA2));
        var ids = snapshots.Select(s => s.Movement.MovementId).Order().ToArray();
        var events = (await ReadOutboxAsync(AccountA2)).Select(r => r.EventId).Order().ToArray();
        Assert.Equal(ids, events);
    }
}
