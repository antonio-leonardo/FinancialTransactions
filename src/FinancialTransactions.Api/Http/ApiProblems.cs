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

    public static IResult NotFound() => Problem(StatusCodes.Status404NotFound, NotFoundCode, "Recurso não encontrado.");

    public static IResult Unprocessable(string code, string detail) => Problem(StatusCodes.Status422UnprocessableEntity, code, detail);
}
