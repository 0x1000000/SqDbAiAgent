using SqExpress;

namespace SqDbAiAgent.ConsoleApp.Services;

public interface IMessageAnalyzeService
{
    IMessageAnalyzeSession CreateSession(
        IConsoleOutput output,
        string databaseName,
        IReadOnlyList<TableBase> publicTables,
        string analyzerSchemaPrompt);
}
