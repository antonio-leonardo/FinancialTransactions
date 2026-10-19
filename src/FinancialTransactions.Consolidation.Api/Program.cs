using FinancialTransactions.Consolidation.Api;

// Ponto de entrada explícito (sem instrução de nível superior): não gera um tipo Program, que colidiria com o da API de lançamentos
// nos projetos de teste que referenciam as duas.
internal static class ConsolidationApiEntryPoint
{
    private static async Task Main(string[] args)
    {
        await ConsolidationApiApp.Create(args).RunAsync().ConfigureAwait(false);
    }
}
