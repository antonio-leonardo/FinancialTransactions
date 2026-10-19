using System.Globalization;
using System.Text;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace FinancialTransactions.LoadTest;

/// <summary>
/// População sintética criada por seed_load.sql: 200 clientes e 400 contas (2 por cliente), ids derivados do número.
/// A ferramenta não consulta o banco para saber quem existe; a reconciliação é que confere o resultado.
/// </summary>
internal static class Population
{
    public const int CustomerCount = 200;
    public const int AccountCount = 400;

    /// <summary>Intervalo de effectiveAt e asOf: o mesmo do histórico pré-carregado e posterior à abertura (2026-01-01Z).</summary>
    public static readonly DateTimeOffset RangeStart = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    public static readonly DateTimeOffset RangeEnd = new(2026, 9, 30, 23, 59, 59, TimeSpan.Zero);

    public static Guid CustomerId(int customerNumber)
    {
        return Guid.Parse(string.Create(CultureInfo.InvariantCulture, $"cccccccc-0000-0000-0000-{customerNumber:x12}"));
    }

    public static Guid AccountId(int accountNumber)
    {
        return Guid.Parse(string.Create(CultureInfo.InvariantCulture, $"dddddddd-0000-0000-0000-{accountNumber:x12}"));
    }

    /// <summary>Contas 2k-1 e 2k pertencem ao cliente k.</summary>
    public static int CustomerOf(int accountNumber)
    {
        return (accountNumber + 1) / 2;
    }

    /// <summary>Instante aleatório em segundos inteiros dentro do intervalo, para que o corpo do replay seja idêntico ao original.</summary>
    public static DateTimeOffset RandomInstant(Random random)
    {
        var seconds = (long)(RangeEnd - RangeStart).TotalSeconds;
        return RangeStart.AddSeconds(random.NextInt64(0, seconds + 1));
    }
}

/// <summary>Tokens JWT por cliente, emitidos com a chave da API (FT__Auth__SigningKey). Ficam só em memória.</summary>
internal sealed class TokenBook
{
    private const string Issuer = "financialtransactions-dev";
    private const string Audience = "financialtransactions-api";
    private const int MinimumKeyBytes = 32;

    private readonly string[] _tokens;

    private TokenBook(string[] tokens)
    {
        _tokens = tokens;
    }

    public static bool TryCreate(string? key, out TokenBook? book, out string error)
    {
        book = null;
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(key))
        {
            error = "FT__Auth__SigningKey ausente no ambiente.";
            return false;
        }

        var keyBytes = Encoding.UTF8.GetBytes(key);
        if (keyBytes.Length < MinimumKeyBytes)
        {
            error = $"FT__Auth__SigningKey curta demais: mínimo {MinimumKeyBytes} bytes.";
            return false;
        }

        var handler = new JsonWebTokenHandler();
        var credentials = new SigningCredentials(new SymmetricSecurityKey(keyBytes), SecurityAlgorithms.HmacSha256);
        var now = DateTime.UtcNow;
        var tokens = new string[Population.CustomerCount + 1];
        for (var customer = 1; customer <= Population.CustomerCount; customer++)
        {
            var id = Population.CustomerId(customer).ToString("D", CultureInfo.InvariantCulture);
            tokens[customer] = handler.CreateToken(new SecurityTokenDescriptor
            {
                Issuer = Issuer,
                Audience = Audience,
                IssuedAt = now,
                NotBefore = now,
                Expires = now.AddMinutes(30),
                Claims = new Dictionary<string, object> { ["sub"] = id, ["customer_id"] = id },
                SigningCredentials = credentials,
            });
        }

        book = new TokenBook(tokens);
        return true;
    }

    public string For(int customerNumber)
    {
        return _tokens[customerNumber];
    }
}
