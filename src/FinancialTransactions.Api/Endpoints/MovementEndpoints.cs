using System.Security.Claims;
using FinancialTransactions.Api.Auth;
using FinancialTransactions.Auth;
using FinancialTransactions.Api.Http;
using FinancialTransactions.Api.Movements;
using FinancialTransactions.Infrastructure;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace FinancialTransactions.Api.Endpoints;

public static class MovementEndpoints
{
    public static RouteGroupBuilder MapMovementEndpoints(this RouteGroupBuilder group)
    {
        ArgumentNullException.ThrowIfNull(group);

        group.MapPost("/accounts/{accountId:guid}/movements", RegisterAsync).RequireAuthorization();
        group.MapGet("/accounts/{accountId:guid}/movements/{movementId:guid}", GetAsync).RequireAuthorization();
        return group;
    }

    private static async Task<IResult> RegisterAsync(
        Guid accountId,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        RegisterMovementRequest? request,
        ClaimsPrincipal user,
        RegisterMovementHandler handler,
        CancellationToken cancellationToken)
    {
        var customerId = CustomerScope.Require(user);
        var result = await handler.HandleAsync(accountId, customerId, idempotencyKey, request, cancellationToken).ConfigureAwait(false);

        return result switch
        {
            { Outcome: RegisterMovementOutcome.Created, Movement: { } created } =>
                Results.Created(MovementResponse.LocationFor(created.AccountId, created.Id), MovementResponse.From(created)),
            { Outcome: RegisterMovementOutcome.Replayed, Movement: { } replayed } =>
                Results.Ok(MovementResponse.From(replayed)),
            { Outcome: RegisterMovementOutcome.NotFound } => ApiProblems.NotFound(),
            { Outcome: RegisterMovementOutcome.Rejected, Code: { } code, Detail: { } detail } => ApiProblems.Unprocessable(code, detail),
            { Outcome: RegisterMovementOutcome.RetryLater, Code: { } code, Detail: { } detail } =>
                ApiProblems.Problem(StatusCodes.Status409Conflict, code, detail),
            _ => throw new InvalidOperationException("Resultado de RegisterMovement sem mapeamento HTTP."),
        };
    }

    private static async Task<IResult> GetAsync(
        Guid accountId,
        Guid movementId,
        ClaimsPrincipal user,
        AccountAccess access,
        FinancialTransactionsDbContext db,
        CancellationToken cancellationToken)
    {
        var customerId = CustomerScope.Require(user);
        var account = await access.FindAuthorizedAsync(accountId, customerId, cancellationToken).ConfigureAwait(false);
        if (account is null)
        {
            return ApiProblems.NotFound();
        }

        var movement = await db.Movements.AsNoTracking()
            .SingleOrDefaultAsync(m => m.Id == movementId && m.AccountId == account.Id, cancellationToken)
            .ConfigureAwait(false);

        return movement is null ? ApiProblems.NotFound() : Results.Ok(MovementResponse.From(movement));
    }
}
