using FinancialTransactions.Api.Movements;
using FinancialTransactions.Infrastructure.Configurations;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace FinancialTransactions.UnitTests;

public class IdempotencyConflictTests
{
    private static DbUpdateException Wrap(string sqlState, string constraintName)
    {
        var inner = new PostgresException(
            messageText: "duplicate key value violates unique constraint",
            severity: "ERROR",
            invariantSeverity: "ERROR",
            sqlState: sqlState,
            constraintName: constraintName);
        return new DbUpdateException("x", inner);
    }

    [Fact(DisplayName = "IsIdempotencyConflict_TrueOnlyForIdempotencyConstraint")]
    public void IsIdempotencyConflictTrueForIdempotencyConstraint()
    {
        var exception = Wrap(PostgresErrorCodes.UniqueViolation, MovementConfiguration.IdempotencyUniqueConstraintName);

        Assert.True(RegisterMovementHandler.IsIdempotencyConflict(exception));
    }

    [Fact(DisplayName = "IsIdempotencyConflict_FalseForPrimaryKeyViolation")]
    public void IsIdempotencyConflictFalseForPrimaryKeyViolation()
    {
        var exception = Wrap(PostgresErrorCodes.UniqueViolation, "pk_movements");

        Assert.False(RegisterMovementHandler.IsIdempotencyConflict(exception));
    }

    [Fact(DisplayName = "IsIdempotencyConflict_FalseForOtherSqlState")]
    public void IsIdempotencyConflictFalseForOtherSqlState()
    {
        var exception = Wrap(PostgresErrorCodes.ForeignKeyViolation, MovementConfiguration.IdempotencyUniqueConstraintName);

        Assert.False(RegisterMovementHandler.IsIdempotencyConflict(exception));
    }

    [Fact(DisplayName = "IsIdempotencyConflict_FalseWithoutInnerException")]
    public void IsIdempotencyConflictFalseWithoutInnerException()
    {
        Assert.False(RegisterMovementHandler.IsIdempotencyConflict(new DbUpdateException("x")));
    }
}
