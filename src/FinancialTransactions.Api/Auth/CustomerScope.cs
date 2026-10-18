using System.Security.Claims;

namespace FinancialTransactions.Api.Auth;

/// <summary>Escopo de acesso vem da claim <c>customer_id</c> do token, nunca do corpo ou da query (H11).</summary>
public static class CustomerScope
{
    public const string ClaimType = "customer_id";

    public static bool TryGet(ClaimsPrincipal? principal, out Guid customerId)
    {
        customerId = Guid.Empty;
        var value = principal?.FindFirst(ClaimType)?.Value;
        return value is not null && Guid.TryParse(value, out customerId) && customerId != Guid.Empty;
    }

    /// <summary>Depois de autenticado, a claim existe: a validação do token falha sem ela (ver Program.cs).</summary>
    public static Guid Require(ClaimsPrincipal principal)
    {
        return TryGet(principal, out var customerId)
            ? customerId
            : throw new InvalidOperationException("Principal autenticado sem claim customer_id válida.");
    }
}
