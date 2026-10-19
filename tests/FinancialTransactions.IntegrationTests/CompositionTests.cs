using FinancialTransactions.Api.Movements;
using Microsoft.Extensions.DependencyInjection;

namespace FinancialTransactions.IntegrationTests;

/// <summary>Lê a composição do host de produção; não registra nada.</summary>
public sealed class CompositionTests(PostgresFixture fixture) : IntegrationTest(fixture)
{
    [Fact(DisplayName = "Composition_ProductionHost_ResolvesNoFailurePoints")]
    public void CompositionProductionHostResolvesNoFailurePoints()
    {
        var points = Fixture.Factory.Services.GetRequiredService<IRegisterMovementFailurePoints>();

        Assert.IsType<NoFailurePoints>(points);
    }
}
