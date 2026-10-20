using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;

namespace SW.EfCoreExtensions
{
    /// <summary>
    /// Builds a projection to <c>object[]</c> rows from a list of column expressions, so a query reads
    /// exactly the columns a caller asked for. The shape depends only on which columns are chosen,
    /// never on values, so EF Core compiles each distinct choice of columns once.
    /// </summary>
    /// <example>
    /// <code>
    /// var row = RowProjection.Of&lt;Order&gt;(
    ///     (Expression&lt;Func&lt;Order, string&gt;&gt;)(o =&gt; o.Number),
    ///     (Expression&lt;Func&lt;Order, string&gt;&gt;)(o =&gt; o.Customer.Name),
    ///     RowProjection.Many&lt;Order, OrderLine&gt;(o =&gt; o.Lines, take: 10,
    ///         (Expression&lt;Func&lt;OrderLine, string&gt;&gt;)(l =&gt; l.Sku)));
    /// var rows = await db.Orders.Select(row).ToListAsync();   // [["ORD-1", "Contoso", [["SKU-1"]]], ...]
    /// </code>
    /// </example>
    public static class RowProjection
    {
        /// <summary>
        /// A row of <paramref name="columns"/>, each a lambda over <typeparamref name="T"/>, in order.
        /// Use <see cref="Many{T,TChild}"/> for a column holding a collection's rows.
        /// </summary>
        public static Expression<Func<T, object[]>> Of<T>(params LambdaExpression[] columns) =>
            Of<T>((IEnumerable<LambdaExpression>)columns);

        /// <inheritdoc cref="Of{T}(LambdaExpression[])"/>
        public static Expression<Func<T, object[]>> Of<T>(IEnumerable<LambdaExpression> columns)
        {
            var row = Expression.Parameter(typeof(T), "row");
            return Expression.Lambda<Func<T, object[]>>(
                Expression.NewArrayInit(typeof(object), columns.Select(c => Expression.Convert(Inline(c, row), typeof(object)))),
                row);
        }

        /// <summary>
        /// A column holding the rows of a collection: <paramref name="childColumns"/> for each element
        /// of <paramref name="collection"/>, in <paramref name="orderBy"/> order, at most
        /// <paramref name="take"/> of them when given.
        /// </summary>
        public static Expression<Func<T, List<object[]>>> Many<T, TChild>(
            Expression<Func<T, IEnumerable<TChild>>> collection,
            int? take,
            params LambdaExpression[] childColumns) =>
            Many(collection, take, orderBy: null, descending: false, childColumns);

        /// <inheritdoc cref="Many{T,TChild}(Expression{Func{T,IEnumerable{TChild}}},int?,LambdaExpression[])"/>
        public static Expression<Func<T, List<object[]>>> Many<T, TChild>(
            Expression<Func<T, IEnumerable<TChild>>> collection,
            int? take,
            LambdaExpression orderBy,
            bool descending,
            params LambdaExpression[] childColumns)
        {
            Expression children = collection.Body;

            if (orderBy != null)
                children = Expression.Call(typeof(Enumerable), descending ? nameof(Enumerable.OrderByDescending) : nameof(Enumerable.OrderBy),
                    new[] { typeof(TChild), orderBy.ReturnType }, children, orderBy);

            if (take != null)
                children = Expression.Call(typeof(Enumerable), nameof(Enumerable.Take), new[] { typeof(TChild) },
                    children, Expression.Constant(take.Value));

            var rows = Expression.Call(typeof(Enumerable), nameof(Enumerable.Select), new[] { typeof(TChild), typeof(object[]) },
                children, Of<TChild>(childColumns));
            var list = Expression.Call(typeof(Enumerable), nameof(Enumerable.ToList), new[] { typeof(object[]) }, rows);

            return Expression.Lambda<Func<T, List<object[]>>>(list, collection.Parameters);
        }

        /// <summary>The body of <paramref name="lambda"/> with its parameter replaced by <paramref name="argument"/>.</summary>
        internal static Expression Inline(LambdaExpression lambda, Expression argument)
        {
            if (lambda.Parameters.Count != 1)
                throw new ArgumentException("A column is a lambda with exactly one parameter.", nameof(lambda));
            if (!lambda.Parameters[0].Type.IsAssignableFrom(argument.Type))
                throw new ArgumentException($"A column over {lambda.Parameters[0].Type.Name} can't be read from {argument.Type.Name}.", nameof(lambda));

            return new ReplaceParameter(lambda.Parameters[0], argument).Visit(lambda.Body);
        }

        private sealed class ReplaceParameter(ParameterExpression from, Expression to) : ExpressionVisitor
        {
            protected override Expression VisitParameter(ParameterExpression node) => node == from ? to : node;
        }
    }
}
