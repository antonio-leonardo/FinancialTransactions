using System.Globalization;
using FinancialTransactions.Domain;

namespace FinancialTransactions.Api.Movements;

/// <summary>
/// Corpo de POST /accounts/{accountId}/movements. Tudo opcional no transporte; a validação devolve 422 com código.
/// effectiveAt chega como texto e só vira instante no InstantParser, para que a ausência de offset seja detectável.
/// </summary>
public sealed record RegisterMovementRequest(string? Type, decimal? Amount, string? EffectiveAt, string? Description);

/// <summary>Representação pública de um movimento. Sem chave de idempotência nem fingerprint. Instantes em UTC.</summary>
public sealed record MovementResponse(
    Guid MovementId,
    Guid AccountId,
    string Type,
    decimal Amount,
    string Currency,
    DateTimeOffset EffectiveAt,
    DateTimeOffset RegisteredAt)
{
    public static MovementResponse From(Movement movement)
    {
        ArgumentNullException.ThrowIfNull(movement);
        return new MovementResponse(
            movement.Id,
            movement.AccountId,
            movement.Type.ToString(),
            movement.Amount.Value,
            movement.Currency,
            movement.EffectiveAt.ToUniversalTime(),
            movement.RegisteredAt.ToUniversalTime());
    }

    public static string LocationFor(Guid accountId, Guid movementId)
    {
        return string.Create(CultureInfo.InvariantCulture, $"/api/v1/accounts/{accountId}/movements/{movementId}");
    }
}

public enum RegisterMovementOutcome
{
    Created,
    Replayed,
    NotFound,
    Rejected,
    RetryLater,
}

/// <summary>Resultado do caso de uso, sem HTTP: o endpoint traduz para status e ProblemDetails.</summary>
public sealed record RegisterMovementResult(RegisterMovementOutcome Outcome, Movement? Movement, string? Code, string? Detail)
{
    public static RegisterMovementResult Created(Movement movement) => new(RegisterMovementOutcome.Created, movement, null, null);

    public static RegisterMovementResult Replayed(Movement movement) => new(RegisterMovementOutcome.Replayed, movement, null, null);

    public static RegisterMovementResult NotFound() => new(RegisterMovementOutcome.NotFound, null, null, null);

    public static RegisterMovementResult Rejected(string code, string detail) => new(RegisterMovementOutcome.Rejected, null, code, detail);

    public static RegisterMovementResult RetryLater() => new(RegisterMovementOutcome.RetryLater, null, "retry_later", "Registro concorrente ainda não visível; repita a mesma requisição.");
}
