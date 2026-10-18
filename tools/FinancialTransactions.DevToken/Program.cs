using System.Globalization;
using System.Text;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

// Emissor local de desenvolvimento. Mesmos issuer/audience da API (H11). Sem chave padrão embutida:
// sem chave, sai com código 2. Uso:
//   dotnet run --project tools/FinancialTransactions.DevToken -- --customer <guid> [--minutes 60] [--key <valor>]
const string Issuer = "financialtransactions-dev";
const string Audience = "financialtransactions-api";
const int MinimumKeyBytes = 32;

string? key = Environment.GetEnvironmentVariable("FT__Auth__SigningKey");
string? customer = null;
int minutes = 60;

for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--key" when i + 1 < args.Length:
            key = args[++i];
            break;
        case "--customer" when i + 1 < args.Length:
            customer = args[++i];
            break;
        case "--minutes" when i + 1 < args.Length:
            if (!int.TryParse(args[++i], NumberStyles.Integer, CultureInfo.InvariantCulture, out minutes) || minutes <= 0)
            {
                Console.Error.WriteLine("--minutes deve ser um inteiro positivo.");
                return 1;
            }

            break;
        default:
            Console.Error.WriteLine($"Argumento não reconhecido: {args[i]}");
            Console.Error.WriteLine("Uso: --customer <guid> [--minutes <n>] [--key <valor>] (ou FT__Auth__SigningKey no ambiente)");
            return 1;
    }
}

if (customer is null || !Guid.TryParse(customer, out var customerId) || customerId == Guid.Empty)
{
    Console.Error.WriteLine("--customer <guid> é obrigatório.");
    return 1;
}

if (string.IsNullOrWhiteSpace(key))
{
    Console.Error.WriteLine("Chave ausente: informe --key ou defina FT__Auth__SigningKey.");
    return 2;
}

var keyBytes = Encoding.UTF8.GetBytes(key);
if (keyBytes.Length < MinimumKeyBytes)
{
    Console.Error.WriteLine($"Chave curta demais: {keyBytes.Length} bytes; mínimo {MinimumKeyBytes}.");
    return 2;
}

var now = DateTime.UtcNow;
var customerText = customerId.ToString("D", CultureInfo.InvariantCulture);
var descriptor = new SecurityTokenDescriptor
{
    Issuer = Issuer,
    Audience = Audience,
    IssuedAt = now,
    NotBefore = now,
    Expires = now.AddMinutes(minutes),
    Claims = new Dictionary<string, object>
    {
        ["sub"] = customerText,
        ["customer_id"] = customerText,
    },
    SigningCredentials = new SigningCredentials(new SymmetricSecurityKey(keyBytes), SecurityAlgorithms.HmacSha256),
};

Console.WriteLine(new JsonWebTokenHandler().CreateToken(descriptor));
return 0;
