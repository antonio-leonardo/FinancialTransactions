namespace FinancialTransactions.Api.Http;

/// <summary>ProblemDetails com a extensão <c>code</c>. Nunca inclui exceção, stack, connection string ou token.</summary>
public static class ApiProblems
{
    public const string NotFoundCode = "not_found";
    public const string InternalErrorCode = "internal_error";

    public static IResult Problem(int statusCode, string code, string detail)
    {
        return Results.Problem(
            statusCode: statusCode,
            detail: detail,
            extensions: new Dictionary<string, object?> { ["code"] = code });
    }

    public const string DependencyUnavailableCode = "dependency_unavailable";
    public const string PayloadTooLargeCode = "payload_too_large";

    public static IResult DependencyUnavailable() =>
        Problem(StatusCodes.Status503ServiceUnavailable, DependencyUnavailableCode, "Dependência indisponível; tente novamente.");

    public static IResult PayloadTooLarge() =>
        Problem(StatusCodes.Status413PayloadTooLarge, PayloadTooLargeCode, "Corpo da requisição acima do limite de 16 KB.");

    public static IResult NotFound() => Problem(StatusCodes.Status404NotFound, NotFoundCode, "Recurso não encontrado.");

    public static IResult Unprocessable(string code, string detail) => Problem(StatusCodes.Status422UnprocessableEntity, code, detail);
}
