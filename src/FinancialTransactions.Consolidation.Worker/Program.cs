using FinancialTransactions.Consolidation.Worker;

// Ponto de entrada explícito (sem instrução de nível superior): não gera um tipo Program, que colidiria com o da API de lançamentos
// nos projetos de teste que referenciam os dois.
internal static class WorkerEntryPoint
{
    private static async Task<int> Main(string[] args)
    {
        if (args.Contains("--backfill", StringComparer.Ordinal))
        {
            return await WorkerApp.RunBackfillAsync(args.Where(a => a != "--backfill").ToArray()).ConfigureAwait(false);
        }

        if (args.FirstOrDefault(a => WorkerAdmin.Modes.Contains(a)) is { } mode)
        {
            return await WorkerAdmin.RunAsync(mode, args.Where(a => a != mode).ToArray(), Console.Out).ConfigureAwait(false);
        }

        await WorkerApp.CreateService(args).RunAsync().ConfigureAwait(false);
        return 0;
    }
}
