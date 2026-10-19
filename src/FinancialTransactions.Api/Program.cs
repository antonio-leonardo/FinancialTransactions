using FinancialTransactions.Api.Auth;
using FinancialTransactions.Auth;
using FinancialTransactions.Api.Endpoints;
using FinancialTransactions.Api.Health;
using FinancialTransactions.Api.Http;
using FinancialTransactions.Api.Movements;
using FinancialTransactions.Infrastructure;

const string ConnectionStringName = DatabaseReadinessCheck.ConnectionStringName;

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

builder.Services.AddFinancialTransactionsJwt();

builder.Services.AddHealthChecks()
    .AddCheck<DatabaseReadinessCheck>("db", tags: [HealthEndpoints.ReadyTag]);

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
app.UseApiRequestBodyLimit();
app.UseAuthentication();
app.UseAuthorization();

// Sondas anônimas, fora do grupo autenticado abaixo.
app.MapHealthEndpoints();

var api = app.MapGroup("/api/v1").RequireAuthorization();
api.MapMovementEndpoints();
api.MapPositionEndpoints();

app.Run();

public partial class Program { }
