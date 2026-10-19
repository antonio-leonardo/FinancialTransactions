using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using FinancialTransactions.Consolidation;
using FinancialTransactions.Consolidation.Api;
using FinancialTransactions.Consolidation.Worker;
using FinancialTransactions.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;

namespace FinancialTransactions.IntegrationTests;

/// <summary>Conexões administrativas das duas instâncias, só para criar, verificar e remover os bancos próprios do teste.</summary>
public static class TestAdmin
{
    public static string Origin { get; } = Environment.GetEnvironmentVariable("FT_TEST_ADMIN_CONNECTION")
        ?? "Host=127.0.0.1;Port=15432;Database=postgres;Username=ft;Password=ft_local_dev_only";

    public static string Consolidation { get; } = Environment.GetEnvironmentVariable("FT_TEST_CONSOLIDATION_ADMIN_CONNECTION")
        ?? "Host=127.0.0.1;Port=15433;Database=postgres;Username=ft;Password=ft_local_dev_only";

    public static async Task CreateDatabaseAsync(string adminConnection, string name)
    {
        await using var connection = new NpgsqlConnection(adminConnection);
        await connection.OpenAsync();
        // Identificador gerado internamente (prefixo fixo + hex), não vem de entrada externa.
        await using var create = new NpgsqlCommand($"CREATE DATABASE \"{name}\"", connection);
        await create.ExecuteNonQueryAsync();
    }

    public static async Task DropDatabaseAsync(string adminConnection, string name)
    {
        NpgsqlConnection.ClearAllPools();
        await using var connection = new NpgsqlConnection(adminConnection);
        await connection.OpenAsync();
        await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{name}\" WITH (FORCE)", connection);
        await drop.ExecuteNonQueryAsync();
    }

    public static string Hex() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4));
}

/// <summary>O Worker em modo serviço, rodando no processo de teste com Kestrel real em porta livre.</summary>
public sealed class WorkerHandle(WebApplication app, TestConnectionFactory connections, TestDiagnostics diagnostics, ConsolidationConsumer consumer) : IAsyncDisposable
{
    private int _disposed;
    private readonly HttpClient _http = new() { BaseAddress = new Uri(app.Urls.First()) };

    public WebApplication App => app;

    public TestConnectionFactory Connections => connections;

    public TestDiagnostics Diagnostics => diagnostics;

    public ConsolidationConsumer Consumer => consumer;

    public bool LoopCompleted => consumer.ExecuteTask?.IsCompleted ?? true;

    public async Task<(HttpStatusCode Status, string Body)> GetAsync(string path)
    {
        using var response = await _http.GetAsync(new Uri(path, UriKind.Relative));
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    /// <summary>Encerra o host e confirma a parada: devolve verdadeiro só se a tarefa do laço terminou dentro do prazo.</summary>
    public async Task<bool> StopAsync(TimeSpan timeout)
    {
        using var deadline = new CancellationTokenSource(timeout);
        try
        {
            await app.StopAsync(deadline.Token);
        }
        catch (OperationCanceledException)
        {
            return LoopCompleted;
        }

        if (consumer.ExecuteTask is { } task)
        {
            await Task.WhenAny(task, Task.Delay(timeout));
        }

        return LoopCompleted;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        _http.Dispose();
        await StopAsync(TimeSpan.FromSeconds(10));
        await app.DisposeAsync();
    }
}

/// <summary>A API do consolidado, rodando no processo de teste com Kestrel real em porta livre.</summary>
public sealed class ConsolidationApiHandle(WebApplication app, TestConnectionFactory connections, ApiFactory tokens) : IAsyncDisposable
{
    private int _disposed;
    private readonly Uri _baseAddress = new(app.Urls.First());
    private readonly HttpClient _http = new() { BaseAddress = new Uri(app.Urls.First()), Timeout = TimeSpan.FromSeconds(30) };

    public Uri BaseAddress => _baseAddress;

    public TestConnectionFactory Connections => connections;

    public async Task<(HttpStatusCode Status, string Body)> GetAsync(string path, Guid? customerId, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(path, UriKind.Relative));
        if (customerId is { } customer)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokens.CreateToken(customer, TimeSpan.FromMinutes(10)));
        }

        using var response = await _http.SendAsync(request, cancellationToken);
        return (response.StatusCode, await response.Content.ReadAsStringAsync(cancellationToken));
    }

    public Task<(HttpStatusCode Status, string Body)> GetAccountAsync(Guid customerId, Guid accountId, string day, CancellationToken cancellationToken = default) =>
        GetAsync($"/api/v1/accounts/{accountId}/consolidation/daily?day={day}", customerId, cancellationToken);

    public Task<(HttpStatusCode Status, string Body)> GetCustomerAsync(Guid customerId, string day) =>
        GetAsync($"/api/v1/customers/{customerId}/consolidation/daily?day={day}", customerId);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        _http.Dispose();
        await app.StopAsync();
        await app.DisposeAsync();
    }
}

public sealed class WorkerStart
{
    public TestConnectionFactory? Factory { get; set; }

    public TestDiagnostics? Diagnostics { get; set; }

    public IConsolidationFailurePoints? FailurePoints { get; set; }

    public Action<ConsolidationConsumerOptions>? ConfigureConsumer { get; set; }
}

/// <summary>
/// Um par de bancos descartáveis (origem em 15432, consolidação em 15433) e tudo o que roda sobre eles: a API de lançamentos, o Worker
/// em modo serviço e a API do consolidado, como hosts no processo de teste, mais o coordenador do bootstrap. Remove os bancos ao final.
/// </summary>
public sealed class ConsolidationRig : IAsyncDisposable
{
    // Seed fixo da migration (literais independentes do código de produção).
    public static readonly Guid CustomerA = Guid.Parse("11111111-1111-1111-1111-111111111111");
    public static readonly Guid CustomerB = Guid.Parse("22222222-2222-2222-2222-222222222222");
    public static readonly Guid AccountA1 = Guid.Parse("aaaaaaa1-0000-0000-0000-000000000001");
    public static readonly Guid AccountA2 = Guid.Parse("aaaaaaa2-0000-0000-0000-000000000002");
    public static readonly Guid AccountB1 = Guid.Parse("bbbbbbb1-0000-0000-0000-000000000001");

    private readonly List<IAsyncDisposable> _handles = [];

    private ConsolidationRig(string originDatabase, string projectionDatabase, string originConnection, string projectionConnection, string signingKey)
    {
        OriginDatabase = originDatabase;
        ProjectionDatabase = projectionDatabase;
        OriginConnection = originConnection;
        ProjectionConnection = projectionConnection;
        SigningKey = signingKey;
        Writes = new ApiFactory(originConnection, signingKey);
    }

    public string OriginDatabase { get; }

    public string ProjectionDatabase { get; }

    public string OriginConnection { get; }

    public string ProjectionConnection { get; }

    public string SigningKey { get; }

    /// <summary>A API de lançamentos (o mesmo host real dos testes anteriores), apontada para a origem deste par.</summary>
    public ApiFactory Writes { get; }

    /// <summary>Marcado quando a limpeza não pôde ser feita porque o Worker não parou no prazo.</summary>
    public string? Contaminated { get; set; }

    public static async Task<ConsolidationRig> CreateAsync()
    {
        var hex = TestAdmin.Hex();
        var originDatabase = "ft_test_" + hex;
        var projectionDatabase = "ft_cons_test_" + hex;
        var origin = new NpgsqlConnectionStringBuilder(TestAdmin.Origin) { Database = originDatabase }.ConnectionString;
        var projection = new NpgsqlConnectionStringBuilder(TestAdmin.Consolidation) { Database = projectionDatabase }.ConnectionString;

        var options = new DbContextOptionsBuilder<FinancialTransactionsDbContext>().UseNpgsql(origin).Options;
        await using (var db = new FinancialTransactionsDbContext(options))
        {
            await db.Database.MigrateAsync();
        }

        var projectionOptions = new DbContextOptionsBuilder<ConsolidationDbContext>().UseNpgsql(projection).Options;
        await using (var db = new ConsolidationDbContext(projectionOptions))
        {
            await db.Database.MigrateAsync();
        }

        return new ConsolidationRig(originDatabase, projectionDatabase, origin, projection, Convert.ToBase64String(RandomNumberGenerator.GetBytes(48)));
    }

    /// <summary>Strings reais deste par, com os prazos pedidos.</summary>
    public ConsolidationConnectionStrings Real(int connectTimeout = 2, int commandTimeout = 10) => new(
        new NpgsqlConnectionStringBuilder(OriginConnection) { Timeout = connectTimeout, CommandTimeout = commandTimeout }.ConnectionString,
        new NpgsqlConnectionStringBuilder(ProjectionConnection) { Timeout = connectTimeout, CommandTimeout = commandTimeout }.ConnectionString);

    // ---------- hosts ----------

    public async Task<WorkerHandle> StartWorkerAsync(WorkerStart? start = null)
    {
        start ??= new WorkerStart();
        var factory = start.Factory ?? new TestConnectionFactory(Real());
        var diagnostics = start.Diagnostics ?? new TestDiagnostics();

        var app = WorkerApp.CreateService([], builder =>
        {
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Logging.ClearProviders();
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:FinancialTransactions"] = OriginConnection,
                ["ConnectionStrings:Consolidation"] = ProjectionConnection,
            });
            builder.Services.RemoveAll<IConsolidationConnectionFactory>();
            builder.Services.AddSingleton<IConsolidationConnectionFactory>(factory);
            builder.Services.RemoveAll<IConsolidationDiagnostics>();
            builder.Services.AddSingleton<IConsolidationDiagnostics>(diagnostics);
            if (start.FailurePoints is { } failurePoints)
            {
                builder.Services.RemoveAll<IConsolidationFailurePoints>();
                builder.Services.AddSingleton(failurePoints);
            }

            // Intervalos reduzidos só nos testes; os de produção (500 ms, 2 s, 1 s a 30 s) são cobertos por teste de unidade.
            builder.Services.Configure<ConsolidationConsumerOptions>(o =>
            {
                o.IdleDelay = TimeSpan.FromMilliseconds(50);
                o.WaitDelay = TimeSpan.FromMilliseconds(100);
                o.BackoffInitial = TimeSpan.FromMilliseconds(100);
                o.BackoffMax = TimeSpan.FromSeconds(5);
                o.AttemptBudget = TimeSpan.FromSeconds(20);
                start.ConfigureConsumer?.Invoke(o);
            });
        });

        await app.StartAsync();
        var consumer = app.Services.GetServices<IHostedService>().OfType<ConsolidationConsumer>().Single();
        var handle = new WorkerHandle(app, factory, diagnostics, consumer);
        _handles.Add(handle);
        return handle;
    }

    public async Task<ConsolidationApiHandle> StartConsolidationApiAsync(
        TestConnectionFactory? factory = null,
        IConsolidationApiFailurePoints? failurePoints = null,
        ILoggerProvider? logger = null,
        Action<ConsolidationApiOptions>? configureApi = null)
    {
        var connections = factory ?? new TestConnectionFactory(Real(1, 1));
        var app = ConsolidationApiApp.Create([], builder =>
        {
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Logging.ClearProviders();
            if (logger is not null)
            {
                builder.Logging.AddProvider(logger);
            }

            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:FinancialTransactions"] = OriginConnection,
                ["ConnectionStrings:Consolidation"] = ProjectionConnection,
                ["Auth:SigningKey"] = SigningKey,
            });
            builder.Services.RemoveAll<IConsolidationConnectionFactory>();
            builder.Services.AddSingleton<IConsolidationConnectionFactory>(connections);
            if (configureApi is not null)
            {
                builder.Services.Configure(configureApi);
            }

            if (failurePoints is not null)
            {
                builder.Services.RemoveAll<IConsolidationApiFailurePoints>();
                builder.Services.AddSingleton(failurePoints);
            }
        });

        await app.StartAsync();
        var handle = new ConsolidationApiHandle(app, connections, Writes);
        _handles.Add(handle);
        return handle;
    }

    /// <summary>Executa o coordenador em processo (a mesma classe do modo --backfill) e devolve o código de saída.</summary>
    public Task<int> RunCoordinatorAsync(IBootstrapFailurePoints? points = null, TestConnectionFactory? factory = null, ILogger<BootstrapCoordinator>? logger = null, CancellationToken cancellationToken = default)
    {
        var coordinator = new BootstrapCoordinator(
            factory ?? new TestConnectionFactory(Real()),
            points ?? new NoBootstrapFailurePoints(),
            logger ?? NullLogger<BootstrapCoordinator>.Instance);
        return coordinator.RunAsync(cancellationToken);
    }

    /// <summary>Coordenador com conjunto vazio e Worker até phase live: a pré-condição dos cenários que não são do bootstrap.</summary>
    public async Task BringToLiveAsync()
    {
        Assert.Equal(CoordinatorExitCodes.Success, await RunCoordinatorAsync());
        var worker = await StartWorkerAsync();
        await Eventually(async () => await ProjectionScalarAsync<string?>("SELECT phase FROM consolidation_bootstrap WHERE id = 1") == "live", "phase live");
        Assert.True(await worker.StopAsync(TimeSpan.FromSeconds(10)));
    }

    // ---------- lançamentos ----------

    public static string Iso(DateTimeOffset instant) => instant.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    public async Task<(HttpStatusCode Status, Guid? MovementId, string Body)> PostAsync(
        Guid customerId, Guid accountId, string type, decimal amount, DateTimeOffset effectiveAt, string? key = null, Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program>? factory = null)
    {
        using var client = factory is null ? Writes.CreateClient(customerId) : Writes.AuthenticatedClient(factory, customerId);
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri($"/api/v1/accounts/{accountId}/movements", UriKind.Relative));
        request.Headers.Add("Idempotency-Key", key ?? "rig-" + Guid.NewGuid().ToString("N"));
        request.Content = JsonContent.Create(new { type, amount, effectiveAt = Iso(effectiveAt) });
        using var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        Guid? id = null;
        if (response.StatusCode is HttpStatusCode.Created or HttpStatusCode.OK)
        {
            using var document = JsonDocument.Parse(body);
            id = document.RootElement.GetProperty("movementId").GetGuid();
        }

        return (response.StatusCode, id, body);
    }

    public async Task<Guid> PostOkAsync(Guid customerId, Guid accountId, string type, decimal amount, DateTimeOffset effectiveAt)
    {
        var (status, id, body) = await PostAsync(customerId, accountId, type, amount, effectiveAt);
        Assert.True(status == HttpStatusCode.Created, $"POST esperado 201, obtido {(int)status}: {body}");
        return id ?? throw new InvalidOperationException("201 sem movementId.");
    }

    /// <summary>Movimento legado: inserido por SQL, sem Outbox, como se tivesse nascido antes da Outbox existir.</summary>
    public async Task<Guid> InsertLegacyMovementAsync(Guid accountId, string type, decimal amount, DateTimeOffset effectiveAt)
    {
        var id = Guid.CreateVersion7();
        await OriginExecuteAsync(
            """
            INSERT INTO movements (id, account_id, type, amount, currency, effective_at, registered_at, idempotency_key, request_fingerprint, description)
            VALUES (@id, @account, @type, @amount, 'BRL', @effective, @registered, @key, @fingerprint, NULL)
            """,
            ("id", id),
            ("account", accountId),
            ("type", (short)(type == "Credit" ? 1 : 2)),
            ("amount", amount),
            ("effective", effectiveAt),
            ("registered", DateTimeOffset.UtcNow),
            ("key", "legacy-" + id.ToString("N")),
            ("fingerprint", new string('a', 64)));
        return id;
    }

    // ---------- SQL de verificação (esperados independentes do código de produção) ----------

    public Task<T?> OriginScalarAsync<T>(string sql, params (string Name, object? Value)[] parameters) => ScalarAsync<T>(OriginConnection, sql, parameters);

    public Task<T?> ProjectionScalarAsync<T>(string sql, params (string Name, object? Value)[] parameters) => ScalarAsync<T>(ProjectionConnection, sql, parameters);

    public Task OriginExecuteAsync(string sql, params (string Name, object? Value)[] parameters) => ExecuteAsync(OriginConnection, sql, parameters);

    public Task ProjectionExecuteAsync(string sql, params (string Name, object? Value)[] parameters) => ExecuteAsync(ProjectionConnection, sql, parameters);

    private static async Task<T?> ScalarAsync<T>(string connectionString, string sql, (string Name, object? Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        var result = await command.ExecuteScalarAsync();
        if (result is null or DBNull)
        {
            return default;
        }

        return result is T typed ? typed : throw new InvalidCastException($"SQL devolveu {result.GetType().Name}; o teste pediu {typeof(T).Name}.");
    }

    private static async Task ExecuteAsync(string connectionString, string sql, (string Name, object? Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        await command.ExecuteNonQueryAsync();
    }

    public async Task<IReadOnlyList<Guid>> OriginGuidsAsync(string sql, params (string Name, object? Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(OriginConnection);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        var ids = new List<Guid>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            ids.Add(reader.GetGuid(0));
        }

        return ids;
    }

    public async Task<IReadOnlyList<Guid>> ProjectionGuidsAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(ProjectionConnection);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        var ids = new List<Guid>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            ids.Add(reader.GetGuid(0));
        }

        return ids;
    }

    public Task<long> ProcessedCountAsync() => ProjectionScalarAsync<long>("SELECT COUNT(*) FROM processed_events");

    public Task<long> OutboxCountAsync(short status) => OriginScalarAsync<long>("SELECT COUNT(*) FROM consolidation_outbox WHERE status = @status", ("status", status));

    public Task<string?> PhaseAsync() => ProjectionScalarAsync<string?>("SELECT phase FROM consolidation_bootstrap WHERE id = 1");

    /// <summary>Espera uma condição observável (consulta ao banco, evento do sink), sem atraso fixo às cegas.</summary>
    public static async Task Eventually(Func<Task<bool>> condition, string description, TimeSpan? timeout = null)
    {
        var deadline = DateTimeOffset.UtcNow + (timeout ?? TimeSpan.FromSeconds(30));
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await condition())
            {
                return;
            }

            await Task.Delay(40);
        }

        Assert.Fail($"Condição não observada no prazo: {description}");
    }

    public static T Parse<T>(string body)
    {
        var value = JsonSerializer.Deserialize<T>(body, JsonSerializerOptions.Web);
        return value ?? throw new InvalidOperationException("Corpo sem conteúdo.");
    }

    /// <summary>Apaga os dados das contas do teste, nas quatro tabelas; só deve ser chamado depois da parada comprovada do Worker.</summary>
    public async Task CleanAccountsAsync(IReadOnlyCollection<Guid> accounts)
    {
        var ids = accounts.ToArray();
        await OriginExecuteAsync("DELETE FROM consolidation_outbox WHERE account_id = ANY(@ids); DELETE FROM movements WHERE account_id = ANY(@ids)", ("ids", ids));
        await ProjectionExecuteAsync("DELETE FROM processed_events WHERE account_id = ANY(@ids); DELETE FROM daily_account_consolidation WHERE account_id = ANY(@ids)", ("ids", ids));
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var handle in _handles.AsEnumerable().Reverse())
        {
            await handle.DisposeAsync();
        }

        await Writes.DisposeAsync();
        await TestAdmin.DropDatabaseAsync(TestAdmin.Origin, OriginDatabase);
        await TestAdmin.DropDatabaseAsync(TestAdmin.Consolidation, ProjectionDatabase);
    }
}
