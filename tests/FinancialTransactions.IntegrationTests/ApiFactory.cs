using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace FinancialTransactions.IntegrationTests;

/// <summary>Sobe a API real contra o banco de teste, com chave de assinatura gerada por execução.</summary>
public sealed class ApiFactory(string connectionString, string signingKey) : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:FinancialTransactions"] = connectionString,
            ["Auth:SigningKey"] = signingKey,
        }));
    }

    /// <summary>Cliente HTTP já autenticado como o cliente informado.</summary>
    public HttpClient CreateClient(Guid customerId)
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", CreateToken(customerId, TimeSpan.FromMinutes(10)));
        return client;
    }

    public string CreateToken(Guid customerId, TimeSpan lifetime)
    {
        var now = DateTime.UtcNow;
        var customer = customerId.ToString("D", CultureInfo.InvariantCulture);
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = "financialtransactions-dev",
            Audience = "financialtransactions-api",
            IssuedAt = now,
            NotBefore = now,
            Expires = now.Add(lifetime),
            Claims = new Dictionary<string, object> { ["sub"] = customer, ["customer_id"] = customer },
            SigningCredentials = new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(signingKey)), SecurityAlgorithms.HmacSha256),
        };
        return new JsonWebTokenHandler().CreateToken(descriptor);
    }
}
