using FinancialTransactions.Domain;

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

/// <summary>
/// Leitura de <c>asOf</c> da query string: data e hora completas, com offset (Z ou ±HH:mm) ou sem offset (UTC).
/// Só data, só hora, formato regional ou texto são inválidos. Delegada ao parser estrito do domínio.
/// </summary>
public static class AsOfParser
{
    public const string HistoricoInsuficiente = "historico_insuficiente";

    public static bool TryParse(string? raw, out DateTimeOffset asOf)
    {
        return InstantParser.TryParseUtcOrOffset(raw, out asOf);
    }
}
