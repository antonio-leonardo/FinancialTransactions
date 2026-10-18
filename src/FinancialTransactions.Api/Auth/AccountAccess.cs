using FinancialTransactions.Domain;
using FinancialTransactions.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace FinancialTransactions.Api.Auth;

/// <summary>Autorização por recurso: a conta precisa existir e pertencer ao cliente do token. Qualquer outro caso é 404, sem revelar existência.</summary>
public sealed class AccountAccess(FinancialTransactionsDbContext db)
{
    public async Task<Account?> FindAuthorizedAsync(Guid accountId, Guid customerId, CancellationToken cancellationToken)
    {
        var account = await db.Accounts.AsNoTracking()
            .SingleOrDefaultAsync(a => a.Id == accountId, cancellationToken)
            .ConfigureAwait(false);

        return account is not null && account.CustomerId == customerId ? account : null;
    }
}
