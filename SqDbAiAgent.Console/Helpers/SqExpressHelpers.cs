using SqExpress;
using SqExpress.Syntax.Names;

namespace SqDbAiAgent.ConsoleApp.Helpers;

public static class SqExpressHelpers
{
    public static IReadOnlyList<(string From, string To)> InferRelationships(IReadOnlyList<TableBase> publicTables)
    {
        var result = new List<(string From, string To)>();

        foreach (var table in publicTables)
        {
            foreach (var column in table.Columns)
            {
                var foreignKeyColumns = column.ColumnMeta?.ForeignKeyColumns;
                if (foreignKeyColumns is null)
                {
                    continue;
                }

                foreach (var foreignKeyColumn in foreignKeyColumns)
                {
                    result.Add((
                        $"{FormatTableName(table)}.[{column.ColumnName.Name}]",
                        $"{FormatTableName(foreignKeyColumn.Table.FullName)}.[{foreignKeyColumn.ColumnName.Name}]"
                    ));
                }
            }
        }

        return result
            .Distinct()
            .OrderBy(i => i.From, StringComparer.OrdinalIgnoreCase)
            .ThenBy(i => i.To, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static string BuildTableComparisonKey(IExprTableFullName fullName)
    {
        var table = fullName.AsExprTableFullName();
        var schema = table.DbSchema?.Schema.Name;
        return string.IsNullOrWhiteSpace(schema)
            ? table.TableName.Name.ToUpperInvariant()
            : (schema + "." + table.TableName.Name).ToUpperInvariant();
    }

    public static TableBase? FindMatchingTable(IReadOnlyList<TableBase> publicTables, TableBase parsedTable)
    {
        var byFullName = publicTables.FirstOrDefault(t =>
            string.Equals(
                t.FullName.LowerInvariantSchemaName,
                parsedTable.FullName.LowerInvariantSchemaName,
                StringComparison.OrdinalIgnoreCase
            )
            && string.Equals(t.FullName.TableName, parsedTable.FullName.TableName, StringComparison.OrdinalIgnoreCase)
        );

        if (byFullName is not null)
        {
            return byFullName;
        }

        return publicTables.FirstOrDefault(t =>
            string.Equals(t.FullName.TableName, parsedTable.FullName.TableName, StringComparison.OrdinalIgnoreCase)
        );
    }

    public static string? BuildTableDifferenceMessage(TableBase expected, TableComparison comparison)
    {
        var parsedOnlyColumns = comparison.MissedColumns.ToList();
        var providedOnlyColumns = comparison.ExtraColumns.ToList();

        var changedColumns = new List<string>();
        foreach (var differentColumn in comparison.DifferentColumns.OrderBy(
                     i => i.Column.ColumnName.Name,
                     StringComparer.OrdinalIgnoreCase
                 ))
        {
            var relevantComparison = differentColumn.ColumnComparison & TableColumnComparison.DifferentName;
            if (relevantComparison != TableColumnComparison.Equal)
            {
                changedColumns.Add($"[{differentColumn.Column.ColumnName.Name}] ({relevantComparison})");
            }
        }

        var matchedProvidedOnlyIndexes = new HashSet<int>();
        var matchedParsedOnlyIndexes = new HashSet<int>();
        for (var m = 0; m < parsedOnlyColumns.Count; m++)
        {
            var parsedOnly = parsedOnlyColumns[m];
            var parsedOnlyLower = parsedOnly.ColumnName.Name.ToLowerInvariant();
            for (var e = 0; e < providedOnlyColumns.Count; e++)
            {
                if (matchedProvidedOnlyIndexes.Contains(e))
                {
                    continue;
                }

                var providedOnly = providedOnlyColumns[e];
                if (!string.Equals(
                        providedOnly.ColumnName.Name.ToLowerInvariant(),
                        parsedOnlyLower,
                        StringComparison.Ordinal
                    ))
                {
                    continue;
                }

                if (!string.Equals(providedOnly.ColumnName.Name, parsedOnly.ColumnName.Name, StringComparison.Ordinal))
                {
                    changedColumns.Add($"[{parsedOnly.ColumnName.Name}] ({TableColumnComparison.DifferentName})");
                }

                matchedProvidedOnlyIndexes.Add(e);
                matchedParsedOnlyIndexes.Add(m);
                break;
            }
        }

        var extraColumns = parsedOnlyColumns
            .Where((_, i) => !matchedParsedOnlyIndexes.Contains(i))
            .Select(i => $"[{i.ColumnName.Name}]")
            .OrderBy(i => i, StringComparer.OrdinalIgnoreCase)
            .ToList();

        changedColumns = changedColumns
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(i => i, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (extraColumns.Count < 1 && changedColumns.Count < 1)
        {
            return null;
        }

        var parts = new List<string>
        {
            FormatTableName(expected)
        };

        if (extraColumns.Count > 0)
        {
            parts.Add("extra columns: " + string.Join(", ", extraColumns));
        }

        if (changedColumns.Count > 0)
        {
            parts.Add("changed columns: " + string.Join(", ", changedColumns));
        }

        return string.Join(", ", parts);
    }

    public static IReadOnlyList<string> GetAvailableColumns(TableBase table)
    {
        return table.Columns
            .Select(c => c.ColumnName.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(c => c, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static string FormatTableName(TableBase table)
    {
        return $"[{table.FullName.LowerInvariantSchemaName}].[{table.FullName.TableName}]";
    }

    public static string FormatTableName(IExprTableFullName table)
    {
        var fullName = table.AsExprTableFullName();
        var schemaName = fullName.DbSchema?.Schema.Name ?? string.Empty;
        return string.IsNullOrWhiteSpace(schemaName)
            ? $"[{fullName.TableName.Name}]"
            : $"[{schemaName}].[{fullName.TableName.Name}]";
    }
}
