using System.Globalization;
using FinancialTransactions.Consolidation;
using FinancialTransactions.Consolidation.Api;
using Microsoft.Extensions.Logging;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace FinancialTransactions.IntegrationTests;

/// <summary>
/// ConsolidationFixture: dois bancos descartáveis por execução (<c>ft_test_&lt;hex&gt;</c> na origem e <c>ft_cons_test_&lt;hex&gt;</c> na consolidação),
/// o coordenador executado com conjunto vazio e o serviço levado a phase live antes dos testes (pré-condição de C1 a C8 e C10 a C15).
/// Os subcasos C9a a C9h usam um par de bancos próprio e não passam por esta fixture.
/// </summary>
public sealed class ConsolidationFixture : IAsyncLifetime
{
    private ConsolidationRig? _rig;

    public ConsolidationRig Rig => _rig ?? throw new InvalidOperationException("Fixture ainda não inicializada.");

    public async Task InitializeAsync()
    {
        _rig = await ConsolidationRig.CreateAsync();
        await _rig.BringToLiveAsync();
    }

    public async Task DisposeAsync()
    {
        if (_rig is not null)
        {
            await _rig.DisposeAsync();
            _rig = null;
        }
    }
}

[CollectionDefinition(Name)]
public sealed class ConsolidationCollectionDefinition : ICollectionFixture<ConsolidationFixture>
{
    public const string Name = "consolidation";
}

/// <summary>
/// Base dos cenários sobre o par compartilhado. A limpeza só ocorre com a parada do Worker comprovada (tarefa do laço concluída em até 10 s);
/// com o prazo esgotado não há nenhum DELETE, o par é marcado como contaminado e todo teste seguinte falha de imediato.
/// </summary>
[Collection(ConsolidationCollectionDefinition.Name)]
public abstract class ConsolidationTest(ConsolidationFixture fixture) : IAsyncLifetime
{
    private readonly List<WorkerHandle> _workers = [];
    private readonly List<ConsolidationApiHandle> _apis = [];
    private readonly HashSet<Guid> _accounts = [];

    protected ConsolidationRig Rig => fixture.Rig;

    protected static Guid CustomerA => ConsolidationRig.CustomerA;

    protected static Guid CustomerB => ConsolidationRig.CustomerB;

    protected static Guid A1 => ConsolidationRig.AccountA1;

    protected static Guid A2 => ConsolidationRig.AccountA2;

    protected static Guid B1 => ConsolidationRig.AccountB1;

    protected static DateOnly Today => BusinessDay.Today(TimeProvider.System);

    protected static string DayText(DateOnly day) => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>Meio-dia local de um dia de negócio: um instante seguro, longe das fronteiras do dia.</summary>
    protected static DateTimeOffset NoonOf(DateOnly day) => BusinessDay.StartUtc(day).AddHours(12);

    public Task InitializeAsync()
    {
        if (Rig.Contaminated is { } reason)
        {
            Assert.Fail($"Par de bancos contaminado por um teste anterior: {reason}");
        }

        return Task.CompletedTask;
    }

    protected void Use(params Guid[] accounts)
    {
        foreach (var account in accounts)
        {
            _accounts.Add(account);
        }
    }

    protected async Task<WorkerHandle> StartWorkerAsync(WorkerStart? start = null)
    {
        var worker = await Rig.StartWorkerAsync(start);
        _workers.Add(worker);
        return worker;
    }

    protected async Task<ConsolidationApiHandle> StartApiAsync(TestConnectionFactory? factory = null, IConsolidationApiFailurePoints? failurePoints = null, ILoggerProvider? logger = null, Action<ConsolidationApiOptions>? configureApi = null)
    {
        var api = await Rig.StartConsolidationApiAsync(factory, failurePoints, logger, configureApi);
        _apis.Add(api);
        return api;
    }

    protected static async Task<DailyDto> ReadAccountAsync(ConsolidationApiHandle api, Guid customer, Guid account, DateOnly day)
    {
        var (status, body) = await api.GetAccountAsync(customer, account, DayText(day));
        Assert.True(status == System.Net.HttpStatusCode.OK, $"GET esperado 200, obtido {(int)status}: {body}");
        return ConsolidationRig.Parse<DailyDto>(body);
    }

    public async Task DisposeAsync()
    {
        var stoppedAll = true;
        foreach (var worker in _workers)
        {
            stoppedAll &= await worker.StopAsync(TimeSpan.FromSeconds(10));
        }

        if (!stoppedAll)
        {
            // Trabalho possivelmente em voo: nenhum DELETE, resultado do teste preservado, recurso marcado.
            Rig.Contaminated = $"o laço do Worker não parou em 10 s ({GetType().Name})";
            return;
        }

        foreach (var worker in _workers)
        {
            await worker.DisposeAsync();
        }

        foreach (var api in _apis)
        {
            await api.DisposeAsync();
        }

        if (_accounts.Count > 0)
        {
            await Rig.CleanAccountsAsync(_accounts);
        }
    }
}
