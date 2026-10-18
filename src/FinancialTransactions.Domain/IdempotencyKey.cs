using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace FinancialTransactions.Domain;

/// <summary>Validação da chave de idempotência e fingerprint canônico do conteúdo (H10).</summary>
public static class IdempotencyKey
{
    public const int MaxLength = 128;

    /// <summary>1 a 128 caracteres ASCII imprimíveis (0x20 a 0x7E), sem espaço em branco nas pontas.</summary>
    public static bool TryValidate(string? raw, [NotNullWhen(true)] out string? key, [NotNullWhen(false)] out string? error)
    {
        key = null;
        error = null;

        if (string.IsNullOrEmpty(raw))
        {
            error = "Idempotency-Key ausente.";
            return false;
        }

        if (raw.Length > MaxLength)
        {
            error = "Idempotency-Key deve ter no máximo 128 caracteres.";
            return false;
        }

        if (char.IsWhiteSpace(raw[0]) || char.IsWhiteSpace(raw[^1]))
        {
            error = "Idempotency-Key não pode começar nem terminar com espaço.";
            return false;
        }

        foreach (var c in raw)
        {
            if (c < 0x20 || c > 0x7E)
            {
                error = "Idempotency-Key deve conter apenas ASCII imprimível.";
                return false;
            }
        }

        key = raw;
        return true;
    }

    /// <summary>
    /// SHA-256, em hexadecimal minúsculo, de "{type}|{amount:F2}|{effectiveAt em UTC, formato O}|{description ou vazio}",
    /// com cultura invariante. O mesmo instante em offsets diferentes produz o mesmo fingerprint.
    /// </summary>
    public static string Fingerprint(MovementType type, MovementAmount amount, DateTimeOffset effectiveAt, string? description)
    {
        ArgumentNullException.ThrowIfNull(amount);
        var canonical = string.Create(
            CultureInfo.InvariantCulture,
            $"{type}|{amount.Value:F2}|{effectiveAt.ToUniversalTime():O}|{description ?? string.Empty}");
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }
}
