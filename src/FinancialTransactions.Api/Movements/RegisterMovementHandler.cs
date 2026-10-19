using FinancialTransactions.Api.Auth;
using FinancialTransactions.Api.Http;
using FinancialTransactions.Domain;
using System.Security.Cryptography;
using System.Text;
using FinancialTransactions.Infrastructure;
using FinancialTransactions.Infrastructure.Configurations;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace FinancialTransactions.Api.Movements;

/// <summary>
/// Caso de uso de registro (H10). Vive no projeto Api porque orquestra transporte (header, corpo), autorização
/// (claim) e persistência, e porque trata um detalhe do provider (PostgresException 23505) que não pertence ao Domain.
/// </summary>
public sealed class RegisterMovementHandler(
    FinancialTransactionsDbContext db,
    AccountAccess access,
    TimeProvider clock,
    IRegisterMovementFailurePoints failurePoints,
    ILogger<RegisterMovementHandler> logger)
{
    public const string IdempotencyKeyRequired = "idempotency_key_required";
    public const string IdempotencyKeyConflict = "idempotency_key_conflict";
    private const int MaxDescriptionLength = 140;

    public async Task<RegisterMovementResult> HandleAsync(
        Guid accountId,
        Guid customerId,
        string? idempotencyKeyHeader,
        RegisterMovementRequest? request,
        CancellationToken cancellationToken)
    {
        // 1. Header: identidade da intenção.
        if (!IdempotencyKey.TryValidate(idempotencyKeyHeader, out var key, out var keyError))
        {
            return RegisterMovementResult.Rejected(IdempotencyKeyRequired, keyError);
        }

        // 2. Corpo: tipo, valor (MovementAmount), instante e descrição.
        if (request is null)
        {
            return RegisterMovementResult.Rejected(MovementRules.ValidationError, "Corpo JSON obrigatório com type, amount e effectiveAt.");
        }

        MovementType type;
        switch (request.Type)
        {
            case nameof(MovementType.Credit):
                type = MovementType.Credit;
                break;
            case nameof(MovementType.Debit):
                type = MovementType.Debit;
                break;
            default:
                return RegisterMovementResult.Rejected(MovementRules.ValidationError, "type deve ser Credit ou Debit.");
        }

        if (request.Amount is not { } rawAmount)
        {
            return RegisterMovementResult.Rejected(MovementRules.ValidationError, "amount obrigatório.");
        }

        if (!MovementAmount.TryCreate(rawAmount, out var amount, out var amountError))
        {
            return RegisterMovementResult.Rejected(MovementRules.ValidationError, amountError);
        }

        // Validação lexical antes da conversão: texto sem offset não vira instante no fuso da máquina.
        if (!InstantParser.TryParseWithOffset(request.EffectiveAt, out var effectiveAt))
        {
            return RegisterMovementResult.Rejected(MovementRules.ValidationError, "effectiveAt deve ser ISO 8601 com offset explícito (ex.: 2026-09-01T09:00:00-03:00 ou 2026-09-01T12:00:00Z).");
        }

        var description = string.IsNullOrEmpty(request.Description) ? null : request.Description;
        if (description is { Length: > MaxDescriptionLength })
        {
            return RegisterMovementResult.Rejected(MovementRules.ValidationError, "description deve ter no máximo 140 caracteres.");
        }

        // 3. Conta: autorização por recurso e openedAt. Fora do escopo é 404, inclusive na repetição.
        var account = await access.FindAuthorizedAsync(accountId, customerId, cancellationToken).ConfigureAwait(false);
        if (account is null)
        {
            return RegisterMovementResult.NotFound();
        }

        // 4. Regras temporais (H5).
        var now = clock.GetUtcNow();
        if (MovementRules.ValidateEffectiveAt(effectiveAt, account.OpenedAt, now, MovementRules.DefaultTolerance) is { IsValid: false, Code: { } ruleCode })
        {
            return RegisterMovementResult.Rejected(ruleCode, ruleCode == MovementRules.EffectiveAtInFuture
                ? "effectiveAt além da tolerância de 5 minutos no futuro."
                : "effectiveAt anterior à abertura da conta.");
        }

        // 5. Fingerprint do conteúdo normalizado e entidade.
        var fingerprint = IdempotencyKey.Fingerprint(type, amount, effectiveAt, description);
        var movement = Movement.Create(account.Id, type, amount, effectiveAt, now, key, fingerprint, description);

        // 6. Inserção na mesma transação em que a identidade (chave + fingerprint) entra na linha.
        await using (var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false))
        {
            db.Movements.Add(movement);
            try
            {
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                await failurePoints.BeforeCommitAsync(cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                Log.MovementRegistered(logger, movement.Id, movement.AccountId);
                await failurePoints.AfterCommitAsync(cancellationToken).ConfigureAwait(false);
                return RegisterMovementResult.Created(movement);
            }
            catch (DbUpdateException ex) when (IsIdempotencyConflict(ex))
            {
                // 23505 exatamente na UNIQUE (account_id, idempotency_key): a transação PostgreSQL está abortada e
                // não aceita novas consultas. Reverter aqui; a releitura acontece fora dela, abaixo. Qualquer outra
                // violação única não é tratada como repetição e segue para o ApiExceptionHandler (500).
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        // Remove a entidade não persistida do rastreamento antes de qualquer nova consulta.
        db.ChangeTracker.Clear();

        var existing = await db.Movements.AsNoTracking()
            .SingleOrDefaultAsync(m => m.AccountId == account.Id && m.IdempotencyKey == key, cancellationToken)
            .ConfigureAwait(false);

        if (existing is null)
        {
            // Linha ausente após o conflito: estado inesperado, causa não determinada (por exemplo, exclusão
            // concorrente); respondido 409 sem repetir o insert. O log leva um prefixo do hash da chave, não a chave.
            Log.IdempotencyRereadMiss(logger, account.Id, KeyHashPrefix(key));
            return RegisterMovementResult.RetryLater();
        }

        return string.Equals(existing.RequestFingerprint, fingerprint, StringComparison.Ordinal)
            ? RegisterMovementResult.Replayed(existing)
            : RegisterMovementResult.Rejected(IdempotencyKeyConflict, "Idempotency-Key já usada nesta conta com conteúdo diferente.");
    }

    /// <summary>
    /// Verdadeiro só para a violação única (23505) da constraint de idempotência. Qualquer outra violação, de outra
    /// constraint ou de outro SqlState, não é repetição e segue como falha inesperada.
    /// </summary>
    public static bool IsIdempotencyConflict(DbUpdateException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return exception.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation,
            ConstraintName: MovementConfiguration.IdempotencyUniqueConstraintName,
        };
    }

    private static string KeyHashPrefix(string key)
    {
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..12];
    }
}
