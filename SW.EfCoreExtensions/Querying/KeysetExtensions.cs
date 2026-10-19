using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;

namespace SW.EfCoreExtensions
{
    /// <summary>One key of a keyset ordering: a lambda over the entity and its direction.</summary>
    public sealed record KeysetKey(LambdaExpression Key, bool Descending = false)
    {
        public static KeysetKey Asc<T, TKey>(Expression<Func<T, TKey>> key) => new(key);

        public static KeysetKey Desc<T, TKey>(Expression<Func<T, TKey>> key) => new(key, Descending: true);
    }

    /// <summary>The count of a query up to a limit, and whether there were more rows than that.</summary>
    public readonly record struct CountUpTo(int Count, bool Exceeded);

    /// <summary>
    /// Keyset ("seek") paging and cheap counts. A keyset page reads the next rows after the last row
    /// already seen, so a deep page costs the same as the first, unlike Skip, which reads and discards
    /// every row before it.
    /// </summary>
    public static class KeysetExtensions
    {
        /// <summary>
        /// Orders by <paramref name="keys"/>. The keys must identify a row, so end them with a unique
        /// column, the primary key for instance.
        /// </summary>
        public static IOrderedQueryable<T> OrderByKeyset<T>(this IQueryable<T> query, IReadOnlyList<KeysetKey> keys)
        {
            if (keys == null || keys.Count == 0)
                throw new ArgumentException("A keyset needs at least one key.", nameof(keys));

            IOrderedQueryable<T> ordered = null;
            foreach (var key in keys)
            {
                var lambda = Lambda<T>(key.Key);
                var name = (ordered == null ? "OrderBy" : "ThenBy") + (key.Descending ? "Descending" : "");
                var expression = Expression.Call(typeof(Queryable), name, new[] { typeof(T), lambda.ReturnType },
                    (ordered ?? query).Expression, Expression.Quote(lambda));
                ordered = (IOrderedQueryable<T>)query.Provider.CreateQuery<T>(expression);
            }
            return ordered;
        }

        /// <summary>
        /// The rows that come after the row whose key values are <paramref name="after"/>, in the
        /// order of <paramref name="keys"/>: (k1 &gt; v1) OR (k1 = v1 AND k2 &gt; v2) OR ..., with
        /// &lt; for descending keys. Apply before <see cref="OrderByKeyset{T}"/>. The values are sent
        /// as parameters. Keys can't be nullable: SQL comparisons with NULL match nothing, so rows
        /// would go missing.
        /// </summary>
        public static IQueryable<T> AfterKeyset<T>(this IQueryable<T> query, IReadOnlyList<KeysetKey> keys, IReadOnlyList<object> after)
        {
            if (after == null)
                return query;
            if (keys == null || keys.Count == 0 || after.Count != keys.Count)
                throw new ArgumentException("A keyset position needs one value per key.", nameof(after));

            var row = Expression.Parameter(typeof(T), "row");
            Expression predicate = null;

            for (var i = keys.Count - 1; i >= 0; i--)
            {
                var member = RowProjection.Inline(Lambda<T>(keys[i].Key), row);
                var value = SearchyValue.Captured(after[i], member.Type);
                var beyond = Compare(member, value, keys[i].Descending ? ExpressionType.LessThan : ExpressionType.GreaterThan);
                // Rows past this key, or level with it and past the keys that follow.
                predicate = predicate == null
                    ? beyond
                    : Expression.OrElse(beyond, Expression.AndAlso(Compare(member, value, ExpressionType.Equal), predicate));
            }

            return query.Where(Expression.Lambda<Func<T, bool>>(predicate, row));
        }

        /// <summary>
        /// Up to <paramref name="pageSize"/> rows, and whether more follow, from one query that reads
        /// one row beyond the page: no separate count.
        /// </summary>
        public static async Task<(List<T> Items, bool HasMore)> ToPageAsync<T>(this IQueryable<T> query, int pageSize,
            CancellationToken cancellationToken = default)
        {
            if (pageSize <= 0)
                throw new ArgumentOutOfRangeException(nameof(pageSize), "A page holds at least one row.");

            var items = await query.Take(pageSize + 1).ToListAsync(cancellationToken);
            var hasMore = items.Count > pageSize;
            if (hasMore)
                items.RemoveAt(pageSize);
            return (items, hasMore);
        }

        /// <summary>
        /// Counts rows up to <paramref name="limit"/>, reading at most one row beyond it: a list shows
        /// "10,000+" instead of paying for an exact count of millions.
        /// </summary>
        public static async Task<CountUpTo> CountUpToAsync<T>(this IQueryable<T> query, int limit,
            CancellationToken cancellationToken = default)
        {
            if (limit < 0)
                throw new ArgumentOutOfRangeException(nameof(limit));

            var count = await query.Take(limit + 1).CountAsync(cancellationToken);
            return count > limit ? new CountUpTo(limit, true) : new CountUpTo(count, false);
        }

        static LambdaExpression Lambda<T>(LambdaExpression key)
        {
            if (key.Parameters.Count != 1 || !key.Parameters[0].Type.IsAssignableFrom(typeof(T)))
                throw new ArgumentException($"A key is a lambda over {typeof(T).Name}.", nameof(key));
            if (Nullable.GetUnderlyingType(key.ReturnType) != null)
                throw new ArgumentException($"A keyset key can't be nullable ({key}).", nameof(key));
            return key;
        }

        // Strings and enums have no comparison operators in expression trees: strings go through
        // string.Compare, which EF Core translates to a plain comparison, enums through their
        // underlying type.
        static Expression Compare(Expression left, Expression right, ExpressionType comparison)
        {
            if (left.Type == typeof(string))
            {
                if (comparison == ExpressionType.Equal)
                    return Expression.Equal(left, right);
                var compare = Expression.Call(typeof(string).GetMethod(nameof(string.Compare), new[] { typeof(string), typeof(string) }), left, right);
                return Expression.MakeBinary(comparison, compare, Expression.Constant(0));
            }

            if (left.Type.IsEnum)
            {
                var underlying = Enum.GetUnderlyingType(left.Type);
                left = Expression.Convert(left, underlying);
                right = Expression.Convert(right, underlying);
            }

            if (left.Type == typeof(bool) || left.Type == typeof(Guid))
                throw new ArgumentException($"A keyset key of type {left.Type.Name} can't be compared in SQL; use another key.");

            return Expression.MakeBinary(comparison, left, right);
        }
    }
}
