using SqDbAiAgent.ConsoleApp.Models;
using SqDbAiAgent.ConsoleApp.Services;

namespace SqDbAiAgent.Tests;

public sealed class InvestigationTests
{
    [Fact]
    public void ActionParserAcceptsInvestigationWithPurpose()
    {
        const string json = """
            {"action":"investigate_sql","message":"","sql":"SELECT TOP (5) [Sku] FROM [ref].[Product] WHERE [Sku] LIKE 'A%'","purpose":"Resolve the requested SKU"}
            """;

        Assert.True(NoToolsAgentResponse.TryParseFromJson(json, out var action));
        Assert.Equal(NoToolsAgentResponseType.InvestigateSql, action.ActionType);
        Assert.Equal("Resolve the requested SKU", action.Purpose);
    }

    [Theory]
    [InlineData("Please investigate why this returned nothing")]
    [InlineData("Can you investigate this code?")]
    public void ExplicitInvestigationIsDetected(string request) =>
        Assert.True(InvestigationRequestPolicy.IsExplicitlyRequested(request));

    [Fact]
    public void InvestigationDefaultsAreSafe()
    {
        var config = new AppConfig();
        Assert.False(config.InvestigationEnabled);
        Assert.Equal(3, config.MaxInvestigationQueries);
        Assert.Equal(100, config.MaxInvestigationVisibleCells);
        Assert.Equal(100, config.DefaultQueryRowLimit);
    }

    [Theory]
    [InlineData("SELECT TOP (5) [Sku] FROM [ref].[Product] WHERE [Sku] LIKE 'A%'", true)]
    [InlineData("SELECT DISTINCT TOP (5) [Sku] FROM [ref].[Product]", true)]
    [InlineData("SELECT COUNT(*) FROM [ref].[Product]", true)]
    [InlineData("SELECT * FROM [ref].[Product]", false)]
    [InlineData("SELECT TOP (101) [Sku] FROM [ref].[Product] WHERE [Sku] LIKE 'A%'", false)]
    [InlineData("SELECT COUNT(*) FROM [ref].[Product] GROUP BY [Sku]", false)]
    [InlineData("SELECT TOP (5) [Sku] FROM [ref].[Product]", false)]
    public void InvestigationShapeIsBounded(string sql, bool expected)
    {
        var valid = ValidatedSqlExecutor.ValidateInvestigationSql(sql, 100, out _);
        Assert.Equal(expected, valid);
    }
}
