using SqExpress;
using SqExpress.Syntax.Names;
using SqExpress.Syntax.Select;
using SqExpress.Syntax.Value;

namespace SqDbAiAgent.ConsoleApp.Services.Sql;

internal static class SqlQueryLimiter
{
    public static SqlQueryLimitResult ApplyDefault(IExprReadOnlyQuery query, int defaultLimit)
    {
        if (defaultLimit < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(defaultLimit));
        }

        return query switch
        {
            ExprQuerySpecification specification => LimitSpecification(specification, defaultLimit),
            ExprSelect select => LimitOrderedSelect(select, defaultLimit),
            ExprSelectOffsetFetch => new SqlQueryLimitResult(query, false),
            ExprQueryExpression expression => new SqlQueryLimitResult(WrapCompound(expression, defaultLimit), true),
            _ => throw new NotSupportedException(
                $"Read-only query type '{query.GetType().Name}' cannot be row-limited safely.")
        };
    }

    public static IExprReadOnlyQuery ReplaceAppliedDefault(
        IExprReadOnlyQuery query,
        int replacementLimit)
    {
        if (replacementLimit < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(replacementLimit));
        }

        return query switch
        {
            ExprQuerySpecification specification => CopySpecification(specification, replacementLimit),
            ExprSelect { SelectQuery: ExprQuerySpecification specification } select =>
                new ExprSelect(CopySpecification(specification, replacementLimit), select.OrderBy),
            ExprSelectOffsetFetch select => new ExprSelectOffsetFetch(
                select.SelectQuery,
                new ExprOrderByOffsetFetch(
                    select.OrderBy.OrderList,
                    new ExprOffsetFetch(new ExprInt32Literal(0), new ExprInt32Literal(replacementLimit)))),
            _ => query
        };
    }

    private static SqlQueryLimitResult LimitSpecification(
        ExprQuerySpecification specification,
        int defaultLimit) =>
        specification.Top is null
            ? new SqlQueryLimitResult(CopySpecification(specification, defaultLimit), true)
            : new SqlQueryLimitResult(specification, false);

    private static SqlQueryLimitResult LimitOrderedSelect(ExprSelect select, int defaultLimit)
    {
        if (select.SelectQuery is ExprQuerySpecification specification)
        {
            var limited = LimitSpecification(specification, defaultLimit);
            return limited.Applied
                ? new SqlQueryLimitResult(new ExprSelect((IExprSubQuery)limited.Query, select.OrderBy), true)
                : new SqlQueryLimitResult(select, false);
        }

        return new SqlQueryLimitResult(
            new ExprSelectOffsetFetch(
                select.SelectQuery,
                new ExprOrderByOffsetFetch(
                    select.OrderBy.OrderList,
                    new ExprOffsetFetch(new ExprInt32Literal(0), new ExprInt32Literal(defaultLimit)))),
            true);
    }

    private static ExprQuerySpecification CopySpecification(
        ExprQuerySpecification source,
        int top) =>
        new(
            source.SelectList,
            new ExprInt32Literal(top),
            source.Distinct,
            source.From,
            source.Where,
            source.GroupBy);

    private static ExprQuerySpecification WrapCompound(
        ExprQueryExpression expression,
        int defaultLimit)
    {
        var alias = new ExprTableAlias(new ExprAlias("__row_limit"));
        var derived = new ExprDerivedTableQuery(expression, alias, null);
        return new ExprQuerySpecification(
            [new ExprAllColumns(alias)],
            new ExprInt32Literal(defaultLimit),
            false,
            derived,
            null,
            null);
    }
}

internal readonly record struct SqlQueryLimitResult(IExprReadOnlyQuery Query, bool Applied);
