using System.Globalization;

namespace FinancialTransactions.Api.Positions;

public sealed record AccountPositionResponse(Guid AccountId, DateTimeOffset AsOf, string Currency, decimal Balance, long MovementCount);

public sealed record CustomerAccountPositionResponse(Guid AccountId, decimal Balance, long MovementCount);

public sealed record ExcludedAccountResponse(Guid AccountId, DateTimeOffset OpenedAt);

public sealed record CustomerPositionResponse(
    Guid CustomerId,
    DateTimeOffset AsOf,
    string Currency,
    decimal Balance,
    IReadOnlyList<CustomerAccountPositionResponse> Accounts,
    IReadOnlyList<ExcludedAccountResponse> ExcludedAccounts);

/// <summary>Leitura de <c>asOf</c> da query string. Sem offset explícito, assume UTC. Ausente ou ilegível é inválido.</summary>
public static class AsOfParser
{
    public const string HistoricoInsuficiente = "historico_insuficiente";

    public static bool TryParse(string? raw, out DateTimeOffset asOf)
    {
        asOf = default;
        return !string.IsNullOrWhiteSpace(raw)
            && DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out asOf);
    }
}
