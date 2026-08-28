using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SqDbAiAgent.ConsoleApp.Models.Configuration;
using SqDbAiAgent.ConsoleApp.Services.Logging;

namespace SqDbAiAgent.Tests;

public sealed class JsonlLoggerProviderTests
{
    [Fact]
    public void EmptyPathDisablesLogging()
    {
        using var provider = new JsonlLoggerProvider(new FileLoggingOptions { Path = "  " }, "Test");
        Assert.False(provider.CreateLogger("Category").IsEnabled(LogLevel.Critical));
    }

    [Fact]
    public void EmptyPathKeepsDependencyInjectedLoggersDisabledAtEveryLevel()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Logging:File:Path"] = ""
            })
            .Build();
        var services = new ServiceCollection();
        services.AddLogging(logging =>
        {
            logging.ClearProviders();
            logging.AddJsonlFile(configuration, "Test");
        });

        using var serviceProvider = services.BuildServiceProvider();
        var logger = serviceProvider.GetRequiredService<ILogger<JsonlLoggerProviderTests>>();

        foreach (var level in Enum.GetValues<LogLevel>())
        {
            Assert.False(logger.IsEnabled(level));
        }
    }

    [Fact]
    public void DefaultLevelIsWarning()
    {
        using var provider = new JsonlLoggerProvider(new FileLoggingOptions { Path = TempPattern() }, "Test");
        var logger = provider.CreateLogger("Category");
        Assert.False(logger.IsEnabled(LogLevel.Information));
        Assert.True(logger.IsEnabled(LogLevel.Warning));
    }

    [Fact]
    public void DefaultFormatIsPlainText()
    {
        var pattern = TempPattern();
        using var provider = new JsonlLoggerProvider(new FileLoggingOptions { Path = pattern }, "Interactive");
        provider.CreateLogger("Test.Category").LogWarningEvent("SqlRejected",
            ("reason", "not allowed"));

        var text = File.ReadAllText(DatedPath(pattern));
        Assert.Contains("[Warning] SqlRejected", text, StringComparison.Ordinal);
        Assert.Contains("category=\"Test.Category\"", text, StringComparison.Ordinal);
        Assert.Contains("reason=\"not allowed\"", text, StringComparison.Ordinal);
        Assert.ThrowsAny<JsonException>(() => JsonDocument.Parse(text));
    }

    [Fact]
    public void PlainTextDoesNotUseUnicodeEscapesForOrdinaryCharacters()
    {
        var pattern = TempPattern();
        using var provider = new JsonlLoggerProvider(new FileLoggingOptions { Path = pattern }, "Interactive");
        provider.CreateLogger("Category").LogWarningEvent("ReadableText",
            ("message", "Customer's balance is < 20 €"));

        var text = File.ReadAllText(DatedPath(pattern));
        Assert.Contains("Customer's balance is < 20 €", text, StringComparison.Ordinal);
        Assert.DoesNotContain("\\u0027", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\\u003C", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PlainTextDoesNotUseUnicodeEscapesForQuotesInsideNestedObjects()
    {
        var pattern = TempPattern();
        using var provider = new JsonlLoggerProvider(
            new FileLoggingOptions { Path = pattern, MinimumLevel = LogLevel.Debug }, "Interactive");
        var request = new { messages = new[] { new { role = "system", content = "Schema: {\"table\":\"Customer\"}" } } };
        provider.CreateLogger("Category").LogDebugEvent("LlmRequest", ("request", request));

        var text = File.ReadAllText(DatedPath(pattern));
        Assert.Contains("Schema: {\\\"table\\\":\\\"Customer\\\"}", text, StringComparison.Ordinal);
        Assert.DoesNotContain("\\u0022", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PlainTextPreservesMultilineSqlAsIndentedBlock()
    {
        var pattern = TempPattern();
        using var provider = new JsonlLoggerProvider(
            new FileLoggingOptions { Path = pattern, MinimumLevel = LogLevel.Debug }, "Interactive");
        provider.CreateLogger("Category").LogDebugEvent("SqlApproved",
            ("approvedSql", "SELECT Id\r\nFROM Customer\r\nWHERE Active = 1"),
            ("attempt", 1));

        var text = File.ReadAllText(DatedPath(pattern));
        Assert.Contains(
            "  approvedSql:" + Environment.NewLine
            + "    SELECT Id" + Environment.NewLine
            + "    FROM Customer" + Environment.NewLine
            + "    WHERE Active = 1" + Environment.NewLine
            + "  attempt=1",
            text,
            StringComparison.Ordinal);
    }

    [Fact]
    public void JsonlKeepsMultilineSqlOnOnePhysicalLine()
    {
        var pattern = TempPattern();
        using var provider = new JsonlLoggerProvider(
            new FileLoggingOptions { Path = pattern, MinimumLevel = LogLevel.Debug, Format = FileLogFormat.Jsonl },
            "Interactive");
        provider.CreateLogger("Category").LogDebugEvent("SqlApproved", ("sql", "SELECT 1\nFROM Test"));

        var lines = File.ReadAllLines(DatedPath(pattern));
        Assert.Single(lines);
        using var json = JsonDocument.Parse(lines[0]);
        Assert.Equal("SELECT 1\nFROM Test", json.RootElement.GetProperty("sql").GetString());
    }

    [Fact]
    public void WritesValidStructuredJsonAndDropsSecretFields()
    {
        var pattern = TempPattern();
        using var provider = new JsonlLoggerProvider(
            new FileLoggingOptions { Path = pattern, MinimumLevel = LogLevel.Debug, Format = FileLogFormat.Jsonl }, "McpStdio");
        provider.CreateLogger("Test.Category").LogDebugEvent("SqlProposed",
            ("sql", "SELECT 1"), ("apiKey", "never-write-me"));

        using var json = JsonDocument.Parse(File.ReadAllText(DatedPath(pattern)).Trim());
        var root = json.RootElement;
        Assert.Equal("SqlProposed", root.GetProperty("event").GetString());
        Assert.Equal("SELECT 1", root.GetProperty("sql").GetString());
        Assert.Equal("McpStdio", root.GetProperty("mode").GetString());
        Assert.True(root.TryGetProperty("timestamp", out _));
        Assert.True(root.TryGetProperty("sessionId", out _));
        Assert.False(root.TryGetProperty("apiKey", out _));
    }

    [Fact]
    public void RedactsConfiguredSecretsEvenInsideMessages()
    {
        var pattern = TempPattern();
        using var provider = new JsonlLoggerProvider(
            new FileLoggingOptions { Path = pattern, MinimumLevel = LogLevel.Debug, Format = FileLogFormat.Jsonl },
            "Test",
            ["sensitive-value"]);
        var logger = provider.CreateLogger("Category");
        logger.LogWarning("Provider returned sensitive-value in an error");

        var text = File.ReadAllText(DatedPath(pattern));
        Assert.DoesNotContain("sensitive-value", text, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConcurrentWritesProduceOneJsonObjectPerLine()
    {
        var pattern = TempPattern();
        using var provider = new JsonlLoggerProvider(
            new FileLoggingOptions { Path = pattern, MinimumLevel = LogLevel.Debug, Format = FileLogFormat.Jsonl }, "Test");
        var logger = provider.CreateLogger("Category");
        await Task.WhenAll(Enumerable.Range(0, 30).Select(index => Task.Run(() =>
            logger.LogDebugEvent("Concurrent", ("index", index)))));

        var lines = await File.ReadAllLinesAsync(DatedPath(pattern));
        Assert.Equal(30, lines.Length);
        foreach (var line in lines) using (JsonDocument.Parse(line)) { }
    }

    [Fact]
    public void MissingDateTokenIsInsertedBeforeExtension()
    {
        var directory = NewTempDirectory();
        var path = Path.Combine(directory, "application.jsonl");
        using var provider = new JsonlLoggerProvider(new FileLoggingOptions { Path = path }, "Test");
        provider.CreateLogger("Category").LogWarningEvent("Warning");
        Assert.True(File.Exists(Path.Combine(directory, $"application-{DateTime.UtcNow:yyyyMMdd}.jsonl")));
    }

    [Fact]
    public void RetentionOnlyDeletesMatchingOldFiles()
    {
        var directory = NewTempDirectory();
        var pattern = Path.Combine(directory, "application-{date}.jsonl");
        var oldMatching = Path.Combine(directory, "application-20000101.jsonl");
        var unrelated = Path.Combine(directory, "other-20000101.jsonl");
        File.WriteAllText(oldMatching, "old");
        File.WriteAllText(unrelated, "keep");
        File.SetLastWriteTimeUtc(oldMatching, DateTime.UtcNow.AddDays(-20));
        File.SetLastWriteTimeUtc(unrelated, DateTime.UtcNow.AddDays(-20));

        using var provider = new JsonlLoggerProvider(
            new FileLoggingOptions { Path = pattern, RetainedDays = 7 }, "Test");
        provider.CreateLogger("Category").LogWarningEvent("Warning");

        Assert.False(File.Exists(oldMatching));
        Assert.True(File.Exists(unrelated));
    }

    [Fact]
    public void FileFailuresDoNotEscapeLoggingCall()
    {
        var directory = NewTempDirectory();
        var pattern = Path.Combine(directory, "blocked-{date}.jsonl");
        Directory.CreateDirectory(DatedPath(pattern));
        using var provider = new JsonlLoggerProvider(new FileLoggingOptions { Path = pattern }, "Test");
        var exception = Record.Exception(() => provider.CreateLogger("Category").LogWarningEvent("Warning"));
        Assert.Null(exception);
    }

    private static string TempPattern() => Path.Combine(NewTempDirectory(), "application-{date}.jsonl");
    private static string DatedPath(string pattern) => pattern.Replace("{date}", DateTime.UtcNow.ToString("yyyyMMdd"));
    private static string NewTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "SqDbAiAgent.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
