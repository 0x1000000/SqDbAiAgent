using Microsoft.AspNetCore.Http;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using SqDbAiAgent.ConsoleApp.Models;
using SqDbAiAgent.ConsoleApp.Services;
using SqDbAiAgent.ConsoleApp.Services.Mcp;

namespace SqDbAiAgent.Tests;

public sealed class LocalDatabaseIntegrationTests
{
    [Fact]
    [Trait("Category", "Integration")]
    public async Task DatabaseContextAndMcpQueriesWorkAgainstConfiguredDatabase()
    {
        var connectionString = Environment.GetEnvironmentVariable("SQDBAIAGENT_TEST_CONNECTION_STRING");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return;
        }

        var config = new AppConfig
        {
            ConnectionString = connectionString,
            DefaultQueryRowLimit = 25,
            MaxAgentVisibleCells = 20,
            MaxInvestigationVisibleCells = 10
        };
        var contextService = new DatabaseContextService(
            Options.Create(config),
            new SecurityFilterFactoryService());
        var formatter = new TableResultFormatterService(new NullConsoleOutputService());
        var httpContextAccessor = new HttpContextAccessor();
        var databaseName = new SqlConnectionStringBuilder(connectionString).InitialCatalog;
        var runtimeContext = new McpRuntimeContextService(
            McpTransport.Http,
            0,
            databaseName,
            new SecurityFilterFactoryService().HasSecurityProfile(databaseName),
            httpContextAccessor);
        var service = new McpDatabaseService(
            contextService,
            Options.Create(config),
            formatter,
            runtimeContext);

        var context = await contextService.GetAsync();
        Assert.DoesNotContain(context.DatabaseName, context.SchemaPrompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Entity/Attribute/Value", context.SchemaPrompt, StringComparison.Ordinal);

        var users = await service.ListSecurityUsersAsync();
        Assert.NotEmpty(users);

        httpContextAccessor.HttpContext = new DefaultHttpContext();
        var missingIdentity = await service.SubmitAsync(
            "Show one customer",
            "SELECT TOP (1) [CustomerId], [CustomerName] FROM [ref].[Customer] ORDER BY [CustomerId]");
        Assert.False(missingIdentity.Success);

        var firstContext = ContextFor(users[0].Id);
        httpContextAccessor.HttpContext = firstContext;
        var defaultLimited = await service.SubmitAsync(
            "Show products",
            "SELECT [ProductId], [Sku] FROM [ref].[Product] ORDER BY [ProductId]");
        Assert.True(defaultLimited.Success, defaultLimited.Error);
        Assert.Contains("TOP 25", defaultLimited.ApprovedSql, StringComparison.OrdinalIgnoreCase);
        Assert.True(defaultLimited.TotalRows <= 25);

        var final = await service.SubmitAsync(
            "Show one customer",
            "SELECT TOP (1) [CustomerId], [CustomerName] FROM [ref].[Customer] ORDER BY [CustomerId]");
        Assert.True(final.Success, final.Error);
        Assert.NotNull(final.ExecutedSql);
        Assert.NotEqual(final.ApprovedSql, final.ExecutedSql);
        Assert.Contains("TOP (1)", final.ApprovedSql, StringComparison.OrdinalIgnoreCase);

        var explicitLargerLimit = await service.SubmitAsync(
            "Show products",
            "SELECT TOP (12) [ProductId], [Sku] FROM [ref].[Product] ORDER BY [ProductId]");
        Assert.True(explicitLargerLimit.Success, explicitLargerLimit.Error);
        Assert.Contains("TOP (12)", explicitLargerLimit.ApprovedSql, StringComparison.OrdinalIgnoreCase);

        var investigation = await service.InvestigateAsync(
            "Find a customer code",
            "Resolve the exact stored customer code",
            "SELECT DISTINCT TOP (10) [CustomerId], [CustomerCode] FROM [ref].[Customer] WHERE [CustomerCode] LIKE 'C%'");
        Assert.True(investigation.Success, investigation.Error);
        Assert.True(investigation.ShownRows <= 10);

        var aggregateInvestigation = await service.InvestigateAsync(
            "Count matching products",
            "Count the matching products",
            "SELECT COUNT(*) AS [ProductCount] FROM [ref].[Product] WHERE [Sku] LIKE 'L%'");
        Assert.True(aggregateInvestigation.Success, aggregateInvestigation.Error);
        Assert.Contains("TOP 10", aggregateInvestigation.ApprovedSql, StringComparison.OrdinalIgnoreCase);

        var broadProbe = await service.InvestigateAsync(
            "Browse customers",
            "Browse arbitrary rows",
            "SELECT * FROM [ref].[Customer]");
        Assert.False(broadProbe.Success);

        if (users.Count > 1)
        {
            httpContextAccessor.HttpContext = ContextFor(users[1].Id);
            var second = await service.SubmitAsync(
                "Count customers",
                "SELECT COUNT(*) AS [CustomerCount] FROM [ref].[Customer]");
            Assert.True(second.Success, second.Error);
            Assert.NotEqual(final.ExecutedSql, second.ExecutedSql);
        }
    }

    private static DefaultHttpContext ContextFor(int userId)
    {
        var context = new DefaultHttpContext();
        context.Request.Headers[McpDatabaseService.UserHeaderName] = userId.ToString();
        return context;
    }
}
