using Microsoft.AspNetCore.Diagnostics;

namespace FinancialTransactions.Api.Http;

/// <summary>Falha de leitura da requisição vira 422 validation_error; qualquer outra vira 500 internal_error, registrada no log e sem detalhes na resposta.</summary>
public sealed class ApiExceptionHandler(ILogger<ApiExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        ArgumentNullException.ThrowIfNull(exception);

        IResult result;
        if (exception is BadHttpRequestException)
        {
            result = ApiProblems.Unprocessable("validation_error", "Requisição inválida: corpo, cabeçalho ou parâmetro não pôde ser lido.");
        }
        else
        {
            Log.UnhandledException(logger, exception, httpContext.Request.Method, httpContext.Request.Path.Value ?? string.Empty);
            result = ApiProblems.Problem(StatusCodes.Status500InternalServerError, ApiProblems.InternalErrorCode, "Falha inesperada. Consulte o log do servidor.");
        }

        await result.ExecuteAsync(httpContext).ConfigureAwait(false);
        return true;
    }
}
