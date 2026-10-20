using System.Globalization;
using FinancialTransactions.Consolidation;
using Microsoft.Extensions.Options;

namespace FinancialTransactions.Consolidation.Worker;

/// <summary>
/// Comandos administrativos do executável do Worker, sem Kestrel e sem consumidor: <c>--status</c>, <c>--list-quarantine</c> e
/// <c>--requeue</c>. As connection strings vêm do ambiente (<c>FT__ConnectionStrings__*</c>); a saída é JSON em stdout e o código de saída
/// está em <see cref="AdminExitCodes"/>. Não há endpoint HTTP de administração.
/// </summary>
public static class WorkerAdmin
{
    public static readonly IReadOnlySet<string> Modes = new HashSet<string>(StringComparer.Ordinal) { "--status", "--list-quarantine", "--requeue" };

    public static async Task<int> RunAsync(string mode, string[] args, TextWriter output, Action<HostApplicationBuilder>? configure = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(output);

        var environment = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")
            ?? Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT")
            ?? Environments.Production;
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { Args = [], EnvironmentName = environment });
        builder.Configuration.AddEnvironmentVariables("FT__");
        builder.Services.AddConsolidationCore(connectTimeoutSeconds: 5, commandTimeoutSeconds: 15);
        builder.Services.AddSingleton<ConsolidationStatusReader>();
        builder.Services.AddSingleton<QuarantineAdmin>();
        configure?.Invoke(builder);

        using var host = builder.Build();
        try
        {
            // Sem hosted service, ValidateOnStart não roda: a validação é explícita e uma configuração inválida sai com código 2.
            _ = host.Services.GetRequiredService<IOptions<ConsolidationConnectionOptions>>().Value;
        }
        catch (OptionsValidationException ex)
        {
            AdminJson.Write(output, new { state = "unknown", error = "configuração inválida: " + string.Join(" ", ex.Failures) });
            return AdminExitCodes.Unreachable;
        }

        var options = Parse(args);
        if (options.Error is not null)
        {
            AdminJson.Write(output, new { state = "refused", reason = options.Error });
            return AdminExitCodes.Refused;
        }

        switch (mode)
        {
            case "--status":
                var (report, exit) = await host.Services.GetRequiredService<ConsolidationStatusReader>().ReadAsync(cancellationToken).ConfigureAwait(false);
                AdminJson.Write(output, report);
                return exit;

            case "--list-quarantine":
                return await host.Services.GetRequiredService<QuarantineAdmin>().ListAsync(options.Limit, output, cancellationToken).ConfigureAwait(false);

            case "--requeue":
                var request = new RequeueRequest(
                    options.Operation ?? string.Empty,
                    options.Events,
                    options.Reason ?? string.Empty,
                    options.Apply,
                    options.PlanDigest,
                    options.ConfirmDatabase ?? string.Empty);
                return await host.Services.GetRequiredService<QuarantineAdmin>().RequeueAsync(request, output, cancellationToken).ConfigureAwait(false);

            default:
                AdminJson.Write(output, new { state = "refused", reason = "modo desconhecido" });
                return AdminExitCodes.Refused;
        }
    }

    private sealed record Parsed(
        string? Operation,
        List<Guid> Events,
        string? Reason,
        bool Apply,
        string? PlanDigest,
        string? ConfirmDatabase,
        int Limit,
        string? Error);

    private static Parsed Parse(string[] args)
    {
        string? operation = null, reason = null, plan = null, confirm = null;
        var events = new List<Guid>();
        var apply = false;
        var limit = 100;
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg == "--apply")
            {
                apply = true;
                continue;
            }

            if (i + 1 >= args.Length)
            {
                return Fail($"argumento sem valor: {arg}");
            }

            var value = args[++i];
            switch (arg)
            {
                case "--operation":
                    operation = value;
                    break;
                case "--reason":
                    reason = value;
                    break;
                case "--plan-digest":
                    plan = value;
                    break;
                case "--confirm-origin-database":
                    confirm = value;
                    break;
                case "--limit":
                    if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out limit))
                    {
                        return Fail("--limit inválido");
                    }

                    break;
                case "--event":
                case "--events":
                    foreach (var part in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    {
                        if (!Guid.TryParseExact(part, "D", out var id))
                        {
                            return Fail("event_id fora do formato (GUID com hífens)");
                        }

                        events.Add(id);
                    }

                    break;
                default:
                    return Fail($"argumento desconhecido: {arg}");
            }
        }

        return new Parsed(operation, events, reason, apply, plan, confirm, limit, null);
    }

    private static Parsed Fail(string error) => new(null, [], null, false, null, null, 100, error);
}
