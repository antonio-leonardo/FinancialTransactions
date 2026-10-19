using System.Security.Claims;
using FinancialTransactions.Api.Auth;
using FinancialTransactions.Auth;
using FinancialTransactions.Api.Http;
using FinancialTransactions.Api.Positions;
using FinancialTransactions.Domain;
using FinancialTransactions.Infrastructure;

namespace FinancialTransactions.Api.Endpoints;

public static class PositionEndpoints
{
    private const string AsOfInvalidDetail = "asOf obrigatório: data e hora completas em ISO 8601, com offset (ex.: 2026-09-01T10:30:00-03:00, 2026-09-01T13:30:00Z) ou sem offset, lido como UTC (2026-09-01T13:30:00). Só data, só hora ou formato regional são inválidos.";
    private const string AsOfTooFarDetail = "asOf além da tolerância de 5 minutos no futuro.";

    public static RouteGroupBuilder MapPositionEndpoints(this RouteGroupBuilder group)
    {
        ArgumentNullException.ThrowIfNull(group);

        group.MapGet("/accounts/{accountId:guid}/position", GetAccountPositionAsync).RequireAuthorization();
        group.MapGet("/customers/{customerId:guid}/position", GetCustomerPositionAsync).RequireAuthorization();
        return group;
    }

    private static async Task<IResult> GetAccountPositionAsync(
        Guid accountId,
        string? asOf,
        ClaimsPrincipal user,
        AccountAccess access,
        PositionQueries queries,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        var customerId = CustomerScope.Require(user);
        var account = await access.FindAuthorizedAsync(accountId, customerId, cancellationToken).ConfigureAwait(false);
        if (account is null)
        {
            return ApiProblems.NotFound();
        }

        if (!AsOfParser.TryParse(asOf, out var asOfValue))
        {
            return ApiProblems.Unprocessable(MovementRules.ValidationError, AsOfInvalidDetail);
        }

        // H8.
        if (MovementRules.ValidateAsOf(asOfValue, clock.GetUtcNow(), MovementRules.DefaultTolerance) is { IsValid: false, Code: { } code })
        {
            return ApiProblems.Unprocessable(code, AsOfTooFarDetail);
        }

        // H6: antes da abertura não há história, e a resposta não pode ser zero.
        if (!account.IsOpenAt(asOfValue))
        {
            return ApiProblems.Unprocessable(AsOfParser.HistoricoInsuficiente, "asOf anterior à abertura da conta.");
        }

        var aggregate = await queries.GetAccountPositionAsync(account.Id, asOfValue, cancellationToken).ConfigureAwait(false);

        // Origem confirmada acima (asOf >= openedAt): só por isso SUM vazio (nulo) significa saldo zero (H6).
        var balance = aggregate.Sum ?? 0m;

        return Results.Ok(new AccountPositionResponse(account.Id, asOfValue.ToUniversalTime(), account.Currency, balance, aggregate.MovementCount));
    }

    private static async Task<IResult> GetCustomerPositionAsync(
        Guid customerId,
        string? asOf,
        ClaimsPrincipal user,
        PositionQueries queries,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        // O cliente da rota precisa ser o cliente do token; qualquer outro é 404, sem revelar existência.
        if (customerId != CustomerScope.Require(user))
        {
            return ApiProblems.NotFound();
        }

        if (!AsOfParser.TryParse(asOf, out var asOfValue))
        {
            return ApiProblems.Unprocessable(MovementRules.ValidationError, AsOfInvalidDetail);
        }

        if (MovementRules.ValidateAsOf(asOfValue, clock.GetUtcNow(), MovementRules.DefaultTolerance) is { IsValid: false, Code: { } code })
        {
            return ApiProblems.Unprocessable(code, AsOfTooFarDetail);
        }

        var aggregate = await queries.GetCustomerPositionAsync(customerId, asOfValue, cancellationToken).ConfigureAwait(false);
        if (aggregate is null)
        {
            return ApiProblems.NotFound();
        }

        // H7: nenhuma conta aberta em asOf significa história insuficiente, não zero.
        if (aggregate.Accounts.Count == 0)
        {
            return ApiProblems.Unprocessable(AsOfParser.HistoricoInsuficiente, "Nenhuma conta do cliente estava aberta em asOf.");
        }

        // Soma de agregados por conta já calculados no banco (uma parcela por conta), não de movimentos.
        var balance = aggregate.Accounts.Sum(a => a.Balance);

        return Results.Ok(new CustomerPositionResponse(
            customerId,
            asOfValue.ToUniversalTime(),
            MovementAmount.FixedCurrency,
            balance,
            aggregate.Accounts.Select(a => new CustomerAccountPositionResponse(a.AccountId, a.Balance, a.MovementCount)).ToList(),
            aggregate.ExcludedAccounts.Select(a => new ExcludedAccountResponse(a.Id, a.OpenedAt.ToUniversalTime())).ToList()));
    }
}
