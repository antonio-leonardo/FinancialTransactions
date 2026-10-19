using System.Net.Sockets;
using FinancialTransactions.Api.Http;
using Npgsql;

namespace FinancialTransactions.UnitTests;

public class DependencyUnavailableTests
{
    [Fact(DisplayName = "IsDependencyUnavailable_TrueForTimeoutException")]
    public void TrueForTimeoutException()
    {
        Assert.True(ApiExceptionHandler.IsDependencyUnavailable(new TimeoutException()));
    }

    [Fact(DisplayName = "IsDependencyUnavailable_TrueForSocketException")]
    public void TrueForSocketException()
    {
        Assert.True(ApiExceptionHandler.IsDependencyUnavailable(new SocketException()));
    }

    [Fact(DisplayName = "IsDependencyUnavailable_TrueForTransientNpgsqlException")]
    public void TrueForTransientNpgsqlException()
    {
        var exception = new NpgsqlException("x", new SocketException());

        Assert.True(exception.IsTransient);
        Assert.True(ApiExceptionHandler.IsDependencyUnavailable(exception));
    }

    [Fact(DisplayName = "IsDependencyUnavailable_FalseForInvalidOperationException")]
    public void FalseForInvalidOperationException()
    {
        Assert.False(ApiExceptionHandler.IsDependencyUnavailable(new InvalidOperationException("x")));
    }

    [Fact(DisplayName = "IsDependencyUnavailable_FalseForUniqueViolation")]
    public void FalseForUniqueViolation()
    {
        var exception = new PostgresException(
            messageText: "duplicate key value violates unique constraint",
            severity: "ERROR",
            invariantSeverity: "ERROR",
            sqlState: PostgresErrorCodes.UniqueViolation);

        Assert.False(ApiExceptionHandler.IsDependencyUnavailable(exception));
    }
}
