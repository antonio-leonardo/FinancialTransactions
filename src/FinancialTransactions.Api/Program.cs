using FinancialTransactions.Api.Auth;
using FinancialTransactions.Api.Endpoints;
using FinancialTransactions.Api.Http;
using FinancialTransactions.Api.Movements;
using FinancialTransactions.Infrastructure;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;

const string ConnectionStringName = "FinancialTransactions";

var builder = WebApplication.CreateBuilder(args);

// FT__Auth__SigningKey (e qualquer FT__Secao__Chave) entra na configuração sem o prefixo: Auth:SigningKey.
builder.Configuration.AddEnvironmentVariables("FT__");

// Configuração é lida de forma tardia (depois do Build): com o hosting mínimo, ajustes feitos pelo host de testes
// só entram na IConfiguration final no Build, e o mesmo vale para user-secrets e variáveis de ambiente.
builder.Services.AddFinancialTransactionsInfrastructure(provider =>
    provider.GetRequiredService<IConfiguration>().GetConnectionString(ConnectionStringName) ?? string.Empty);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<IRegisterMovementFailurePoints, NoFailurePoints>();
builder.Services.AddScoped<AccountAccess>();
builder.Services.AddScoped<RegisterMovementHandler>();

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer();
builder.Services
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
builder.Services.AddAuthorization();

builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<ApiExceptionHandler>();
// Falhas de binding (JSON ilegível, tipo errado) viram exceção e saem como 422 validation_error no ApiExceptionHandler.
builder.Services.Configure<RouteHandlerOptions>(options => options.ThrowOnBadRequest = true);

var app = builder.Build();

// Validação de startup depois de Build(): o design-time do EF (dotnet ef) para no Build e não precisa da chave;
// a aplicação real nunca chega a Run() sem chave de pelo menos 32 bytes (H11) nem sem connection string.
if (AuthDefaults.TryReadSigningKey(app.Configuration[AuthDefaults.SigningKeyConfigurationPath], out var signingKeyError) is null)
{
    throw new InvalidOperationException(signingKeyError);
}

if (string.IsNullOrWhiteSpace(app.Configuration.GetConnectionString(ConnectionStringName)))
{
    throw new InvalidOperationException($"ConnectionStrings:{ConnectionStringName} ausente. Use appsettings.Development.json, variável de ambiente ou user-secrets.");
}

app.UseExceptionHandler();
app.UseAuthentication();
app.UseAuthorization();

var api = app.MapGroup("/api/v1").RequireAuthorization();
api.MapMovementEndpoints();
api.MapPositionEndpoints();

app.Run();

public partial class Program { }
