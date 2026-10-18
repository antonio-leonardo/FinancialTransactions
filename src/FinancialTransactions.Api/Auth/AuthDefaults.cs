using System.Text;

namespace FinancialTransactions.Api.Auth;

/// <summary>Emissor local de desenvolvimento (H11). Issuer e audience são públicos; só a chave é segredo.</summary>
public static class AuthDefaults
{
    public const string Issuer = "financialtransactions-dev";
    public const string Audience = "financialtransactions-api";
    public const string SigningKeyConfigurationPath = "Auth:SigningKey";
    public const int MinimumKeyBytes = 32;

    /// <summary>Bytes UTF-8 do valor configurado. Nulo quando ausente ou curto demais; o motivo vai em <paramref name="error"/>.</summary>
    public static byte[]? TryReadSigningKey(string? configuredValue, out string? error)
    {
        if (string.IsNullOrWhiteSpace(configuredValue))
        {
            error = $"{SigningKeyConfigurationPath} ausente. Defina FT__Auth__SigningKey ou um user-secret; nunca em appsettings.";
            return null;
        }

        var bytes = Encoding.UTF8.GetBytes(configuredValue);
        if (bytes.Length < MinimumKeyBytes)
        {
            error = $"{SigningKeyConfigurationPath} precisa de pelo menos {MinimumKeyBytes} bytes; recebidos {bytes.Length}.";
            return null;
        }

        error = null;
        return bytes;
    }
}
