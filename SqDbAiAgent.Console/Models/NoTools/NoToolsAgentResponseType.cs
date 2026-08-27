namespace SqDbAiAgent.ConsoleApp.Models.NoTools;

/// <summary>Actions available in the tool-less structured JSON response contract.</summary>
public enum NoToolsAgentResponseType
{
    Respond,
    RunSql,
    InvestigateSql,
    HandleOffTopic,
    Exit
}
