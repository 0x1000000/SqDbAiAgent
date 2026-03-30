using System.Data;
using System.Text;
using SqDbAiAgent.ConsoleApp.Models;

namespace SqDbAiAgent.ConsoleApp.Services;

public sealed class ConsoleTablePrinter(IConsoleOutput output) : ITablePrinter, IAgentTableFormatter
{
    public void Print(DataTable table)
    {
        if (table.Columns.Count == 0)
        {
            output.OutDataLine("Query completed. Nothing is returned.");
            return;
        }

        if (table.Rows.Count == 0)
        {
            output.OutDataLine("Query returned no rows.");
            return;
        }

        var widths = new int[table.Columns.Count];

        for (var i = 0; i < table.Columns.Count; i++)
        {
            widths[i] = table.Columns[i].ColumnName.Length;
        }

        foreach (DataRow row in table.Rows)
        {
            for (var i = 0; i < table.Columns.Count; i++)
            {
                var cellText = FormatCell(row[i]);
                widths[i] = Math.Max(widths[i], cellText.Length);
            }
        }

        output.OutDataLine(BuildRow(table.Columns.Cast<DataColumn>().Select(c => c.ColumnName).ToArray(), widths));
        output.OutDataLine(BuildSeparator(widths));

        foreach (DataRow row in table.Rows)
        {
            var values = new string[table.Columns.Count];
            for (var i = 0; i < table.Columns.Count; i++)
            {
                values[i] = FormatCell(row[i]);
            }

            output.OutDataLine(BuildRow(values, widths));
        }

        output.OutDataLine(string.Empty);
        output.OutDataLine($"Rows: {table.Rows.Count}");
    }

    public RenderedTable RenderMarkdown(DataTable table, int maxCells)
    {
        if (maxCells <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxCells), "Visible cell budget must be greater than zero.");
        }

        if (table.Columns.Count == 0)
        {
            return new RenderedTable(
                "Query completed. Nothing is returned.",
                table.Rows.Count,
                table.Columns.Count,
                0,
                0,
                0,
                false);
        }

        if (table.Rows.Count == 0)
        {
            var header = BuildMarkdownRow(table.Columns.Cast<DataColumn>().Select(c => c.ColumnName).ToArray());
            var separator = BuildMarkdownSeparator(table.Columns.Count);

            return new RenderedTable(
                $"{header}{Environment.NewLine}{separator}",
                0,
                table.Columns.Count,
                0,
                table.Columns.Count,
                0,
                false);
        }

        var visibleColumns = Math.Min(table.Columns.Count, maxCells);
        var visibleRows = visibleColumns == 0
            ? 0
            : Math.Min(table.Rows.Count, maxCells / visibleColumns);

        if (visibleRows == 0 && table.Rows.Count > 0)
        {
            visibleRows = 1;
            visibleColumns = Math.Min(table.Columns.Count, maxCells);
        }

        var builder = new StringBuilder();
        var headerValues = table.Columns.Cast<DataColumn>().Take(visibleColumns).Select(c => EscapeMarkdown(c.ColumnName)).ToArray();
        builder.AppendLine(BuildMarkdownRow(headerValues));
        builder.AppendLine(BuildMarkdownSeparator(visibleColumns));

        for (var rowIndex = 0; rowIndex < visibleRows; rowIndex++)
        {
            var row = table.Rows[rowIndex];
            var rowValues = new string[visibleColumns];
            for (var columnIndex = 0; columnIndex < visibleColumns; columnIndex++)
            {
                rowValues[columnIndex] = EscapeMarkdown(FormatCell(row[columnIndex]));
            }

            builder.AppendLine(BuildMarkdownRow(rowValues));
        }

        var shownCells = visibleRows * visibleColumns;
        var truncated = visibleRows < table.Rows.Count || visibleColumns < table.Columns.Count;

        return new RenderedTable(
            builder.ToString().TrimEnd(),
            table.Rows.Count,
            table.Columns.Count,
            visibleRows,
            visibleColumns,
            shownCells,
            truncated);
    }

    private static string BuildRow(IReadOnlyList<string> values, IReadOnlyList<int> widths)
    {
        var builder = new StringBuilder();
        builder.Append("|");

        for (var i = 0; i < values.Count; i++)
        {
            builder.Append(' ');
            builder.Append(values[i].PadRight(widths[i]));
            builder.Append(" |");
        }

        return builder.ToString();
    }

    private static string BuildSeparator(IReadOnlyList<int> widths)
    {
        var builder = new StringBuilder();
        builder.Append("|");

        for (var i = 0; i < widths.Count; i++)
        {
            builder.Append(new string('-', widths[i] + 2));
            builder.Append("|");
        }

        return builder.ToString();
    }

    private static string FormatCell(object value)
    {
        return value == DBNull.Value ? "NULL" : Convert.ToString(value) ?? string.Empty;
    }

    private static string BuildMarkdownRow(IReadOnlyList<string> values)
    {
        var builder = new StringBuilder();
        builder.Append("| ");
        builder.Append(string.Join(" | ", values));
        builder.Append(" |");
        return builder.ToString();
    }

    private static string BuildMarkdownSeparator(int columnCount)
    {
        return "| " + string.Join(" | ", Enumerable.Repeat("---", columnCount)) + " |";
    }

    private static string EscapeMarkdown(string value)
    {
        return value.Replace("|", "\\|", StringComparison.Ordinal).Replace(Environment.NewLine, " ", StringComparison.Ordinal);
    }
}
