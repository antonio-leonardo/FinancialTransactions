using System.Globalization;
using Npgsql;

namespace FinancialTransactions.LoadTest;

/// <summary>Argumentos da linha de comando. Recusa tudo que não seja a API local e um banco descartável ft_load_*.</summary>
internal sealed record Options(
    Uri BaseUrl,
    string AdminConnection,
    int DurationSeconds,
    int WarmupSeconds,
    int Workers,
    int Seed,
    string OutDirectory)
{
    public const string AdminConnectionEnvironmentVariable = "FT_LOAD_ADMIN_CONNECTION";
    public const string LoadDatabasePrefix = "ft_load_";

    public static bool TryParse(string[] args, out Options? options, out string error)
    {
        options = null;
        error = string.Empty;

        string? baseUrl = null;
        var adminConnection = Environment.GetEnvironmentVariable(AdminConnectionEnvironmentVariable);
        string? outDirectory = null;
        var duration = 60;
        var warmup = 10;
        var workers = 32;
        var seed = 20261019;

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
                case "--base-url":
                    baseUrl = value;
                    break;
                case "--admin-connection":
                    adminConnection = value;
                    break;
                case "--out":
                    outDirectory = value;
                    break;
                case "--duration-seconds":
                    if (!TryPositive(value, out duration))
                    {
                        error = "--duration-seconds deve ser um inteiro positivo.";
                        return false;
                    }

                    break;
                case "--warmup-seconds":
                    if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out warmup))
                    {
                        error = "--warmup-seconds deve ser um inteiro não negativo.";
                        return false;
                    }

                    break;
                case "--workers":
                    if (!TryPositive(value, out workers))
                    {
                        error = "--workers deve ser um inteiro positivo.";
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
                default:
                    error = $"Argumento não reconhecido: {args[i]}";
                    return false;
            }

            i++;
        }

        if (baseUrl is null || !Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttp)
        {
            error = "--base-url obrigatório: http://127.0.0.1:<porta> ou http://localhost:<porta>.";
            return false;
        }

        // Só a API local iniciada para o ensaio.
        if (uri.Host is not ("127.0.0.1" or "localhost"))
        {
            error = "--base-url recusado: o host deve ser 127.0.0.1 ou localhost.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(adminConnection))
        {
            error = $"--admin-connection obrigatório (ou variável {AdminConnectionEnvironmentVariable}).";
            return false;
        }

        if (!IsLoadDatabase(adminConnection))
        {
            error = $"--admin-connection recusado: o banco deve começar com {LoadDatabasePrefix}.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(outDirectory))
        {
            error = "--out obrigatório.";
            return false;
        }

        options = new Options(uri, adminConnection, duration, warmup, workers, seed, outDirectory);
        return true;
    }

    private static bool TryPositive(string text, out int value)
    {
        return int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value) && value > 0;
    }

    private static bool IsLoadDatabase(string connectionString)
    {
        try
        {
            var database = new NpgsqlConnectionStringBuilder(connectionString).Database;
            return database is not null && database.StartsWith(LoadDatabasePrefix, StringComparison.Ordinal);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
