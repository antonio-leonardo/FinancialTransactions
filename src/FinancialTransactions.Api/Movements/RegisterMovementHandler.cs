using FinancialTransactions.Api.Auth;
using FinancialTransactions.Api.Http;
using FinancialTransactions.Domain;
using FinancialTransactions.Infrastructure;
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

        if (request.EffectiveAt is not { } effectiveAt)
        {
            return RegisterMovementResult.Rejected(MovementRules.ValidationError, "effectiveAt obrigatório, em ISO 8601 com offset.");
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
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return RegisterMovementResult.Created(movement);
            }
            catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
            {
                // 23505 em ux_movements_account_id_idempotency_key: a transação PostgreSQL está abortada e
                // não aceita novas consultas. Reverter aqui; a releitura acontece fora dela, abaixo.
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
            // Janela rara: a outra transação que detinha a chave foi revertida depois do nosso conflito.
            // Sem repetir o insert em loop; o cliente repete a mesma requisição.
            Log.IdempotencyRereadMiss(logger, account.Id, key);
            return RegisterMovementResult.RetryLater();
        }

        return string.Equals(existing.RequestFingerprint, fingerprint, StringComparison.Ordinal)
            ? RegisterMovementResult.Replayed(existing)
            : RegisterMovementResult.Rejected(IdempotencyKeyConflict, "Idempotency-Key já usada nesta conta com conteúdo diferente.");
    }
}
