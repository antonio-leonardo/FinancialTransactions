namespace FinancialTransactions.Domain;

/// <summary>Magnitude de um único movimento, em BRL com duas casas. Não representa saldo.</summary>
public sealed record MovementAmount
{
    public const string FixedCurrency = "BRL";
    public const decimal Maximum = 1_000_000_000m;

    public decimal Value { get; }
    public string Currency { get; } = FixedCurrency;

    private MovementAmount(decimal value) => Value = value;

    public static bool TryCreate(decimal value, out MovementAmount? amount, out string? error)
    {
        amount = null;
        error = null;
        if (value <= 0m) { error = "amount_must_be_positive"; return false; }
        if (decimal.Round(value, 2, MidpointRounding.ToEven) != value) { error = "amount_scale_exceeds_2"; return false; }
        if (value > Maximum) { error = "amount_exceeds_maximum"; return false; }
        amount = new MovementAmount(value);
        return true;
    }
}
