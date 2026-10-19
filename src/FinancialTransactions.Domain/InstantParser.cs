using System.Globalization;

namespace FinancialTransactions.Domain;

/// <summary>
/// Leitura estrita de instantes vindos da API (ISO 8601, cultura invariante, lista fechada de formatos).
/// A validação é lexical: só o que casa com um formato vira DateTimeOffset. Nada é completado nem assumido
/// a partir do relógio ou do fuso da máquina.
/// </summary>
public static class InstantParser
{
    private static readonly string[] WithOffsetFormats =
    [
        "yyyy-MM-dd'T'HH:mm:ss'Z'",
        "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'",
        "yyyy-MM-dd'T'HH:mm:sszzz",
        "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFzzz",
    ];

    private static readonly string[] UtcOrOffsetFormats =
    [
        "yyyy-MM-dd'T'HH:mm:ss'Z'",
        "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'",
        "yyyy-MM-dd'T'HH:mm:sszzz",
        "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFzzz",
        "yyyy-MM-dd'T'HH:mm:ss",
        "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF",
    ];

    /// <summary>Data e hora completas com offset explícito (Z ou ±HH:mm). Usado em effectiveAt (POST).</summary>
    public static bool TryParseWithOffset(string? raw, out DateTimeOffset value)
    {
        return TryParseExact(raw, WithOffsetFormats, out value);
    }

    /// <summary>Os formatos com offset mais data e hora completas sem offset, lidas como UTC. Usado em asOf (GET).</summary>
    public static bool TryParseUtcOrOffset(string? raw, out DateTimeOffset value)
    {
        return TryParseExact(raw, UtcOrOffsetFormats, out value);
    }

    private static bool TryParseExact(string? raw, string[] formats, out DateTimeOffset value)
    {
        value = default;
        if (string.IsNullOrEmpty(raw) || raw.Trim().Length != raw.Length)
        {
            return false;
        }

        // AssumeUniversal é o único estilo: faz o 'Z' literal e a ausência de offset resultarem em offset zero,
        // sem permitir espaços nem componentes ausentes.
        return DateTimeOffset.TryParseExact(raw, formats, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out value);
    }
}
