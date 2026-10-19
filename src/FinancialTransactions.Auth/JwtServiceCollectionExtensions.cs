using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;

namespace FinancialTransactions.Auth;

public static class JwtServiceCollectionExtensions
{
    /// <summary>
    /// Autenticação JWT bearer HS256 do emissor local de desenvolvimento (H11) e autorização por padrão.
    /// A chave é lida de forma tardia, quando as opções são resolvidas: ajustes do host de testes, user-secrets e variáveis
    /// de ambiente só entram na <see cref="IConfiguration"/> final depois do Build.
    /// </summary>
    public static IServiceCollection AddFinancialTransactionsJwt(this IServiceCollection services)
    {
        services
            .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer();
        services
            .AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<IConfiguration>((options, configuration) =>
            {
                var signingKey = AuthDefaults.TryReadSigningKey(configuration[AuthDefaults.SigningKeyConfigurationPath], out _);
                options.MapInboundClaims = false;
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = AuthDefaults.Issuer,
                    ValidateAudience = true,
                    ValidAudience = AuthDefaults.Audience,
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = signingKey is null ? null : new SymmetricSecurityKey(signingKey),
                    ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
                    ValidateLifetime = true,
                    RequireExpirationTime = true,
                    ClockSkew = TimeSpan.FromMinutes(1),
                    NameClaimType = "sub",
                };
                options.Events = new JwtBearerEvents
                {
                    // Token válido mas sem escopo de cliente utilizável é tratado como não autenticado (401).
                    OnTokenValidated = context =>
                    {
                        if (!CustomerScope.TryGet(context.Principal, out _))
                        {
                            context.Fail("Token sem claim customer_id válida.");
                        }

                        return Task.CompletedTask;
                    },
                };
            });
        services.AddAuthorization();
        return services;
    }
}
