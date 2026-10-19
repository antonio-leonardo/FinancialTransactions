using System.Globalization;
using FinancialTransactions.Infrastructure;
using Microsoft.Extensions.Options;
using Npgsql;

namespace FinancialTransactions.Consolidation.Api;

/// <summary>Conta e titular lidos na origem: a autorização do consolidado depende da origem (declarado na arquitetura).</summary>
public sealed record OwnedAccount(Guid Id, Guid CustomerId, DateTimeOffset OpenedAt);

public sealed class AccountOwnership(IConsolidationConnectionFactory connections)
{
    public async Task<OwnedAccount?> FindAsync(Guid accountId, CancellationToken cancellationToken)
    {
        await using var connection = await BootstrapStore.OpenAsync(connections.Open(ConsolidationConnectionRole.Query).Origin, cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand("SELECT id, customer_id, opened_at FROM accounts WHERE id = @id", connection);
        command.Parameters.AddWithValue("id", accountId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new OwnedAccount(reader.GetGuid(0), reader.GetGuid(1), reader.GetFieldValue<DateTimeOffset>(2))
            : null;
    }

    public async Task<IReadOnlyList<OwnedAccount>> ListByCustomerAsync(Guid customerId, CancellationToken cancellationToken)
    {
        await using var connection = await BootstrapStore.OpenAsync(connections.Open(ConsolidationConnectionRole.Query).Origin, cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand("SELECT id, customer_id, opened_at FROM accounts WHERE customer_id = @id ORDER BY id", connection);
        command.Parameters.AddWithValue("id", customerId);
        var accounts = new List<OwnedAccount>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            accounts.Add(new OwnedAccount(reader.GetGuid(0), reader.GetGuid(1), reader.GetFieldValue<DateTimeOffset>(2)));
        }

        return accounts;
    }
}

public sealed record AccountDailyResponse(
    Guid AccountId,
    DateOnly Day,
    string BusinessTimeZone,
    string DayStatus,
    string Currency,
    decimal Credits,
    decimal Debits,
    decimal Net,
    long MovementCount,
    decimal ClosingBalance,
    long? MaxAppliedSequence,
    DateTimeOffset? ObservedAt,
    long? UnresolvedEvents,
    long? FailedEvents,
    bool? Stale,
    string Freshness);

public sealed record CustomerAccountDailyResponse(
    Guid AccountId,
    decimal Credits,
    decimal Debits,
    decimal Net,
    long MovementCount,
    decimal ClosingBalance,
    long? MaxAppliedSequence);

public sealed record ExcludedAccountResponse(Guid AccountId, DateTimeOffset OpenedAt);

public sealed record CustomerDailyResponse(
    Guid CustomerId,
    DateOnly Day,
    string BusinessTimeZone,
    string DayStatus,
    string Currency,
    decimal Credits,
    decimal Debits,
    decimal Net,
    long MovementCount,
    decimal ClosingBalance,
    long? MaxAppliedSequence,
    DateTimeOffset? ObservedAt,
    long? UnresolvedEvents,
    long? FailedEvents,
    bool? Stale,
    string Freshness,
    IReadOnlyList<CustomerAccountDailyResponse> Accounts,
    IReadOnlyList<ExcludedAccountResponse> ExcludedAccounts);

/// <summary>
/// Consulta do consolidado diário. Ordem obrigatória: autorização e leitura diagnóstica na origem primeiro; só depois a consulta única à
/// projeção, aberta em conexão nova. Por isso, tudo o que o Worker confirmou antes da abertura da consulta à projeção aparece nos valores,
/// enquanto <c>unresolvedEvents</c>, <c>failedEvents</c> e <c>freshness</c> descrevem o instante <c>observedAt</c> da leitura na origem.
/// </summary>
public sealed class DailyConsolidationService(
    AccountOwnership ownership,
    UnresolvedEventsQuery unresolvedQuery,
    ConsolidationQueries queries,
    IConsolidationApiFailurePoints failurePoints,
    IOptions<ConsolidationApiOptions> options,
    TimeProvider clock,
    ILogger<DailyConsolidationService> logger)
{
    private const string Currency = "BRL";
    private const string DayFormat = "yyyy-MM-dd";

    public Task<IResult> GetAccountAsync(HttpContext http, Guid accountId, Guid customerId, string? dayText) =>
        ExecuteWithBudgetAsync(http, ct => AccountAsync(accountId, customerId, dayText, ct));

    public Task<IResult> GetCustomerAsync(HttpContext http, Guid routeCustomerId, Guid tokenCustomerId, string? dayText) =>
        ExecuteWithBudgetAsync(http, ct => CustomerAsync(routeCustomerId, tokenCustomerId, dayText, ct));

    private async Task<IResult> ExecuteWithBudgetAsync(HttpContext http, Func<CancellationToken, Task<IResult>> action)
    {
        // Orçamento por requisição encadeado ao RequestAborted: o abandono do cliente e o estouro do orçamento são coisas diferentes.
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(http.RequestAborted);
        budget.CancelAfter(options.Value.RequestBudget);
        try
        {
            return await action(budget.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (http.RequestAborted.IsCancellationRequested)
        {
            Log.RequestAbandoned(logger, http.Request.Method, http.Request.Path.Value ?? string.Empty);
            return Results.StatusCode(499);
        }
        catch (Exception ex) when (ex is OperationCanceledException || DependencyFailure.IsUnavailable(ex))
        {
            Log.DependencyUnavailable(logger, http.Request.Method, http.Request.Path.Value ?? string.Empty, ex.GetType().Name);
            return Problems.DependencyUnavailable();
        }
    }

    private async Task<IResult> AccountAsync(Guid accountId, Guid customerId, string? dayText, CancellationToken ct)
    {
        var (day, invalid) = ParseDay(dayText);
        if (invalid is not null)
        {
            return invalid;
        }

        // Fora do escopo do token é 404, sem revelar existência.
        var account = await ownership.FindAsync(accountId, ct).ConfigureAwait(false);
        if (account is null || account.CustomerId != customerId)
        {
            return Problems.NotFound();
        }

        if (account.OpenedAt >= BusinessDay.EndExclusiveUtc(day))
        {
            return Problems.InsufficientHistory("A conta foi aberta depois do fim do dia consultado.");
        }

        var observation = await ObserveAsync([account.Id], day, ct).ConfigureAwait(false);
        if (observation is not { Snapshot: { } snapshot })
        {
            return observation.Failure ?? Problems.NotReady();
        }

        var row = snapshot.Accounts.Single();
        var (freshness, stale) = Freshness(observation.Unresolved);
        return Results.Ok(new AccountDailyResponse(
            account.Id,
            day,
            BusinessDay.TimeZoneId,
            DayStatus(day),
            Currency,
            row.Credits,
            row.Debits,
            row.Net,
            row.MovementCount,
            row.ClosingBalance,
            row.MaxAppliedSequence,
            observation.Unresolved?.ObservedAt,
            observation.Unresolved?.Unresolved,
            observation.Unresolved?.Failed,
            stale,
            freshness));
    }

    private async Task<IResult> CustomerAsync(Guid routeCustomerId, Guid tokenCustomerId, string? dayText, CancellationToken ct)
    {
        var (day, invalid) = ParseDay(dayText);
        if (invalid is not null)
        {
            return invalid;
        }

        if (routeCustomerId != tokenCustomerId)
        {
            return Problems.NotFound();
        }

        var accounts = await ownership.ListByCustomerAsync(routeCustomerId, ct).ConfigureAwait(false);
        if (accounts.Count == 0)
        {
            return Problems.NotFound();
        }

        var end = BusinessDay.EndExclusiveUtc(day);
        var included = accounts.Where(a => a.OpenedAt < end).ToList();
        var excluded = accounts.Where(a => a.OpenedAt >= end).ToList();
        if (included.Count == 0)
        {
            return Problems.InsufficientHistory("Nenhuma conta do cliente estava aberta no fim do dia consultado.");
        }

        var observation = await ObserveAsync(included.ConvertAll(a => a.Id), day, ct).ConfigureAwait(false);
        if (observation is not { Snapshot: { } snapshot })
        {
            return observation.Failure ?? Problems.NotReady();
        }

        var rows = snapshot.Accounts;
        var totals = ConsolidationTotals.Of(rows);
        var (freshness, stale) = Freshness(observation.Unresolved);
        return Results.Ok(new CustomerDailyResponse(
            routeCustomerId,
            day,
            BusinessDay.TimeZoneId,
            DayStatus(day),
            Currency,
            totals.Credits,
            totals.Debits,
            totals.Net,
            totals.MovementCount,
            totals.ClosingBalance,
            totals.MaxAppliedSequence,
            observation.Unresolved?.ObservedAt,
            observation.Unresolved?.Unresolved,
            observation.Unresolved?.Failed,
            stale,
            freshness,
            rows.Select(r => new CustomerAccountDailyResponse(r.AccountId, r.Credits, r.Debits, r.Net, r.MovementCount, r.ClosingBalance, r.MaxAppliedSequence)).ToList(),
            excluded.Select(a => new ExcludedAccountResponse(a.Id, a.OpenedAt.ToUniversalTime())).ToList()));
    }

    private sealed record Observation(UnresolvedEvents? Unresolved, ProjectionSnapshot? Snapshot, IResult? Failure);

    private async Task<Observation> ObserveAsync(IReadOnlyList<Guid> accountIds, DateOnly day, CancellationToken ct)
    {
        // 1. Origem primeiro. Se só a leitura diagnóstica falha (a autorização já foi obtida), segue para a projeção: freshness unknown.
        UnresolvedEvents? unresolved = null;
        try
        {
            unresolved = await unresolvedQuery.ReadAsync(accountIds, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException && DependencyFailure.IsUnavailable(ex))
        {
            Log.DiagnosticReadFailed(logger, ex.GetType().Name);
        }

        await failurePoints.BetweenOriginAndProjectionAsync(ct).ConfigureAwait(false);

        // 2. Só agora a consulta única à projeção, em conexão nova, READ COMMITTED.
        var snapshot = await queries.ReadAsync(accountIds, day, ct).ConfigureAwait(false);
        return snapshot.Phase == ConsolidationBootstrapState.Live
            ? new Observation(unresolved, snapshot, null)
            : new Observation(null, null, Problems.NotReady());
    }

    private static (string Freshness, bool? Stale) Freshness(UnresolvedEvents? unresolved) =>
        unresolved switch
        {
            null => ("unknown", null),
            { Unresolved: 0 } => ("complete", false),
            _ => ("stale", true),
        };

    private string DayStatus(DateOnly day) => day == BusinessDay.Today(clock) ? "open" : "closed";

    private (DateOnly Day, IResult? Problem) ParseDay(string? text)
    {
        if (!DateOnly.TryParseExact(text, DayFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
        {
            return (default, Problems.Validation("day obrigatório no formato YYYY-MM-DD."));
        }

        return day > BusinessDay.Today(clock) ? (day, Problems.Validation("day não pode ser futuro.")) : (day, null);
    }
}
