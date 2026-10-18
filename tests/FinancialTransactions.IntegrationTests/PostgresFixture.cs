using System.Security.Cryptography;
using FinancialTransactions.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace FinancialTransactions.IntegrationTests;

/// <summary>
/// Banco PostgreSQL real e descartável por execução: ft_test_&lt;8 hex&gt; criado e migrado em InitializeAsync,
/// removido com DROP DATABASE ... WITH (FORCE) em DisposeAsync. Nenhum provider em memória.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime, IDisposable
{
    private const string DefaultAdminConnection = "Host=127.0.0.1;Port=15432;Database=postgres;Username=ft;Password=ft_local_dev_only";

    private readonly string _adminConnection =
        Environment.GetEnvironmentVariable("FT_TEST_ADMIN_CONNECTION") ?? DefaultAdminConnection;

    private readonly string _databaseName = "ft_test_" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4));
    private ApiFactory? _factory;

    /// <summary>Chave gerada por execução, nunca constante.</summary>
    public string SigningKey { get; } = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48));

    public string ConnectionString { get; private set; } = string.Empty;

    public ApiFactory Factory => _factory ?? throw new InvalidOperationException("Fixture ainda não inicializada.");

    public async Task InitializeAsync()
    {
        ConnectionString = new NpgsqlConnectionStringBuilder(_adminConnection) { Database = _databaseName }.ConnectionString;

        // MigrateAsync cria o banco se não existir (via banco de manutenção) e aplica InitialSchema com o seed.
        var options = new DbContextOptionsBuilder<FinancialTransactionsDbContext>().UseNpgsql(ConnectionString).Options;
        await using (var db = new FinancialTransactionsDbContext(options))
        {
            await db.Database.MigrateAsync();
        }

        _factory = new ApiFactory(ConnectionString, SigningKey);
    }

    public async Task DisposeAsync()
    {
        if (_factory is not null)
        {
            await _factory.DisposeAsync();
            _factory = null;
        }

        NpgsqlConnection.ClearAllPools();

        await using var connection = new NpgsqlConnection(_adminConnection);
        await connection.OpenAsync();
        // Identificador gerado internamente (prefixo fixo + hex), não vem de entrada externa.
        await using var drop = connection.CreateCommand();
        drop.CommandText = $"DROP DATABASE IF EXISTS \"{_databaseName}\" WITH (FORCE)";
        await drop.ExecuteNonQueryAsync();
    }

    public void Dispose()
    {
        _factory?.Dispose();
        _factory = null;
    }

    public NpgsqlConnection OpenTestConnection()
    {
        var connection = new NpgsqlConnection(ConnectionString);
        connection.Open();
        return connection;
    }
}

[CollectionDefinition(Name)]
public sealed class PostgresCollectionDefinition : ICollectionFixture<PostgresFixture>
{
    public const string Name = "postgres";
}
