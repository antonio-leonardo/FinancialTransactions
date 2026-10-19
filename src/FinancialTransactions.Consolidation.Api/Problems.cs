namespace FinancialTransactions.Consolidation.Api;

/// <summary>ProblemDetails com a extensão <c>code</c>. Nunca inclui exceção, stack, connection string ou token.</summary>
public static class Problems
{
    public const string NotFoundCode = "not_found";
    public const string ValidationErrorCode = "validation_error";
    public const string InsufficientHistoryCode = "historico_insuficiente";
    public const string DependencyUnavailableCode = "dependency_unavailable";
    public const string NotReadyCode = "consolidation_not_ready";
    public const string InternalErrorCode = "internal_error";

    public static IResult Problem(int statusCode, string code, string detail) =>
        Results.Problem(statusCode: statusCode, detail: detail, extensions: new Dictionary<string, object?> { ["code"] = code });

    public static IResult NotFound() => Problem(StatusCodes.Status404NotFound, NotFoundCode, "Recurso não encontrado.");

    public static IResult Validation(string detail) => Problem(StatusCodes.Status422UnprocessableEntity, ValidationErrorCode, detail);

    public static IResult InsufficientHistory(string detail) => Problem(StatusCodes.Status422UnprocessableEntity, InsufficientHistoryCode, detail);

    public static IResult DependencyUnavailable() =>
        Problem(StatusCodes.Status503ServiceUnavailable, DependencyUnavailableCode, "Dependência indisponível; tente novamente.");

    public static IResult NotReady() =>
        Problem(StatusCodes.Status503ServiceUnavailable, NotReadyCode, "A consolidação inicial ainda não terminou; tente novamente.");

    public static IResult Internal() =>
        Problem(StatusCodes.Status500InternalServerError, InternalErrorCode, "Falha inesperada. Consulte o log do servidor.");
}

internal static partial class Log
{
    [LoggerMessage(EventId = 7000, Level = LogLevel.Error, Message = "Falha inesperada ao processar {Method} {Path}.")]
    public static partial void UnhandledException(ILogger logger, Exception exception, string method, string path);

    [LoggerMessage(EventId = 7001, Level = LogLevel.Warning, Message = "Dependência indisponível ou orçamento da requisição estourado em {Method} {Path} ({FailureType}); respondido 503.")]
    public static partial void DependencyUnavailable(ILogger logger, string method, string path, string failureType);

    [LoggerMessage(EventId = 7002, Level = LogLevel.Information, Message = "Requisição abandonada pelo cliente em {Method} {Path}.")]
    public static partial void RequestAbandoned(ILogger logger, string method, string path);

    [LoggerMessage(EventId = 7003, Level = LogLevel.Warning, Message = "Leitura diagnóstica da origem falhou ({FailureType}); freshness unknown.")]
    public static partial void DiagnosticReadFailed(ILogger logger, string failureType);
}
