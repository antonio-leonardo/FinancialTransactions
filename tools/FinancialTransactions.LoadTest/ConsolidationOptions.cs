using System.Globalization;
using System.Text.Json;
using Npgsql;

namespace FinancialTransactions.LoadTest;

internal enum RunMode
{
    /// <summary>Laço fechado do P04 (histórico, anterior à Outbox). É o padrão sem <c>--mode</c> para preservar o uso antigo.</summary>
    ClosedLoopHistorical,

    /// <summary>Chegadas abertas contra o consolidado (P08, parte C).</summary>
    ConsolidationOpen,

    /// <summary>Escreve a massa reproduzível (movimento e Outbox na mesma transação) e a valida por cálculo independente.</summary>
    SeedConsolidation,
}

/// <summary>Conteúdo mínimo do manifesto próprio do ensaio, gravado ANTES da execução pelo orquestrador.</summary>
internal sealed record RunManifest(string RunId, string OriginDatabase, string ProjectionDatabase, int ExpectedSeedMovements);

/// <summary>
/// Argumentos do modo consolidado. Recusa tudo que não seja loopback, URL com credencial, banco que não corresponda ao manifesto
/// próprio e pasta de saída que já exista.
/// </summary>
internal sealed record ConsolidationOptions(
    RunMode Mode,
    string Scenario,
    double ArrivalRate,
    int DurationSeconds,
    int WarmupSeconds,
    IReadOnlyList<Uri> ReadUrls,
    Uri? WriteUrl,
    double WriteRate,
    int MaxInFlightGet,
    int MaxInFlightPost,
    TimeSpan GetTransportTimeout,
    TimeSpan PostTransportTimeout,
    TimeSpan UsefulDeadline,
    TimeSpan EmissionTolerance,
    TimeSpan ConvergenceTimeout,
    int Seed,
    int Repetition,
    string OutDirectory,
    string? StartSignalFile,
    string OriginConnection,
    string ProjectionConnection,
    RunManifest Manifest,
    int FinalApiChecks)
{
    public const string OriginConnectionVariable = "FT_LOAD_ORIGIN_CONNECTION";
    public const string ProjectionConnectionVariable = "FT_LOAD_PROJECTION_CONNECTION";
    public const string DatabasePrefix = "ft_load_p08_";

    public static bool TryParse(string[] args, out ConsolidationOptions? options, out string error)
    {
        ArgumentNullException.ThrowIfNull(args);
        options = null;
        error = string.Empty;

        var mode = RunMode.ConsolidationOpen;
        var scenario = "L1";
        var rate = 50.0;
        var duration = 60;
        var warmup = 10;
        var readUrls = new List<string>();
        string? writeUrl = null;
        var writeRate = 10.0;
        var maxGet = 128;
        var maxPost = 64;
        var getTimeout = TimeSpan.FromSeconds(2);
        var postTimeout = TimeSpan.FromSeconds(5);
        var deadline = TimeSpan.FromMilliseconds(1000);
        var tolerance = TimeSpan.FromMilliseconds(100);
        var convergence = TimeSpan.FromSeconds(120);
        var seed = 20261020;
        var repetition = 1;
        string? outDirectory = null;
        string? startSignal = null;
        var origin = Environment.GetEnvironmentVariable(OriginConnectionVariable);
        var projection = Environment.GetEnvironmentVariable(ProjectionConnectionVariable);
        string? manifestPath = null;
        var finalChecks = 100;

        for (var i = 0; i < args.Length; i++)
        {
            if (i + 1 >= args.Length)
            {
                error = $"Argumento sem valor: {args[i]}";
                return false;
            }

            var value = args[i + 1];
            switch (args[i])
            {
                case "--mode":
                    if (value == "seed-consolidation")
                    {
                        mode = RunMode.SeedConsolidation;
                    }
                    else if (value != "consolidation-open")
                    {
                        error = "--mode deve ser consolidation-open ou seed-consolidation.";
                        return false;
                    }

                    break;
                case "--scenario":
                    scenario = value;
                    break;
                case "--arrival-rate":
                    if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out rate) || rate <= 0)
                    {
                        error = "--arrival-rate deve ser um número positivo.";
                        return false;
                    }

                    break;
                case "--write-rate":
                    if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out writeRate) || writeRate <= 0)
                    {
                        error = "--write-rate deve ser um número positivo.";
                        return false;
                    }

                    break;
                case "--duration":
                    if (!TryPositive(value, out duration))
                    {
                        error = "--duration deve ser um inteiro positivo (segundos).";
                        return false;
                    }

                    break;
                case "--warmup":
                    if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out warmup))
                    {
                        error = "--warmup deve ser um inteiro não negativo (segundos).";
                        return false;
                    }

                    break;
                case "--read-url":
                    readUrls.Add(value);
                    break;
                case "--write-url":
                    writeUrl = value;
                    break;
                case "--max-inflight-get":
                    if (!TryPositive(value, out maxGet))
                    {
                        error = "--max-inflight-get deve ser um inteiro positivo.";
                        return false;
                    }

                    break;
                case "--max-inflight-post":
                    if (!TryPositive(value, out maxPost))
                    {
                        error = "--max-inflight-post deve ser um inteiro positivo.";
                        return false;
                    }

                    break;
                case "--get-transport-timeout":
                    if (!TryDuration(value, out getTimeout))
                    {
                        error = "--get-transport-timeout inválido (use 2s ou 2000ms).";
                        return false;
                    }

                    break;
                case "--post-transport-timeout":
                    if (!TryDuration(value, out postTimeout))
                    {
                        error = "--post-transport-timeout inválido (use 5s ou 5000ms).";
                        return false;
                    }

                    break;
                case "--useful-deadline":
                    if (!TryDuration(value, out deadline))
                    {
                        error = "--useful-deadline inválido (use 1000ms).";
                        return false;
                    }

                    break;
                case "--emission-tolerance":
                    if (!TryDuration(value, out tolerance))
                    {
                        error = "--emission-tolerance inválido (use 100ms).";
                        return false;
                    }

                    break;
                case "--convergence-timeout":
                    if (!TryDuration(value, out convergence))
                    {
                        error = "--convergence-timeout inválido (use 120s).";
                        return false;
                    }

                    break;
                case "--seed":
                    if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out seed))
                    {
                        error = "--seed deve ser um inteiro.";
                        return false;
                    }

                    break;
                case "--rep":
                    if (!TryPositive(value, out repetition))
                    {
                        error = "--rep deve ser um inteiro positivo.";
                        return false;
                    }

                    break;
                case "--final-api-checks":
                    if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out finalChecks))
                    {
                        error = "--final-api-checks deve ser um inteiro não negativo.";
                        return false;
                    }

                    break;
                case "--out":
                    outDirectory = value;
                    break;
                case "--start-signal":
                    startSignal = value;
                    break;
                case "--origin-connection":
                    origin = value;
                    break;
                case "--projection-connection":
                    projection = value;
                    break;
                case "--manifest":
                    manifestPath = value;
                    break;
                default:
                    error = $"Argumento não reconhecido: {args[i]}";
                    return false;
            }

            i++;
        }

        if (scenario is not ("L1" or "L2" or "L3" or "F08"))
        {
            error = "--scenario deve ser L1, L2, L3 ou F08.";
            return false;
        }

        var urls = new List<Uri>();
        foreach (var text in readUrls)
        {
            if (!TryLoopback(text, out var uri, out error))
            {
                return false;
            }

            urls.Add(uri);
        }

        Uri? write = null;
        if (writeUrl is not null && !TryLoopback(writeUrl, out write, out error))
        {
            return false;
        }

        if (mode == RunMode.ConsolidationOpen && urls.Count is < 1 or > 2)
        {
            error = "--read-url deve ser informado uma ou duas vezes.";
            return false;
        }

        if (mode == RunMode.ConsolidationOpen && scenario is "L3" or "F08" && write is null)
        {
            error = $"O cenário {scenario} exige --write-url.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(outDirectory))
        {
            error = "--out obrigatório (pasta exclusiva, que não pode existir).";
            return false;
        }

        if (string.IsNullOrWhiteSpace(manifestPath) || !File.Exists(manifestPath))
        {
            error = "--manifest obrigatório e deve existir (gravado antes da execução).";
            return false;
        }

        if (string.IsNullOrWhiteSpace(origin) || string.IsNullOrWhiteSpace(projection))
        {
            error = $"Conexões obrigatórias (--origin-connection e --projection-connection, ou as variáveis {OriginConnectionVariable} e {ProjectionConnectionVariable}).";
            return false;
        }

        if (!TryManifest(manifestPath, out var manifest, out error) || manifest is null)
        {
            return false;
        }

        if (!CheckDatabase(origin, manifest.OriginDatabase, "origem", out error) || !CheckDatabase(projection, manifest.ProjectionDatabase, "projeção", out error))
        {
            return false;
        }

        if (Directory.Exists(outDirectory) && Directory.EnumerateFileSystemEntries(outDirectory).Any())
        {
            error = "--out recusado: a pasta de saída já existe e não está vazia (criação exclusiva).";
            return false;
        }

        options = new ConsolidationOptions(
            mode, scenario, rate, duration, warmup, urls, write, writeRate, maxGet, maxPost, getTimeout, postTimeout, deadline, tolerance, convergence,
            seed, repetition, outDirectory, startSignal, origin, projection, manifest, finalChecks);
        return true;
    }

    /// <summary>Somente loopback, http, sem credencial na URL, sem caminho/consulta/fragmento: a ferramenta monta as rotas.</summary>
    public static bool TryLoopback(string text, out Uri uri, out string error)
    {
        error = string.Empty;
        uri = new Uri("http://127.0.0.1/");
        if (!Uri.TryCreate(text, UriKind.Absolute, out var parsed) || parsed.Scheme != Uri.UriSchemeHttp)
        {
            error = "URL recusada: use http://127.0.0.1:<porta> ou http://localhost:<porta>.";
            return false;
        }

        if (!string.IsNullOrEmpty(parsed.UserInfo))
        {
            error = "URL recusada: não pode conter credencial.";
            return false;
        }

        if (parsed.Host is not ("127.0.0.1" or "localhost" or "[::1]"))
        {
            error = "URL recusada: o host deve ser loopback (127.0.0.1, localhost ou [::1]).";
            return false;
        }

        if (parsed.AbsolutePath != "/" || !string.IsNullOrEmpty(parsed.Query) || !string.IsNullOrEmpty(parsed.Fragment))
        {
            error = "URL recusada: informe só o endereço base (sem caminho, consulta ou fragmento).";
            return false;
        }

        uri = parsed;
        return true;
    }

    public static bool TryDuration(string text, out TimeSpan value)
    {
        value = default;
        if (text.EndsWith("ms", StringComparison.Ordinal) && double.TryParse(text.AsSpan(0, text.Length - 2), NumberStyles.Float, CultureInfo.InvariantCulture, out var ms) && ms >= 0)
        {
            value = TimeSpan.FromMilliseconds(ms);
            return true;
        }

        if (text.EndsWith('s') && double.TryParse(text.AsSpan(0, text.Length - 1), NumberStyles.Float, CultureInfo.InvariantCulture, out var s) && s >= 0)
        {
            value = TimeSpan.FromSeconds(s);
            return true;
        }

        return false;
    }

    private static bool TryPositive(string text, out int value) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value) && value > 0;

    private static bool TryManifest(string path, out RunManifest? manifest, out string error)
    {
        manifest = null;
        error = string.Empty;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            manifest = new RunManifest(
                root.GetProperty("runId").GetString() ?? string.Empty,
                root.GetProperty("originDatabase").GetString() ?? string.Empty,
                root.GetProperty("projectionDatabase").GetString() ?? string.Empty,
                root.GetProperty("expectedSeedMovements").GetInt32());
            return true;
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or IOException)
        {
            error = "--manifest ilegível ou incompleto (runId, originDatabase, projectionDatabase, expectedSeedMovements).";
            return false;
        }
    }

    /// <summary>O banco da conexão precisa ser o do manifesto, ter o prefixo próprio e estar em loopback.</summary>
    public static bool CheckDatabase(string connectionString, string manifestDatabase, string role, out string error)
    {
        error = string.Empty;
        try
        {
            var builder = new NpgsqlConnectionStringBuilder(connectionString);
            if (builder.Host is not ("127.0.0.1" or "localhost" or "::1"))
            {
                error = $"Conexão da {role} recusada: o host deve ser loopback.";
                return false;
            }

            if (builder.Database is null || !builder.Database.StartsWith(DatabasePrefix, StringComparison.Ordinal))
            {
                error = $"Conexão da {role} recusada: o banco deve começar com {DatabasePrefix}.";
                return false;
            }

            if (!string.Equals(builder.Database, manifestDatabase, StringComparison.Ordinal))
            {
                error = $"Conexão da {role} recusada: o banco não corresponde ao manifesto próprio.";
                return false;
            }

            return true;
        }
        catch (ArgumentException)
        {
            error = $"Conexão da {role} inválida.";
            return false;
        }
    }
}
