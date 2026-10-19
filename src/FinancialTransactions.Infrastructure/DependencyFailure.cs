using System.Net.Sockets;
using Npgsql;

namespace FinancialTransactions.Infrastructure;

/// <summary>Classificação única de "o banco não respondeu", usada pelas APIs e pelo Worker.</summary>
public static class DependencyFailure
{
    /// <summary>
    /// Verdadeiro quando a exceção (ou a sua <c>InnerException</c>, um nível) indica que o banco não respondeu:
    /// <c>NpgsqlException</c> transitória, <c>TimeoutException</c> ou <c>SocketException</c>. Violações de constraint
    /// (<c>PostgresException</c> 23505) e demais falhas não são indisponibilidade. <c>OperationCanceledException</c> pura
    /// não é classificada aqui: quem a trata sabe de qual token ela veio.
    /// </summary>
    public static bool IsUnavailable(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return Matches(exception) || (exception.InnerException is { } inner && Matches(inner));
    }

    private static bool Matches(Exception exception)
    {
        return exception switch
        {
            TimeoutException or SocketException => true,
            NpgsqlException { IsTransient: true } => true,
            NpgsqlException { InnerException: TimeoutException or SocketException } => true,
            _ => false,
        };
    }
}
