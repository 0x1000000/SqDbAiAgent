using System.Diagnostics.CodeAnalysis;
using SqDbAiAgent.ConsoleApp.SecurityFilters.HarborFlow;
using SqExpress;
using SqExpress.Syntax;

namespace SqDbAiAgent.ConsoleApp.Services;

public interface ISecurityFilterFactoryService
{
    public bool TryCreateSecurityFilter(
        string databaseName,
        IReadOnlyList<TableBase> tableBases,
        [NotNullWhen(true)] out ISecurityFilter? emptyFilter,
        [NotNullWhen(false)] out string? error);
}

public class DefaultSecurityFilterFactoryService : ISecurityFilterFactoryService
{
    public bool TryCreateSecurityFilter(
        string databaseName,
        IReadOnlyList<TableBase> tableBases,
        [NotNullWhen(true)] out ISecurityFilter? securityFilter,
        [NotNullWhen(false)] out string? error)
    {
        if (string.Equals(databaseName, "HarborFlow", StringComparison.InvariantCultureIgnoreCase))
        {
            if (HarborFlowSecurityFilter.TryToCreate(tableBases, out var hbSecFilter, out error))
            {
                securityFilter = hbSecFilter;
                return true;
            }

            securityFilter = null;
            return false;
        }

        error = null;
        securityFilter = new VoidSecurityFilter(tableBases);
        return true;
    }
}

public interface ISecurityFilter
{
    IReadOnlyList<TableBase> GetPublicTables();

    IExprReadOnlyQuery? GetUsersQuery(string intUserIdAlias, string strDisplayNameAlias);

    bool ValidateQuery(
        IExpr expr,
        int? userId,
        [NotNullWhen(true)] out IExpr? result,
        [NotNullWhen(false)] out string? error);
}

public class VoidSecurityFilter(IReadOnlyList<TableBase> tables) : ISecurityFilter
{
    public IReadOnlyList<TableBase> GetPublicTables() => tables;

    public IExprReadOnlyQuery? GetUsersQuery(string intUserIdAlias, string strDisplayNameAlias) => null;

    public bool ValidateQuery(
        IExpr expr,
        int? userId,
        [NotNullWhen(true)] out IExpr? result,
        [NotNullWhen(false)] out string? error)
    {
        error = null;
        result = expr;
        return true;
    }
}
