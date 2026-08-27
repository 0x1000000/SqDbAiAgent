namespace SqDbAiAgent.ConsoleApp.Services.Mcp;

public static class McpContractNames
{
    public const string DatabaseAgentPrompt = "database_agent";
    public const string DatabaseSchemaResource = "database://schema";
    public const string DatabaseSchemaResourceName = "database-schema";
    public const string GetDatabaseSchemaTool = "get_database_schema";
    public const string ListSecurityUsersTool = "list_security_users";
    public const string SubmitSqlTool = "submit_sql";
    public const string InvestigateSqlTool = "investigate_sql";
    public const string DatabaseUserHeader = "X-Database-User-Id";
}
