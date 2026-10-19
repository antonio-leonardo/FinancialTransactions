using FinancialTransactions.Infrastructure;
using Microsoft.AspNetCore.Diagnostics;

namespace FinancialTransactions.Api.Http;

/// <summary>
/// Ponto único de tradução de exceção em resposta HTTP. Corpo ilegível vira 422 validation_error; corpo acima do limite
/// vira 413; dependência (banco) indisponível vira 503 dependency_unavailable; qualquer outra falha vira 500
/// internal_error, registrada no log. Nenhuma resposta leva detalhes da exceção.
/// </summary>
public sealed class ApiExceptionHandler(ILogger<ApiExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        ArgumentNullException.ThrowIfNull(exception);

        var method = httpContext.Request.Method;
        var path = httpContext.Request.Path.Value ?? string.Empty;

        IResult result;
        if (exception is BadHttpRequestException { StatusCode: StatusCodes.Status413PayloadTooLarge })
        {
            result = ApiProblems.PayloadTooLarge();
        }
        else if (exception is BadHttpRequestException)
        {
            result = ApiProblems.Unprocessable("validation_error", "Requisição inválida: corpo, cabeçalho ou parâmetro não pôde ser lido.");
        }
        else if (IsDependencyUnavailable(exception))
        {
            Log.DependencyUnavailable(logger, method, path, httpContext.TraceIdentifier);
            result = ApiProblems.DependencyUnavailable();
        }
        else
        {
            Log.UnhandledException(logger, exception, method, path);
            result = ApiProblems.Problem(StatusCodes.Status500InternalServerError, ApiProblems.InternalErrorCode, "Falha inesperada. Consulte o log do servidor.");
        }

        await result.ExecuteAsync(httpContext).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Verdadeiro quando a exceção (ou a sua <c>InnerException</c>, um nível) indica que o banco não respondeu:
    /// <c>NpgsqlException</c> transitória, <c>TimeoutException</c> ou <c>SocketException</c>. Violações de constraint
    /// (<c>PostgresException</c> 23505) e demais falhas não são indisponibilidade.
    /// </summary>
    public static bool IsDependencyUnavailable(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return DependencyFailure.IsUnavailable(exception);
    }
}
