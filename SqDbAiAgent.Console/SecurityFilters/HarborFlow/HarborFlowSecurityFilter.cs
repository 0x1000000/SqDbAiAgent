using System.Diagnostics.CodeAnalysis;
using SqDbAiAgent.ConsoleApp.SecurityFilters.HarborFlow.Tables;
using SqDbAiAgent.ConsoleApp.Services;
using SqExpress;
using SqExpress.Syntax;
using SqExpress.Syntax.Boolean;
using SqExpress.Syntax.Names;
using SqExpress.Syntax.Select;

namespace SqDbAiAgent.ConsoleApp.SecurityFilters.HarborFlow;

public class HarborFlowSecurityFilter(IReadOnlyList<TableBase> realTables, TablesGraph tablesGraph) : ISecurityFilter
{
    // Only business-facing operational/reference tables are exposed to the LLM prompt.
    private readonly IReadOnlyList<TableBase> _publicTables =
        realTables.Where(t => t.FullName.LowerInvariantSchemaName is "ref" or "ops").ToList();

    internal static bool TryToCreate(IReadOnlyList<TableBase> realTables, [NotNullWhen(true)] out HarborFlowSecurityFilter? securityFilter,[NotNullWhen(false)] out string? error)
    {
        if (!realTables.Includes(AllTables.StaticList, TableIncludesFlags.IgnoreColumnMeta))
        {
            securityFilter = null;
            error = "The database schema does not match the expected";
        }

        if (!TablesGraph.TryCreate(realTables, out TablesGraph? graph, out error))
        {
            securityFilter = null;
            error ??= "Could not create the tables graph for the database schema";
            return false;
        }

        error = null;
        securityFilter = new HarborFlowSecurityFilter(realTables, graph);

        return true;
    }

    public IReadOnlyList<TableBase> GetPublicTables()
    {
        return this._publicTables;
    }


    public IExprReadOnlyQuery GetUsersQuery(string intUserIdAlias, string strDisplayNameAlias)
    {
        var tbl = AllTables.GetAppUser();

        // The startup user-selection list comes from active application users only.
        return SqQueryBuilder
            .Select(tbl.AppUserId.As(intUserIdAlias), tbl.DisplayName.As(strDisplayNameAlias))
            .From(tbl)
            .Where(tbl.IsActive == true)
            .OrderBy(SqQueryBuilder.Desc(tbl.CreatedUtc))
            .Done();
    }

    public bool ValidateQuery(IExpr expr, int? userId, [NotNullWhen(true)] out IExpr? result, [NotNullWhen(false)] out string? error)
    {
        // No selected user means no row-level security context, so the query is allowed unchanged.
        if (!userId.HasValue)
        {
            result = expr;
            error = null;
            return true;
        }

        if (expr is IExprReadOnlyQuery readOnlyQuery)
        {
            string? localError = null;

            var userBranchAccess = AllTables.GetUserBranchAccess();
            var userCustomerAccess = AllTables.GetUserCustomerAccess();
            var appUser = AllTables.GetAppUser();

            var newQuery = readOnlyQuery.SyntaxTree()
                .Modify<ExprQuerySpecification>(querySpec =>
                    {
                        // Security is applied to each visited query specification by extending its WHERE clause.
                        var tables = querySpec.From?.ToTableMultiplication();

                        // Build per-table EXISTS predicates so the final query only returns
                        // rows reachable through the selected user's allowed branch/customer scope.
                        List<ExprBoolean> securityPredicates = [];
                        if (tables?.Tables.Count > 0)
                        {
                            foreach (var t in tables.Value.Tables)
                            {
                                if (t is ExprTable queryTable && tablesGraph.TryGetTable(
                                        queryTable,
                                        out var tableDescriptor
                                    ))
                                {
                                    // Reject any direct access to non-public tables even if the parser accepted them.
                                    if (!this._publicTables.Select(x => x.FullName).Contains(queryTable.FullName))
                                    {
                                        localError = "Request contains access to private tables";
                                        return querySpec;
                                    }

                                    var subTb = queryTable.WithAlias(SqQueryBuilder.TableAlias());

                                    // Match the security subquery row back to the original query row by primary key.
                                    var subCompare = TryCompareByPk(subTb, queryTable, tableDescriptor);
                                    if (subCompare == null)
                                    {
                                        localError = "Restricted table does not have a primary key";
                                        return querySpec;
                                    }

                                    // If the current table can be connected to AppUser through branch access,
                                    // add an EXISTS check for that allowed path.
                                    if (tablesGraph.TryToJoinTables(subTb, appUser, [userBranchAccess], out var join))
                                    {
                                        securityPredicates.Add(
                                            SqQueryBuilder.Exists(
                                                SqQueryBuilder.SelectOne()
                                                    .From(join)
                                                    .Where(
                                                        subCompare
                                                        & (appUser.AppUserId == userId.Value)
                                                        & (userBranchAccess.CanRead == true)
                                                    )
                                            )
                                        );
                                    }

                                    // If the current table can also be connected through customer access,
                                    // allow that path as well. Multiple predicates are later combined with AND.
                                    if (tablesGraph.TryToJoinTables(
                                            subTb,
                                            appUser,
                                            [userCustomerAccess],
                                            out var join2
                                        ))
                                    {
                                        securityPredicates.Add(
                                            SqQueryBuilder.Exists(
                                                SqQueryBuilder.SelectOne()
                                                    .From(join2)
                                                    .Where(
                                                        subCompare
                                                        & (appUser.AppUserId == userId.Value)
                                                        & (userCustomerAccess.CanRead == true)
                                                    )
                                            )
                                        );
                                    }
                                }
                            }
                        }

                        if (securityPredicates.Count > 0)
                        {
                            // Preserve the original WHERE clause and add the security checks on top of it.
                            var newWhere = securityPredicates.JoinAsAnd();
                            if (querySpec.Where != null)
                            {
                                newWhere = querySpec.Where & newWhere;
                            }

                            var updatedSec = querySpec.WithWhere(newWhere);

                            return updatedSec;
                        }

                        // If no security path was discovered for the referenced tables, leave the query as-is.
                        return querySpec;

                        static ExprBoolean? TryCompareByPk(ExprTable t1, ExprTable t2, TableBase tableDescriptor)
                        {
                            // Security predicates are attached through PK equality so the EXISTS
                            // clause applies to the exact row referenced by the outer query.
                            var pk = tableDescriptor.Columns.Where(c => c.ColumnMeta?.IsPrimaryKey ?? false).ToList();
                            if (pk.Count < 1)
                            {
                                return null;
                            }

                            ExprBoolean result = RedirectColumn(pk[0], t1) == RedirectColumn(pk[0], t2);
                            for (int i = 1; i < pk.Count; i++)
                            {
                                result &= RedirectColumn(pk[i], t1) == RedirectColumn(pk[i], t2);
                            }

                            return result;

                            static TableColumn RedirectColumn(TableColumn tableColumn, ExprTable t)
                                => tableColumn.WithSource(
                                    t.Alias ?? (IExprColumnSource)t.FullName.AsExprTableFullName()
                                );
                        }
                    }
                );

            if (localError != null)
            {
                error = localError;
                result = null;
                return false;
            }

            error = null;
            result = newQuery!;
            return true;
        }

        result = null;
        error = "Only read access is allowed";
        return false;
    }
}
