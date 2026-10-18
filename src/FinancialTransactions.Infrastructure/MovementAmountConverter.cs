using FinancialTransactions.Domain;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace FinancialTransactions.Infrastructure;

/// <summary>MovementAmount.Value para numeric(18,2) e de volta. A moeda vai em coluna própria.</summary>
public sealed class MovementAmountConverter : ValueConverter<MovementAmount, decimal>
{
    public MovementAmountConverter()
        : base(amount => amount.Value, value => Rehydrate(value))
    {
    }

    // CHECK (amount > 0) e numeric(18,2) tornam a falha impossível em dados íntegros; se acontecer, é corrupção, não entrada do usuário.
    private static MovementAmount Rehydrate(decimal value)
    {
        return MovementAmount.TryCreate(value, out var amount, out var error)
            ? amount
            : throw new InvalidOperationException($"Valor persistido inválido para MovementAmount: {error}.");
    }
}
