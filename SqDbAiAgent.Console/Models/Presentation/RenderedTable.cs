namespace SqDbAiAgent.ConsoleApp.Models.Presentation;

public readonly record struct RenderedTable(
    string Markdown,
    int TotalRows,
    int TotalColumns,
    int ShownRows,
    int ShownColumns,
    int ShownCells,
    bool Truncated);
