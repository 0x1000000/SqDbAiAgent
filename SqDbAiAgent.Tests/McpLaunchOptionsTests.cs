using SqDbAiAgent.ConsoleApp.Services.Mcp;

namespace SqDbAiAgent.Tests;

public sealed class McpLaunchOptionsTests
{
    [Theory]
    [InlineData("http", McpTransport.Http)]
    [InlineData("STDIO", McpTransport.Stdio)]
    public void ParsesSupportedTransport(string value, McpTransport expected)
    {
        Assert.True(McpLaunchOptions.TryParse(["--transport", value], out var options, out var error), error);
        Assert.Equal(expected, options.Transport);
    }

    [Fact]
    public void ParsesEqualsSyntaxAndStdioUser()
    {
        Assert.True(McpLaunchOptions.TryParse(
            ["--transport=stdio", "--database-user-id=7"],
            out var options,
            out var error), error);
        Assert.Equal(McpTransport.Stdio, options.Transport);
        Assert.Equal(7, options.DatabaseUserId);
    }

    [Fact]
    public void ParsesCombinedArgumentsProducedByDesktopConfiguration()
    {
        Assert.True(McpLaunchOptions.TryParse(
            ["--transport stdio", "--database-user-id 7"],
            out var options,
            out var error), error);
        Assert.Equal(McpTransport.Stdio, options.Transport);
        Assert.Equal(7, options.DatabaseUserId);
    }

    [Fact]
    public void NoTransportMeansInteractiveConsole()
    {
        Assert.True(McpLaunchOptions.TryParse([], out var options, out var error), error);
        Assert.Null(options.Transport);
        Assert.Equal(0, options.DatabaseUserId);
    }

    [Theory]
    [InlineData("--transport")]
    [InlineData("--transport", "console")]
    [InlineData("--transport", "http", "--transport", "stdio")]
    [InlineData("--transport", "http", "--database-user-id", "7")]
    [InlineData("--transport", "stdio", "--database-user-id", "invalid")]
    [InlineData("--transport", "stdio", "--database-user-id", "-1")]
    public void RejectsInvalidArguments(params string[] args) =>
        Assert.False(McpLaunchOptions.TryParse(args, out _, out _));
}
