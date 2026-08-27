using System.ComponentModel;
using System.Reflection;
using SqDbAiAgent.ConsoleApp.SecurityFilters.HarborFlow.Tables;
using SqDbAiAgent.ConsoleApp.Services;

namespace SqDbAiAgent.Tests;

public sealed class DatabaseAgnosticTests
{
    [Fact]
    public void SharedSchemaContextIsGenericAndContainsEavGuidance()
    {
        const string databaseName = "ArbitraryDatabaseName";
        var schema = DatabaseContextService.BuildSchemaPrompt(databaseName, AllTables.StaticList);
        var analyzer = DatabaseContextService.BuildAnalyzerSchemaPrompt(databaseName, AllTables.StaticList);

        Assert.Contains(databaseName, schema, StringComparison.Ordinal);
        Assert.Contains(databaseName, analyzer, StringComparison.Ordinal);
        Assert.DoesNotContain("HarborFlow", schema, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Entity/Attribute/Value", schema, StringComparison.Ordinal);
        Assert.Contains("Do not assume EAV", schema, StringComparison.Ordinal);
    }

    [Fact]
    public void McpDescriptionsDoNotLeakDomainNames()
    {
        var descriptions = typeof(McpDatabaseTools)
            .GetMembers(BindingFlags.Instance | BindingFlags.Public)
            .SelectMany(member => member.GetCustomAttributes<DescriptionAttribute>())
            .Select(attribute => attribute.Description)
            .Concat(typeof(McpSecurityTools)
                .GetMembers(BindingFlags.Instance | BindingFlags.Public)
                .SelectMany(member => member.GetCustomAttributes<DescriptionAttribute>())
                .Select(attribute => attribute.Description))
            .Concat(typeof(McpDatabasePrompts).GetCustomAttributes<DescriptionAttribute>().Select(a => a.Description))
            .Concat(typeof(McpDatabaseResources).GetCustomAttributes<DescriptionAttribute>().Select(a => a.Description));

        foreach (var description in descriptions)
        {
            Assert.DoesNotContain("HarborFlow", description, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Theory]
    [InlineData(McpTransport.Http, 0, true)]
    [InlineData(McpTransport.Stdio, 0, true)]
    [InlineData(McpTransport.Stdio, 7, true)]
    [InlineData(McpTransport.Stdio, 0, false)]
    public void McpServerInstructionsAreGenericAndDescribeTheWorkflow(
        McpTransport transport,
        int databaseUserId,
        bool hasSecurityProfile)
    {
        const string databaseName = "ArbitraryDatabaseName";
        var instructions = McpAgentInstructionsProviderService.Build(
            transport,
            databaseUserId,
            databaseName,
            hasSecurityProfile);

        Assert.Contains(databaseName, instructions, StringComparison.Ordinal);
        Assert.Contains(McpContractNames.GetDatabaseSchemaTool, instructions, StringComparison.Ordinal);
        Assert.Contains(McpContractNames.SubmitSqlTool, instructions, StringComparison.Ordinal);
        Assert.Contains("Entity/Attribute/Value", instructions, StringComparison.Ordinal);
        Assert.Contains("user does not know SQL", instructions, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("HarborFlow", instructions, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("LumenHarbor", instructions, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(
            hasSecurityProfile,
            instructions.Contains(McpContractNames.ListSecurityUsersTool, StringComparison.Ordinal));
    }
}
