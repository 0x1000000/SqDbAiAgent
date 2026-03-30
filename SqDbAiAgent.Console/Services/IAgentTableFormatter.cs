using System.Data;
using SqDbAiAgent.ConsoleApp.Models;

namespace SqDbAiAgent.ConsoleApp.Services;

public interface IAgentTableFormatter
{
    RenderedTable RenderMarkdown(DataTable table, int maxCells);
}
