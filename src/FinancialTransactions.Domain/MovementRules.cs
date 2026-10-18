namespace FinancialTransactions.Domain;

/// <summary>Resultado tipado de uma regra: válido, ou inválido com o código que a API devolve em <c>code</c>.</summary>
public readonly record struct RuleResult(bool IsValid, string? Code)
{
    public static RuleResult Ok => new(true, null);

    public static RuleResult Fail(string code)
    {
        ArgumentException.ThrowIfNullOrEmpty(code);
        return new RuleResult(false, code);
    }
}

/// <summary>Regras puras sobre tempo e sinal (H4, H5, H8). Sem dependência de banco ou relógio.</summary>
public static class MovementRules
{
    public const string EffectiveAtInFuture = "effective_at_in_future";
    public const string EffectiveAtBeforeOpening = "effective_at_before_opening";
    public const string ValidationError = "validation_error";

    /// <summary>Tolerância para relógios dessincronizados entre cliente e servidor (H5/H8).</summary>
    public static readonly TimeSpan DefaultTolerance = TimeSpan.FromMinutes(5);

    /// <summary>H5: effectiveAt acima de now + tolerance é futuro; abaixo de openedAt antecede a conta; igual a openedAt é aceito.</summary>
    public static RuleResult ValidateEffectiveAt(DateTimeOffset effectiveAt, DateTimeOffset openedAt, DateTimeOffset now, TimeSpan tolerance)
    {
        if (effectiveAt > now + tolerance)
        {
            return RuleResult.Fail(EffectiveAtInFuture);
        }

        if (effectiveAt < openedAt)
        {
            return RuleResult.Fail(EffectiveAtBeforeOpening);
        }

        return RuleResult.Ok;
    }

    /// <summary>H8: asOf acima de now + tolerance é rejeitado como erro de validação.</summary>
    public static RuleResult ValidateAsOf(DateTimeOffset asOf, DateTimeOffset now, TimeSpan tolerance)
    {
        return asOf > now + tolerance ? RuleResult.Fail(ValidationError) : RuleResult.Ok;
    }

    /// <summary>
    /// Único ponto de conversão de sinal no código C#: crédito soma, débito subtrai. A agregação no
    /// banco (PositionQueries) expressa a mesma convenção em SQL, por exigência de agregar no servidor.
    /// </summary>
    public static decimal SignedAmount(MovementType type, MovementAmount amount)
    {
        ArgumentNullException.ThrowIfNull(amount);
        return type switch
        {
            MovementType.Credit => amount.Value,
            MovementType.Debit => -amount.Value,
            _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Tipo de movimento desconhecido."),
        };
    }
}
