using Microsoft.Extensions.Options;
using Npgsql;

namespace FinancialTransactions.Consolidation;

/// <summary>Quem está pedindo a conexão. Permite à fixture de teste tornar a projeção inalcançável só para um papel.</summary>
public enum ConsolidationConnectionRole
{
    /// <summary>Laço do consumidor (leitura da fase, aplicação dos eventos).</summary>
    Consumer,

    /// <summary>Reconciliação do conjunto inicial e promoção para live.</summary>
    Reconcile,

    /// <summary>Sondas de saúde.</summary>
    Health,

    /// <summary>Coordenador do bootstrap.</summary>
    Bootstrap,

    /// <summary>Consultas da API do consolidado.</summary>
    Query,
}

/// <summary>As duas connection strings resolvidas para um papel: origem (lançamentos e Outbox) e projeção (consolidado).</summary>
public sealed record ConsolidationConnectionStrings(string Origin, string Projection);

/// <summary>
/// Resolve as connection strings por papel. Em produção lê a configuração uma vez; no host de teste a implementação pode ser
/// trocada e é consultada a cada tentativa, o que permite tornar a projeção inalcançável e depois recuperá-la sem tocar no host.
/// </summary>
public interface IConsolidationConnectionFactory
{
    ConsolidationConnectionStrings Open(ConsolidationConnectionRole role);
}

public sealed class ConsolidationConnectionOptions
{
    public string Origin { get; set; } = string.Empty;

    public string Projection { get; set; } = string.Empty;

    /// <summary>Segundos de <c>Timeout</c> (abertura de conexão) aplicados às duas strings.</summary>
    public int ConnectTimeoutSeconds { get; set; } = 5;

    /// <summary>Segundos de <c>Command Timeout</c> aplicados às duas strings.</summary>
    public int CommandTimeoutSeconds { get; set; } = 15;
}

/// <summary>Validação na inicialização: ausência ou valor inválido impede o host de iniciar, com mensagem clara.</summary>
public sealed class ConsolidationConnectionOptionsValidator : IValidateOptions<ConsolidationConnectionOptions>
{
    public ValidateOptionsResult Validate(string? name, ConsolidationConnectionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var errors = new List<string>();
        CheckConnection(options.Origin, ConsolidationConfiguration.OriginKey, errors);
        CheckConnection(options.Projection, ConsolidationConfiguration.ProjectionKey, errors);
        if (errors.Count == 0 && string.Equals(options.Origin, options.Projection, StringComparison.Ordinal))
        {
            errors.Add("ConnectionStrings:FinancialTransactions e ConnectionStrings:Consolidation não podem ser iguais: origem e projeção são bancos distintos.");
        }

        if (options.ConnectTimeoutSeconds < 1 || options.CommandTimeoutSeconds < 1)
        {
            errors.Add("Timeout e Command Timeout precisam ser de pelo menos 1 segundo.");
        }

        if (!BusinessDay.IsTimeZoneAvailable(out var zoneError))
        {
            errors.Add(zoneError);
        }

        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }

    private static void CheckConnection(string value, string key, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            errors.Add($"ConnectionStrings:{key} ausente. Use appsettings.Development.json, variável de ambiente (FT__ConnectionStrings__{key}) ou user-secrets.");
            return;
        }

        try
        {
            _ = new NpgsqlConnectionStringBuilder(value);
        }
        catch (ArgumentException)
        {
            // Sem a connection string na mensagem: ela pode carregar credencial.
            errors.Add($"ConnectionStrings:{key} não é uma connection string válida do PostgreSQL.");
        }
    }
}

/// <summary>Nomes das chaves de configuração compartilhadas pelos processos.</summary>
public static class ConsolidationConfiguration
{
    public const string OriginKey = "FinancialTransactions";
    public const string ProjectionKey = "Consolidation";
}

/// <summary>Implementação de produção: lê a configuração uma vez e aplica os prazos do processo às duas strings.</summary>
public sealed class ConfigurationConsolidationConnectionFactory : IConsolidationConnectionFactory
{
    private readonly ConsolidationConnectionStrings _strings;

    public ConfigurationConsolidationConnectionFactory(IOptions<ConsolidationConnectionOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var value = options.Value;
        _strings = new ConsolidationConnectionStrings(
            WithTimeouts(value.Origin, value),
            WithTimeouts(value.Projection, value));
    }

    public ConsolidationConnectionStrings Open(ConsolidationConnectionRole role) => _strings;

    private static string WithTimeouts(string connectionString, ConsolidationConnectionOptions options)
    {
        return new NpgsqlConnectionStringBuilder(connectionString)
        {
            Timeout = options.ConnectTimeoutSeconds,
            CommandTimeout = options.CommandTimeoutSeconds,
        }.ConnectionString;
    }
}

/// <summary>Papel da conexão no escopo corrente; lido pela fábrica do <see cref="ConsolidationDbContext"/>.</summary>
public sealed class ConsolidationScopeRole
{
    public ConsolidationConnectionRole Role { get; set; } = ConsolidationConnectionRole.Consumer;
}
