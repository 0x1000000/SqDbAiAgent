using System.Data;

namespace SqDbAiAgent.ConsoleApp.Services;

public interface ITablePrinter
{
    void Print(DataTable table);
}
