using SqDbAiAgent.ConsoleApp.Services;
using SqDbAiAgent.ConsoleApp.Services.Mcp;
using SqExpress;

namespace SqDbAiAgent.Tests;

public sealed class McpSecurityTests
{
    [Fact]
    public void RequiresSelectionWhenIdentitiesExist()
    {
        var context = CreateContext(true, new Dictionary<int, string> { [7] = "Test user" });

        var missing = McpDatabaseService.ResolveSecurityUser(null, context);
        var selected = McpDatabaseService.ResolveSecurityUser("7", context);
        var unknown = McpDatabaseService.ResolveSecurityUser("8", context);

        Assert.False(missing.Success);
        Assert.Contains("list_security_users", missing.Error);
        Assert.True(selected.Success);
        Assert.Equal(7, selected.UserId);
        Assert.False(unknown.Success);
    }

    [Fact]
    public void RejectsSelectionWhenDatabaseHasNoIdentities()
    {
        var context = CreateContext(false, new Dictionary<int, string>());
        Assert.True(McpDatabaseService.ResolveSecurityUser(null, context).Success);
        Assert.False(McpDatabaseService.ResolveSecurityUser("1", context).Success);
    }

    [Fact]
    public void StdioMayUseUnfilteredContextWhenIdentitiesExist()
    {
        var context = CreateContext(true, new Dictionary<int, string> { [7] = "Test user" });
        var result = McpDatabaseService.ResolveSecurityUser(null, context, allowUnfiltered: true);
        Assert.True(result.Success);
        Assert.Null(result.UserId);
    }

    private static DatabaseContext CreateContext(
        bool requiresUser,
        IReadOnlyDictionary<int, string> users)
    {
        IReadOnlyList<TableBase> tables = [];
        return new DatabaseContext(
            "ArbitraryDatabaseName",
            "Server=(local);Database=ArbitraryDatabaseName;Integrated Security=True",
            tables,
            new VoidSecurityFilter(tables),
            "{}",
            "{}",
            requiresUser,
            users);
    }
}
