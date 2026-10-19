using System.Text.RegularExpressions;
using System.Xml.Linq;
using FinancialTransactions.Consolidation;
using FinancialTransactions.Consolidation.Api;
using FinancialTransactions.Consolidation.Worker;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FinancialTransactions.IntegrationTests;

/// <summary>
/// I1 e I4: a composição de produção não contém pontos de falha, fábrica nem sink de teste; o grafo de projetos não tem ciclo e a
/// <c>Auth</c> não referencia ninguém; a configuração inválida impede o início; o modo coordenador não tem Kestrel nem consumidor.
/// </summary>
public sealed class ConsolidationCompositionTests
{
    private const string Origin = "Host=127.0.0.1;Port=15432;Database=composicao_origem;Username=u;Password=p";
    private const string Projection = "Host=127.0.0.1;Port=15433;Database=composicao_projecao;Username=u;Password=p";

    private static Dictionary<string, string?> ValidConfiguration() => new()
    {
        ["ConnectionStrings:FinancialTransactions"] = Origin,
        ["ConnectionStrings:Consolidation"] = Projection,
        ["Auth:SigningKey"] = new string('k', 48),
    };

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "FinancialTransactions.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Raiz da solução não encontrada.");
    }

    [Fact(DisplayName = "Composition_ProductionWorker_HasNoTestFailurePointsFactoryOrSink")]
    public async Task CompositionProductionWorkerHasNoTestFailurePointsFactoryOrSink()
    {
        await using var app = WorkerApp.CreateService([], builder =>
        {
            builder.Configuration.AddInMemoryCollection(ValidConfiguration());
            builder.Logging.ClearProviders();
        });

        var services = app.Services;
        Assert.IsType<NoConsolidationFailurePoints>(services.GetRequiredService<IConsolidationFailurePoints>());
        Assert.IsType<NoBootstrapFailurePoints>(services.GetRequiredService<IBootstrapFailurePoints>());
        Assert.IsType<ConfigurationConsolidationConnectionFactory>(services.GetRequiredService<IConsolidationConnectionFactory>());
        Assert.IsType<LoggingConsolidationDiagnostics>(services.GetRequiredService<IConsolidationDiagnostics>());
        Assert.Single(services.GetServices<IHostedService>().OfType<ConsolidationConsumer>());
        AssertNoTestTypes(typeof(WorkerApp).Assembly, typeof(ConsolidationConsumer).Assembly);
    }

    [Fact(DisplayName = "Composition_ProductionConsolidationApi_HasNoTestFailurePointsOrFactory")]
    public async Task CompositionProductionConsolidationApiHasNoTestFailurePointsOrFactory()
    {
        await using var app = ConsolidationApiApp.Create([], builder =>
        {
            builder.Configuration.AddInMemoryCollection(ValidConfiguration());
            builder.Logging.ClearProviders();
        });

        Assert.IsType<NoConsolidationApiFailurePoints>(app.Services.GetRequiredService<IConsolidationApiFailurePoints>());
        Assert.IsType<ConfigurationConsolidationConnectionFactory>(app.Services.GetRequiredService<IConsolidationConnectionFactory>());
        Assert.Equal(TimeSpan.FromMilliseconds(1500), app.Services.GetRequiredService<IOptions<ConsolidationApiOptions>>().Value.RequestBudget);
        AssertNoTestTypes(typeof(ConsolidationApiApp).Assembly);
    }

    [Fact(DisplayName = "Composition_ApiConnectionStrings_CarryTheApiBudget_WorkerKeepsItsOwn")]
    public async Task CompositionApiConnectionStringsCarryTheApiBudgetWorkerKeepsItsOwn()
    {
        await using var api = ConsolidationApiApp.Create([], builder => builder.Configuration.AddInMemoryCollection(ValidConfiguration()));
        await using var worker = WorkerApp.CreateService([], builder => builder.Configuration.AddInMemoryCollection(ValidConfiguration()));

        var apiStrings = api.Services.GetRequiredService<IConsolidationConnectionFactory>().Open(ConsolidationConnectionRole.Query);
        var workerStrings = worker.Services.GetRequiredService<IConsolidationConnectionFactory>().Open(ConsolidationConnectionRole.Consumer);

        foreach (var text in new[] { apiStrings.Origin, apiStrings.Projection })
        {
            Assert.Contains("Timeout=1", text, StringComparison.Ordinal);
            Assert.Contains("Command Timeout=1", text, StringComparison.Ordinal);
        }

        foreach (var text in new[] { workerStrings.Origin, workerStrings.Projection })
        {
            Assert.Contains("Timeout=5", text, StringComparison.Ordinal);
            Assert.Contains("Command Timeout=15", text, StringComparison.Ordinal);
        }
    }

    private static void AssertNoTestTypes(params System.Reflection.Assembly[] assemblies)
    {
        foreach (var assembly in assemblies)
        {
            Assert.DoesNotContain(assembly.GetTypes(), t => t.Name.StartsWith("Test", StringComparison.Ordinal) || t.Name.StartsWith("Scripted", StringComparison.Ordinal));
        }
    }

    [Fact(DisplayName = "Config_WorkerWithoutConnectionStrings_RefusesToStart_WithAClearMessage")]
    public async Task ConfigWorkerWithoutConnectionStringsRefusesToStartWithAClearMessage()
    {
        await using var app = WorkerApp.CreateService([], builder =>
        {
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Logging.ClearProviders();
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:FinancialTransactions"] = string.Empty,
                ["ConnectionStrings:Consolidation"] = "isto não é uma connection string",
            });
        });

        var exception = await Assert.ThrowsAsync<OptionsValidationException>(() => app.StartAsync());

        Assert.Contains(exception.Failures, f => f.Contains("ConnectionStrings:FinancialTransactions ausente", StringComparison.Ordinal));
        Assert.Contains(exception.Failures, f => f.Contains("ConnectionStrings:Consolidation não é uma connection string válida", StringComparison.Ordinal));
        Assert.DoesNotContain(exception.Failures, f => f.Contains("isto não é", StringComparison.Ordinal)); // nunca ecoa o valor
    }

    [Fact(DisplayName = "Config_ConsolidationApiWithoutSigningKey_RefusesToStart")]
    public void ConfigConsolidationApiWithoutSigningKeyRefusesToStart()
    {
        var exception = Assert.Throws<InvalidOperationException>(() => ConsolidationApiApp.Create([], builder =>
        {
            var configuration = ValidConfiguration();
            configuration.Remove("Auth:SigningKey");
            builder.Configuration.AddInMemoryCollection(configuration);
        }));

        Assert.Contains("Auth:SigningKey", exception.Message, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "BackfillMode_InvalidConfiguration_ExitsWithCode2")]
    public async Task BackfillModeInvalidConfigurationExitsWithCode2()
    {
        var exit = await WorkerApp.RunBackfillAsync([], builder =>
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:FinancialTransactions"] = string.Empty }));

        Assert.Equal(CoordinatorExitCodes.Unreachable, exit);
    }

    [Fact(DisplayName = "BackfillMode_HasNoConsumerAndNoWebServer_AndUnreachableDatabasesExitWith2")]
    public async Task BackfillModeHasNoConsumerAndNoWebServerAndUnreachableDatabasesExitWith2()
    {
        IServiceCollection? services = null;
        var exit = await WorkerApp.RunBackfillAsync([], builder =>
        {
            services = builder.Services;
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:FinancialTransactions"] = "Host=127.0.0.1;Port=1;Database=a;Username=u;Password=p;Timeout=1",
                ["ConnectionStrings:Consolidation"] = "Host=127.0.0.1;Port=1;Database=b;Username=u;Password=p;Timeout=1",
            });
        });

        Assert.Equal(CoordinatorExitCodes.Unreachable, exit);
        Assert.NotNull(services);
        Assert.DoesNotContain(services, d => d.ImplementationType == typeof(ConsolidationConsumer));
        Assert.DoesNotContain(services, d => d.ServiceType.FullName == "Microsoft.AspNetCore.Hosting.Server.IServer");
    }

    [Fact(DisplayName = "ProjectGraph_AuthReferencesNothing_NoCycle_ApisDoNotReferenceEachOther")]
    public void ProjectGraphAuthReferencesNothingNoCycleApisDoNotReferenceEachOther()
    {
        var root = RepositoryRoot();
        var graph = Directory.GetFiles(Path.Combine(root, "src"), "*.csproj", SearchOption.AllDirectories)
            .ToDictionary(
                path => Path.GetFileNameWithoutExtension(path),
                path => XDocument.Load(path).Descendants("ProjectReference")
                    .Select(r => Path.GetFileNameWithoutExtension(((string?)r.Attribute("Include") ?? string.Empty).Replace('\\', '/')))
                    .ToList());

        Assert.Empty(graph["FinancialTransactions.Auth"]);
        Assert.Contains("FinancialTransactions.Auth", graph["FinancialTransactions.Api"]);
        Assert.Contains("FinancialTransactions.Auth", graph["FinancialTransactions.Consolidation.Api"]);
        Assert.DoesNotContain(graph["FinancialTransactions.Api"], n => n.Contains("Consolidation", StringComparison.Ordinal));
        Assert.DoesNotContain("FinancialTransactions.Api", graph["FinancialTransactions.Consolidation.Api"]);
        Assert.DoesNotContain("FinancialTransactions.Api", graph["FinancialTransactions.Consolidation.Worker"]);
        Assert.DoesNotContain("FinancialTransactions.Api", graph["FinancialTransactions.Consolidation"]);

        // Sem ciclo: busca em profundidade com marcação de caminho.
        var onPath = new HashSet<string>();
        var done = new HashSet<string>();
        foreach (var node in graph.Keys)
        {
            Visit(node);
        }

        void Visit(string node)
        {
            if (done.Contains(node) || !graph.TryGetValue(node, out var edges))
            {
                return;
            }

            Assert.True(onPath.Add(node), $"ciclo de projetos em {node}");
            foreach (var next in edges)
            {
                Visit(next);
            }

            onPath.Remove(node);
            done.Add(node);
        }
    }

    [Fact(DisplayName = "SourceHygiene_NoWarningSuppressionNoNullForgivingOperator_NoPostRetry")]
    public void SourceHygieneNoWarningSuppressionNoNullForgivingOperatorNoPostRetry()
    {
        var root = RepositoryRoot();
        var sources = Directory.GetFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !p.Contains($"{Path.DirectorySeparatorChar}Migrations{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .ToList();
        Assert.NotEmpty(sources);

        // `!` aplicado a um identificador, a um fecho de parênteses ou de colchetes, seguido de ponto, ponto e vírgula, vírgula ou fecho.
        var nullForgiving = new Regex(@"[\w\)\]]!(?=[\.\;\,\)\s]|$)(?!=)", RegexOptions.Compiled, TimeSpan.FromSeconds(5));
        foreach (var path in sources)
        {
            var lines = File.ReadAllLines(path);
            for (var i = 0; i < lines.Length; i++)
            {
                var code = lines[i].Split("//", 2)[0];
                var inString = code.Count(c => c == '"') % 2 == 1;
                Assert.DoesNotContain("#pragma warning disable", code, StringComparison.Ordinal);
                Assert.DoesNotContain("SuppressMessage", code, StringComparison.Ordinal);
                if (!inString && nullForgiving.IsMatch(code) && !code.Contains("!=", StringComparison.Ordinal))
                {
                    Assert.Fail($"operador de supressão de nulidade em {Path.GetRelativePath(root, path)}:{i + 1}: {lines[i].Trim()}");
                }
            }
        }

        foreach (var project in Directory.GetFiles(Path.Combine(root, "src"), "*.csproj", SearchOption.AllDirectories))
        {
            Assert.DoesNotContain("<NoWarn>", File.ReadAllText(project), StringComparison.Ordinal);
        }

        // Nenhuma política de nova tentativa no caminho de POST.
        Assert.DoesNotContain(sources, p => File.ReadAllText(p).Contains("Polly", StringComparison.Ordinal));
    }
}
