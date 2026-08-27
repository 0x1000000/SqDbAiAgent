using SqDbAiAgent.ConsoleApp.SecurityFilters.HarborFlow.Tables;
using SqDbAiAgent.ConsoleApp.Services;

namespace SqDbAiAgent.Tests;

public sealed class SqlDeterministicValidatorTests
{
    private readonly SqlDeterministicValidator _validator = new(AllTables.StaticList);

    [Fact]
    public void AcceptsKnownReadOnlyQuery() =>
        Assert.True(this._validator.Validate("SELECT TOP (5) [ProductId], [Sku] FROM [ref].[Product]").Success);

    [Theory]
    [InlineData("SELECT * FROM INFORMATION_SCHEMA.TABLES")]
    [InlineData("DELETE FROM [ref].[Product]")]
    [InlineData("SELECT [MissingColumn] FROM [ref].[Product]")]
    [InlineData("SELECT [Value] FROM [dbo].[MissingTable]")]
    [InlineData("SELECT [Sku] FROM [ref].[Product] WHERE [ProductId] = @id")]
    public void RejectsUnsafeOrUnknownSql(string sql) =>
        Assert.False(this._validator.Validate(sql).Success);
}
