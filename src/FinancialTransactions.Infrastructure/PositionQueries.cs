using FinancialTransactions.Domain;
using Microsoft.EntityFrameworkCore;

namespace FinancialTransactions.Infrastructure;

/// <summary>Agregado de uma conta em um corte. <see cref="Sum"/> é nulo quando não há movimento no corte: só vira zero depois que o chamador confirma a origem (H6).</summary>
public sealed record AccountAggregate(decimal? Sum, long MovementCount);

/// <summary>Linha do GROUP BY por conta. Um grupo tem ao menos um movimento, logo <see cref="Sum"/> nunca é nulo.</summary>
public sealed record CustomerAccountAggregate(Guid AccountId, decimal Sum, long MovementCount);

/// <summary>Posição de uma conta incluída na consolidação: origem já confirmada por <c>opened_at &lt;= asOf</c> (H7).</summary>
public sealed record AccountPosition(Guid AccountId, decimal Balance, long MovementCount);

/// <summary>Resultado da consolidação por cliente: contas abertas em <c>asOf</c> com posição, e contas abertas depois, excluídas (H7).</summary>
public sealed record CustomerAggregate(IReadOnlyList<AccountPosition> Accounts, IReadOnlyList<Account> ExcludedAccounts);

/// <summary>
/// Posição calculada no banco por SUM/COUNT (H12). SQL parametrizado: o sinal do movimento entra como fragmento
/// constante (<see cref="SignedAmountSql"/>) e os valores (conta, corte) entram como parâmetros Npgsql posicionais
/// (<c>{0}</c>, <c>{1}</c>), nunca como texto. Nenhum movimento é materializado em memória.
/// </summary>
public sealed class PositionQueries(FinancialTransactionsDbContext db)
{
    /// <summary>Fragmento constante e único do sinal em SQL (crédito positivo, débito negativo); espelha <c>MovementRules.SignedAmount</c>.</summary>
    private const string SignedAmountSql = "CASE m.type WHEN 1 THEN m.amount ELSE -m.amount END";

    private const string AccountAggregateSql = $$"""
        SELECT SUM({{SignedAmountSql}}) AS "Sum",
               COUNT(*) AS "MovementCount"
        FROM movements AS m
        WHERE m.account_id = {0} AND m.effective_at <= {1}
        """;

    private const string CustomerAggregateSql = $$"""
        SELECT m.account_id AS "AccountId",
               SUM({{SignedAmountSql}}) AS "Sum",
               COUNT(*) AS "MovementCount"
        FROM movements AS m
        WHERE m.account_id = ANY({0}) AND m.effective_at <= {1}
        GROUP BY m.account_id
        """;

    /// <summary>Consulta composável exposta para prova de tradução (ToQueryString) e para a posição por conta.</summary>
    public IQueryable<AccountAggregate> AccountAggregateQuery(Guid accountId, DateTimeOffset asOf)
    {
        // timestamptz exige DateTimeOffset com offset zero no parâmetro.
        var asOfUtc = asOf.ToUniversalTime();
        return db.Database.SqlQueryRaw<AccountAggregate>(AccountAggregateSql, accountId, asOfUtc);
    }

    /// <summary>SUM e COUNT de uma conta até <paramref name="asOf"/> (fronteira inclusiva, H4). Sempre uma linha.</summary>
    public async Task<AccountAggregate> GetAccountPositionAsync(Guid accountId, DateTimeOffset asOf, CancellationToken cancellationToken)
    {
        return await AccountAggregateQuery(accountId, asOf).SingleAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Carrega as contas do cliente, separa as abertas em <paramref name="asOf"/> das abertas depois e agrega as
    /// incluídas em uma única consulta com GROUP BY account_id. Devolve nulo se o cliente não existir.
    /// </summary>
    public async Task<CustomerAggregate?> GetCustomerPositionAsync(Guid customerId, DateTimeOffset asOf, CancellationToken cancellationToken)
    {
        var customerExists = await db.Customers.AsNoTracking().AnyAsync(c => c.Id == customerId, cancellationToken).ConfigureAwait(false);
        if (!customerExists)
        {
            return null;
        }

        var accounts = await db.Accounts.AsNoTracking()
            .Where(a => a.CustomerId == customerId)
            .OrderBy(a => a.OpenedAt).ThenBy(a => a.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var included = accounts.Where(a => a.IsOpenAt(asOf)).ToList();
        var excluded = accounts.Where(a => !a.IsOpenAt(asOf)).ToList();
        if (included.Count == 0)
        {
            return new CustomerAggregate([], excluded);
        }

        var includedIds = included.Select(a => a.Id).ToArray();
        var asOfUtc = asOf.ToUniversalTime();
        var rows = await db.Database.SqlQueryRaw<CustomerAccountAggregate>(CustomerAggregateSql, includedIds, asOfUtc)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        var byAccount = rows.ToDictionary(r => r.AccountId);
        var positions = included
            .Select(a => byAccount.TryGetValue(a.Id, out var row)
                ? new AccountPosition(a.Id, row.Sum, row.MovementCount)
                // Conta aberta em asOf e sem movimento no corte: origem confirmada, saldo zero (H6/H7).
                : new AccountPosition(a.Id, 0m, 0))
            .ToList();

        return new CustomerAggregate(positions, excluded);
    }
}
