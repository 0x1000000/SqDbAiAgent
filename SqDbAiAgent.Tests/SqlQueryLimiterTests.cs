using SqDbAiAgent.ConsoleApp.SecurityFilters.HarborFlow.Tables;
using SqDbAiAgent.ConsoleApp.Services;

namespace SqDbAiAgent.Tests;

public sealed class SqlQueryLimiterTests
{
    [Theory]
    [InlineData("SELECT [ProductId], [Sku] FROM [ref].[Product]")]
    [InlineData("SELECT DISTINCT [Sku] FROM [ref].[Product]")]
    [InlineData("SELECT [ProductCategoryId], COUNT(*) AS [Count] FROM [ref].[Product] GROUP BY [ProductCategoryId]")]
    [InlineData("SELECT COUNT(*) AS [Count] FROM [ref].[Product]")]
    [InlineData("SELECT [ProductId], [Sku] FROM [ref].[Product] ORDER BY [ProductId]")]
    public void AddsConfiguredTopToUnboundedQueries(string sql)
    {
        var result = Validate(sql, 37);

        Assert.True(result.Success, result.FailureMessage);
        Assert.True(result.DefaultRowLimitApplied);
        Assert.Contains("TOP 37", result.ApprovedSql, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("SELECT TOP (5) [ProductId] FROM [ref].[Product]", "TOP (5)")]
    [InlineData("SELECT TOP (500) [ProductId] FROM [ref].[Product]", "TOP (500)")]
    [InlineData("SELECT [ProductId] FROM [ref].[Product] ORDER BY [ProductId] OFFSET 0 ROWS FETCH NEXT 250 ROWS ONLY", "FETCH NEXT 250")]
    public void PreservesExplicitOuterLimits(string sql, string expected)
    {
        var result = Validate(sql, 100);

        Assert.True(result.Success, result.FailureMessage);
        Assert.False(result.DefaultRowLimitApplied);
        Assert.Contains(expected, result.ApprovedSql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LimitsCompoundQueryAtOuterResult()
    {
        const string sql = "SELECT [ProductId] FROM [ref].[Product] UNION SELECT [ProductId] FROM [ref].[Product]";
        var result = Validate(sql, 23);

        Assert.True(result.Success, result.FailureMessage);
        Assert.True(result.DefaultRowLimitApplied);
        Assert.StartsWith("SELECT TOP 23", result.ApprovedSql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("UNION", result.ApprovedSql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("[__row_limit]", result.ApprovedSql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LimitsOrderedCompoundQueryWithOffsetFetch()
    {
        const string sql = "SELECT [ProductId] FROM [ref].[Product] UNION SELECT [ProductId] FROM [ref].[Product] ORDER BY [ProductId]";
        var result = Validate(sql, 19);

        Assert.True(result.Success, result.FailureMessage);
        Assert.True(result.DefaultRowLimitApplied);
        Assert.Contains("OFFSET 0 ROW FETCH NEXT 19 ROW ONLY", result.ApprovedSql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void UsesDefaultOfOneHundred()
    {
        var validator = new SqlDeterministicValidator(AllTables.StaticList);
        var result = validator.Validate("SELECT [ProductId] FROM [ref].[Product]");

        Assert.True(result.Success, result.FailureMessage);
        Assert.Contains("TOP 100", result.ApprovedSql, StringComparison.OrdinalIgnoreCase);
    }

    private static SqlApprovalResult Validate(string sql, int limit) =>
        new SqlDeterministicValidator(AllTables.StaticList, limit).Validate(sql);
}
