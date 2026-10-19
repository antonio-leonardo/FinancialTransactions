using FinancialTransactions.Consolidation;
using Microsoft.Extensions.Logging;
using Npgsql;
using Xunit.Abstractions;

namespace FinancialTransactions.IntegrationTests;

/// <summary>
/// P07-EX-01 e P07-EX-02, com controle causal. Cada teste usa um par de bancos próprio e descartável.
/// </summary>
public sealed class ConsolidationObservationTests(ITestOutputHelper output)
{
    private const long BarrierKey = 7_770_001;

    private static async Task<NpgsqlConnection> OpenOwnSessionAsync(string connectionString)
    {
        // Sessão própria e sem pool: fechar a conexão encerra a sessão e libera qualquer lock que ela segure.
        var connection = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(connectionString) { Pooling = false }.ConnectionString);
        await connection.OpenAsync();
        return connection;
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    [Fact(DisplayName = "EX01_ObservedAt_IsNotLaterThanTheSnapshot_ACommitDuringTheObservationIsNotCoveredByTheAnnouncedInstant")]
    public async Task EX01ObservedAtIsNotLaterThanTheSnapshotACommitDuringTheObservationIsNotCoveredByTheAnnouncedInstant()
    {
        await using var rig = await ConsolidationRig.CreateAsync();

        // Uma linha já processada da conta A1 (para a função de barreira ser avaliada) e uma barreira DENTRO da instrução SQL de produção:
        // a tabela da Outbox é substituída, só neste banco descartável, por uma view cujo predicado chama uma função que espera um lock
        // consultivo. O snapshot da contagem já foi tomado quando a função bloqueia; a instrução fica retida no meio da execução.
        await rig.OriginExecuteAsync(
            """
            INSERT INTO consolidation_outbox (event_id, account_id, customer_id, type, amount, effective_at, created_at, origin, status, attempts)
            VALUES (gen_random_uuid(), @a, @c, 1, 1.00, now(), now(), 0, 1, 0)
            """,
            ("a", ConsolidationRig.AccountA1),
            ("c", ConsolidationRig.CustomerA));
        await rig.OriginExecuteAsync(
            $$"""
            ALTER TABLE consolidation_outbox RENAME TO consolidation_outbox_real;
            CREATE FUNCTION test_barrier() RETURNS boolean LANGUAGE plpgsql VOLATILE AS $f$
            BEGIN
                PERFORM pg_advisory_lock_shared({{BarrierKey}});
                PERFORM pg_advisory_unlock_shared({{BarrierKey}});
                RETURN true;
            END $f$;
            CREATE VIEW consolidation_outbox AS SELECT * FROM consolidation_outbox_real WHERE test_barrier();
            """);

        await using var holder = await OpenOwnSessionAsync(rig.OriginConnection);
        await ExecuteAsync(holder, $"SELECT pg_advisory_lock({BarrierKey})");

        var query = new UnresolvedEventsQuery(new TestConnectionFactory(rig.Real(2, 30)));
        var observation = Task.Run(() => query.ReadAsync([ConsolidationRig.AccountA1], CancellationToken.None));

        // A instrução está, de fato, bloqueada na função de barreira (espera por lock consultivo): controle causal, não tempo.
        await ConsolidationRig.Eventually(
            async () => await rig.OriginScalarAsync<long>(
                "SELECT count(*) FROM pg_stat_activity WHERE datname = current_database() AND wait_event_type = 'Lock' AND wait_event = 'advisory' AND query LIKE '%FILTER (WHERE status IN (0, 2))%'") == 1,
            "instrução diagnóstica retida na barreira");
        Assert.False(observation.IsCompleted);

        // Um commit concorrente, DEPOIS do snapshot da contagem e ANTES de a instrução terminar. O instante registrado é lido antes do COMMIT:
        // o commit acontece em ou depois dele.
        DateTimeOffset beforeCommit;
        await using (var writer = await OpenOwnSessionAsync(rig.OriginConnection))
        await using (var transaction = await writer.BeginTransactionAsync())
        {
            await using (var insert = new NpgsqlCommand(
                """
                INSERT INTO consolidation_outbox_real (event_id, account_id, customer_id, type, amount, effective_at, created_at, origin, status, attempts)
                VALUES (gen_random_uuid(), @a, @c, 1, 2.00, now(), now(), 0, 0, 0)
                """,
                writer,
                transaction))
            {
                insert.Parameters.AddWithValue("a", ConsolidationRig.AccountA1);
                insert.Parameters.AddWithValue("c", ConsolidationRig.CustomerA);
                await insert.ExecuteNonQueryAsync();
            }

            await using (var clock = new NpgsqlCommand("SELECT clock_timestamp()", writer, transaction))
            {
                beforeCommit = new DateTimeOffset(DateTime.SpecifyKind((DateTime)(await clock.ExecuteScalarAsync() ?? throw new InvalidOperationException("sem relógio")), DateTimeKind.Utc));
            }

            await transaction.CommitAsync();
        }

        Assert.Equal(1, await rig.OriginScalarAsync<long>("SELECT count(*) FROM consolidation_outbox_real WHERE status = 0")); // o commit concorrente existe
        await holder.CloseAsync(); // libera a barreira: a instrução termina

        var result = await observation;
        output.WriteLine($"EX01 contagem={result.Unresolved} observedAt={result.ObservedAt:o} instante antes do commit concorrente={beforeCommit:o}");

        // Premissa: o snapshot da instrução é anterior ao commit concorrente, então a contagem não o inclui (e isso é correto).
        Assert.Equal(0, result.Unresolved);

        // Propriedade: o instante anunciado não pode ser posterior a um commit que a contagem não cobriu. Com um relógio avaliado depois da
        // agregação, observedAt cairia depois da liberação da barreira, isto é, depois do commit concorrente, e esta asserção reprovaria.
        Assert.True(
            result.ObservedAt <= beforeCommit,
            $"observedAt ({result.ObservedAt:o}) é posterior ao commit concorrente não contado (≥ {beforeCommit:o}): a observação anunciaria um corte que não cobriu.");
    }

    [Fact(DisplayName = "EX02_Coordinator_ThatDidNotAcquireTheLock_DoesNotUnlock_AndDisposesItsSession_TheOwnerKeepsTheLock")]
    public async Task EX02CoordinatorThatDidNotAcquireTheLockDoesNotUnlockAndDisposesItsSessionTheOwnerKeepsTheLock()
    {
        await using var rig = await ConsolidationRig.CreateAsync();
        var provider = new CapturingLoggerProvider();
        using var loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(provider));
        var logger = loggerFactory.CreateLogger<BootstrapCoordinator>();

        // Outra sessão é a dona do lock do coordenador.
        var owner = await OpenOwnSessionAsync(rig.OriginConnection);
        try
        {
            await ExecuteAsync(owner, "SELECT pg_advisory_lock(hashtext('ft_backfill'))");
            var ownerPid = await rig.OriginScalarAsync<int>("SELECT pid FROM pg_locks WHERE locktype = 'advisory' AND granted LIMIT 1");
            var sessionsBefore = await rig.OriginScalarAsync<long>("SELECT count(*) FROM pg_stat_activity WHERE datname = current_database()");

            var exit = await rig.RunCoordinatorAsync(logger: logger);

            Assert.Equal(CoordinatorExitCodes.LockBusy, exit); // a disputa e o código 3 continuam
            Assert.Contains(provider.Messages, m => m.Contains("já em execução", StringComparison.Ordinal));
            Assert.DoesNotContain(provider.Messages, m => m.Contains("pg_advisory_unlock", StringComparison.Ordinal)); // não tentou liberar lock alheio

            // O dono continua dono, e o coordenador que perdeu a disputa descartou a própria sessão.
            Assert.Equal(1, await rig.OriginScalarAsync<long>("SELECT count(*) FROM pg_locks WHERE locktype = 'advisory' AND granted"));
            Assert.Equal(ownerPid, await rig.OriginScalarAsync<int>("SELECT pid FROM pg_locks WHERE locktype = 'advisory' AND granted LIMIT 1"));
            Assert.Equal(sessionsBefore, await rig.OriginScalarAsync<long>("SELECT count(*) FROM pg_stat_activity WHERE datname = current_database()"));
            output.WriteLine($"EX02 disputa: saida={exit}; lock ainda do pid {ownerPid}; sessoes antes={sessionsBefore} depois={await rig.OriginScalarAsync<long>("SELECT count(*) FROM pg_stat_activity WHERE datname = current_database()")}");
        }
        finally
        {
            await owner.DisposeAsync();
        }

        // Quem adquire, libera: depois de uma execução bem-sucedida não resta lock consultivo, e não houve aviso de unlock.
        var second = await rig.RunCoordinatorAsync(logger: logger);
        Assert.Equal(CoordinatorExitCodes.Success, second);
        Assert.DoesNotContain(provider.Messages, m => m.Contains("pg_advisory_unlock", StringComparison.Ordinal));
        Assert.Equal(0, await rig.OriginScalarAsync<long>("SELECT count(*) FROM pg_locks WHERE locktype = 'advisory' AND granted"));
        output.WriteLine($"EX02 execucao seguinte: saida={second}; locks consultivos restantes=0");
    }
}
