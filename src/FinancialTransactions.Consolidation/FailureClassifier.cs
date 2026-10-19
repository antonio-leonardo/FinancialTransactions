using FinancialTransactions.Infrastructure;
using Npgsql;

namespace FinancialTransactions.Consolidation;

public enum FailureClass
{
    /// <summary>Infraestrutura não respondeu a tempo: não conta tentativa, lote abandonado, backoff.</summary>
    Transient,

    /// <summary>Encerramento do host: sai limpo, nada é marcado.</summary>
    HostCancellation,

    /// <summary>Afeta todos os eventos (esquema, credencial, recurso, constraint fora da lista, exceção desconhecida): não conta tentativa, backoff, Degraded.</summary>
    Environment,

    /// <summary>Atribuível aos dados do evento: conta tentativa; a partir de 5, quarentena.</summary>
    Event,
}

/// <summary>
/// Classificação das falhas do consumidor (4.3). A atribuição ao evento é concreta (validação própria, overflow, constraint de uma lista
/// fechada), nunca por exclusão: o que não se reconhece é ambiente e não manda evento algum para a quarentena.
/// </summary>
public static class FailureClassifier
{
    /// <summary>Lista fechada de constraints de domínio do agregado; qualquer outra violação é falha de ambiente.</summary>
    public static readonly IReadOnlySet<string> EventDomainConstraints = new HashSet<string>(StringComparer.Ordinal)
    {
        ConsolidationDbContext.CreditsNonNegative,
        ConsolidationDbContext.DebitsNonNegative,
        ConsolidationDbContext.MovementCountNonNegative,
        ConsolidationDbContext.NetIsCreditsMinusDebits,
    };

    public static FailureClass Classify(Exception exception, CancellationToken hostToken)
    {
        ArgumentNullException.ThrowIfNull(exception);

        if (exception is OperationCanceledException)
        {
            // Com o token do host sinalizado é encerramento; sem ele, a causa é o orçamento interno da tentativa (timeout).
            return hostToken.IsCancellationRequested ? FailureClass.HostCancellation : FailureClass.Transient;
        }

        var chain = Chain(exception).ToList();

        if (chain.Exists(e => e is InvalidEventException or OverflowException))
        {
            return FailureClass.Event;
        }

        if (chain.OfType<PostgresException>().FirstOrDefault() is { } postgres)
        {
            return ClassifyPostgres(postgres);
        }

        return DependencyFailure.IsUnavailable(exception) ? FailureClass.Transient : FailureClass.Environment;
    }

    private static FailureClass ClassifyPostgres(PostgresException exception)
    {
        var state = exception.SqlState;

        // 22003 (valor numérico fora da faixa) vindo do UPSERT é overflow ao aplicar o valor do evento. As demais 22 não
        // se atribuem a um valor do evento com segurança.
        if (state == PostgresErrorCodes.NumericValueOutOfRange)
        {
            return FailureClass.Event;
        }

        if (state.StartsWith("23", StringComparison.Ordinal))
        {
            return exception.ConstraintName is { } name && EventDomainConstraints.Contains(name)
                ? FailureClass.Event
                : FailureClass.Environment;
        }

        // Banco desligando/iniciando, conexão rompida, deadlock ou falha de serialização: passam sozinhos.
        if (state.StartsWith("08", StringComparison.Ordinal)
            || state is PostgresErrorCodes.AdminShutdown or PostgresErrorCodes.CrashShutdown or PostgresErrorCodes.CannotConnectNow
                or PostgresErrorCodes.SerializationFailure or PostgresErrorCodes.DeadlockDetected)
        {
            return FailureClass.Transient;
        }

        // Classes 42, 28, 3D, 53, demais 22 e tudo o que não se reconhece: ambiente.
        return FailureClass.Environment;
    }

    private static IEnumerable<Exception> Chain(Exception exception)
    {
        var current = exception;
        for (var depth = 0; current is not null && depth < 4; depth++)
        {
            yield return current;
            current = current.InnerException;
        }
    }

    /// <summary>
    /// Texto curto e seguro para <c>last_error</c> e logs: tipo, SQLSTATE e nome de constraint quando houver, e a mensagem só de
    /// <see cref="InvalidEventException"/> (nossa). Nunca connection string, corpo, token nem <c>ToString()</c> da exceção.
    /// </summary>
    public static string Sanitize(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var postgres = Chain(exception).OfType<PostgresException>().FirstOrDefault();
        var text = (exception, postgres) switch
        {
            (InvalidEventException invalid, _) => $"{nameof(InvalidEventException)}: {invalid.Message}",
            (_, { } pg) => $"{nameof(PostgresException)}: SQLSTATE {pg.SqlState}" + (pg.ConstraintName is { } constraint ? $" ({constraint})" : string.Empty),
            _ => exception.GetType().Name,
        };
        return text.Length <= 200 ? text : text[..200];
    }
}
