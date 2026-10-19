using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using FinancialTransactions.Api.Movements;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace FinancialTransactions.IntegrationTests;

/// <summary>Variações de um token válido; cada propriedade fora do padrão invalida o token por um motivo.</summary>
public sealed record TokenVariation(string Audience = "financialtransactions-api", bool IncludeCustomerId = true, string? SigningKey = null);

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

    /// <summary>
    /// Factory derivada cujo ponto de falha do registro é o informado (só no host de teste; produção usa
    /// <c>NoFailurePoints</c>). O chamador descarta a factory devolvida.
    /// </summary>
    public WebApplicationFactory<Program> WithFailurePoints(IRegisterMovementFailurePoints failurePoints)
    {
        ArgumentNullException.ThrowIfNull(failurePoints);
        return WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IRegisterMovementFailurePoints>();
            services.AddSingleton(failurePoints);
        }));
    }

    /// <summary>Factory derivada que também entrega todos os logs ao provider informado. O chamador descarta a factory devolvida.</summary>
    public WebApplicationFactory<Program> WithLoggerProvider(ILoggerProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        return WithWebHostBuilder(builder => builder.ConfigureLogging(logging => logging.AddProvider(provider)));
    }

    /// <summary>Cliente autenticado como o cliente informado, a partir de qualquer factory derivada.</summary>
    public HttpClient AuthenticatedClient(WebApplicationFactory<Program> factory, Guid customerId)
    {
        ArgumentNullException.ThrowIfNull(factory);
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", CreateToken(customerId, TimeSpan.FromMinutes(10)));
        return client;
    }

    public string CreateToken(Guid customerId, TimeSpan lifetime)
    {
        return CreateToken(customerId, lifetime, new TokenVariation());
    }

    /// <summary>Token com uma variação deliberadamente inválida, para os cenários de autenticação (T11c).</summary>
    public string CreateToken(Guid customerId, TimeSpan lifetime, TokenVariation variation)
    {
        ArgumentNullException.ThrowIfNull(variation);
        var now = DateTime.UtcNow;
        var customer = customerId.ToString("D", CultureInfo.InvariantCulture);
        var claims = new Dictionary<string, object> { ["sub"] = customer };
        if (variation.IncludeCustomerId)
        {
            claims["customer_id"] = customer;
        }

        // Para lifetime negativo, a janela inteira fica no passado (Expires deve ser posterior a NotBefore).
        var start = lifetime < TimeSpan.Zero ? now.Add(lifetime).AddMinutes(-10) : now;
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = "financialtransactions-dev",
            Audience = variation.Audience,
            IssuedAt = start,
            NotBefore = start,
            Expires = now.Add(lifetime),
            Claims = claims,
            SigningCredentials = new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes(variation.SigningKey ?? signingKey)),
                SecurityAlgorithms.HmacSha256),
        };
        return new JsonWebTokenHandler().CreateToken(descriptor);
    }
}
