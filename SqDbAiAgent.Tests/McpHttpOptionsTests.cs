using SqDbAiAgent.ConsoleApp.Models.Configuration;

namespace SqDbAiAgent.Tests;

public sealed class McpHttpOptionsTests
{
    [Fact]
    public void DefaultsUseLoopback()
    {
        var options = new McpHttpOptions();
        Assert.Equal("http://localhost:5080", options.Url);
        Assert.Equal("McpHttp", McpHttpOptions.SectionName);
    }
}
