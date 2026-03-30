using SqExpress;

namespace SqDbAiAgent.ConsoleApp.Services;

public interface ISqlApprovalService
{
    ISqlApprovalSession CreateSession(
        IConsoleOutput output,
        string databaseName,
        IReadOnlyList<TableBase> publicTables,
        string schemaPrompt);
}
