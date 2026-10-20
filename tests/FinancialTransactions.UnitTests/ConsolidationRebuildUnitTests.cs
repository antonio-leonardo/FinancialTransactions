using System.Reflection;
using System.Text.Json;
using FinancialTransactions.Consolidation;
using Microsoft.Extensions.Options;
using Npgsql;

namespace FinancialTransactions.UnitTests;

/// <summary>Estados, tipos de divergência, nomes de aplicação e saída JSON da verificação e da reconstrução.</summary>
public sealed class ConsolidationRebuildUnitTests
{
    [Fact(DisplayName = "RebuildStates_AreTheSevenOnesOfTheCheckConstraint_InTheDocumentedOrder")]
    public void RebuildStatesAreTheSevenOnesOfTheCheckConstraintInTheDocumentedOrder()
    {
        Assert.Equal(["prepared", "captured", "replayed", "reconciled", "enabled", "promoted", "cancelled"], RebuildStates.All);
    }

    [Fact(DisplayName = "DeviationKinds_AreDistinctNonEmptyStableIdentifiers")]
    public void DeviationKindsAreDistinctNonEmptyStableIdentifiers()
    {
        var values = typeof(DeviationKinds).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral)
            .Select(f => f.GetRawConstantValue() as string ?? string.Empty)
            .ToList();

        Assert.True(values.Count >= 15);
        Assert.Equal(values.Count, values.Distinct(StringComparer.Ordinal).Count());
        Assert.All(values, v => Assert.Matches("^[a-z_]+$", v));
    }

    [Fact(DisplayName = "ApplicationNames_OnlyWorkerAndApiCountAsConsumers_CoordinatorAndAdminDoNot")]
    public void ApplicationNamesOnlyWorkerAndApiCountAsConsumersCoordinatorAndAdminDoNot()
    {
        Assert.Equal([ConsolidationApplicationNames.Worker, ConsolidationApplicationNames.Api], ConsolidationApplicationNames.Consumers);
        Assert.DoesNotContain(ConsolidationApplicationNames.Admin, ConsolidationApplicationNames.Consumers);
        Assert.DoesNotContain(ConsolidationApplicationNames.Coordinator, ConsolidationApplicationNames.Consumers);
        Assert.False(ConsolidationApplicationNames.Admin.StartsWith(ConsolidationApplicationNames.Worker, StringComparison.Ordinal));
    }

    [Fact(DisplayName = "ConfigurationFactory_AppliesTheApplicationName_ToBothStrings_AndLeavesItAloneWhenEmpty")]
    public void ConfigurationFactoryAppliesTheApplicationNameToBothStringsAndLeavesItAloneWhenEmpty()
    {
        var named = new ConfigurationConsolidationConnectionFactory(Options.Create(new ConsolidationConnectionOptions
        {
            Origin = "Host=h;Database=a;Username=u;Password=p",
            Projection = "Host=h;Database=b;Username=u;Password=p",
            ApplicationName = ConsolidationApplicationNames.Worker,
        })).Open(ConsolidationConnectionRole.Consumer);
        var plain = new ConfigurationConsolidationConnectionFactory(Options.Create(new ConsolidationConnectionOptions
        {
            Origin = "Host=h;Database=a;Username=u;Password=p;Application Name=meu-app",
            Projection = "Host=h;Database=b;Username=u;Password=p",
        })).Open(ConsolidationConnectionRole.Consumer);

        Assert.Equal(ConsolidationApplicationNames.Worker, new NpgsqlConnectionStringBuilder(named.Origin).ApplicationName);
        Assert.Equal(ConsolidationApplicationNames.Worker, new NpgsqlConnectionStringBuilder(named.Projection).ApplicationName);
        Assert.Equal("meu-app", new NpgsqlConnectionStringBuilder(plain.Origin).ApplicationName);
    }

    [Fact(DisplayName = "AdminJson_KeepsAccentsReadable_AndStaysValidJson")]
    public void AdminJsonKeepsAccentsReadableAndStaysValidJson()
    {
        var writer = new StringWriter();

        AdminJson.Write(writer, new { reason = "a origem não pode ser o destino" });

        Assert.Contains("não", writer.ToString(), StringComparison.Ordinal);
        Assert.Equal("a origem não pode ser o destino", JsonDocument.Parse(writer.ToString()).RootElement.GetProperty("reason").GetString());
    }

    [Fact(DisplayName = "RebuildRequest_And_Roles_AreDistinctFromTheConsumerRoles")]
    public void RebuildRequestAndRolesAreDistinctFromTheConsumerRoles()
    {
        Assert.NotEqual(ConsolidationConnectionRole.Verify, ConsolidationConnectionRole.Rebuild);
        Assert.NotEqual(ConsolidationConnectionRole.Consumer, ConsolidationConnectionRole.Rebuild);
        Assert.Equal(new RebuildRequest("op-1", "db"), new RebuildRequest("op-1", "db", null));
    }
}
