using SqDbAiAgent.ConsoleApp.Models.Configuration;

namespace SqDbAiAgent.Tests;

public sealed class McpHttpOptionsTests
{
    [Fact]
    public void DefaultsAreLoopbackAndSilent()
    {
        var options = new McpHttpOptions();
        Assert.Equal("http://localhost:5080", options.Url);
        Assert.False(options.ConsoleOutputEnabled);
        Assert.Equal("McpHttp", McpHttpOptions.SectionName);
    }
}
