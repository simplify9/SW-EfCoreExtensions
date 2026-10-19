using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using SW.PrimitiveTypes;

namespace SW.EfCoreExtensions
{
    public static class IQueryableOfTExtensions
    {

        async public static Task<IDictionary<string, string>> ToDictionaryAsync<TEntity>(this IQueryable<TEntity> query, SearchyRequest searchyRequest, Func<TEntity, string> keySelector, Func<TEntity, string> valueSelector = null)
        {
            return await query.
                Search(searchyRequest.Conditions).
                ToDictionaryAsync(keySelector, valueSelector ?? keySelector);
        }

        async public static Task<SearchyResponse<TEntity>> ToSearchyResponseAsync<TEntity>(this IQueryable<TEntity> query, SearchyRequest searchyRequest)
        {
            return new SearchyResponse<TEntity>
            {
                Result = await query.Search(searchyRequest.Conditions, searchyRequest.Sorts, searchyRequest.PageSize, searchyRequest.PageIndex).ToListAsync(),
                TotalCount = searchyRequest.CountRows ? await query.Search(searchyRequest.Conditions).CountAsync() : 0
            };
        }

        public static IQueryable<TEntity> Search<TEntity>(this IQueryable<TEntity> target, string field, object valueEquals)
        {
            return Search(target, new SearchyCondition[] { new SearchyCondition(field, SearchyRule.EqualsTo, valueEquals) });
        }

        public static IQueryable<TEntity> Search<TEntity>(this IQueryable<TEntity> target, string field, SearchyRule rule, object value)
        {
            return Search(target, new SearchyCondition[] { new SearchyCondition(field, rule, value) });
        }

        public static IQueryable<TEntity> Search<TEntity>(this IQueryable<TEntity> target, SearchyCondition condition)
        {
            return Search(target, new SearchyCondition[] { condition });
        }

        public static IQueryable<TEntity> Search<TEntity>(this IQueryable<TEntity> target,
            IEnumerable<SearchyCondition> conditions,
            IEnumerable<SearchySort> orders = null,
            int pageSize = 0,
            int pageIndex = 0)
        {

            var param = Expression.Parameter(typeof(TEntity), "TEntity");
            var finalexp = param.BuildConditionsExpression<TEntity>(conditions);

            if (finalexp != null)
            {
                var finalwhereexp = Expression.Lambda<Func<TEntity, bool>>(finalexp, param);
                target = target.Where(finalwhereexp);
            }

            return target.SortAndPage(orders, pageSize, pageIndex);
        }

        public static IQueryable<TEntity> Search<TEntity, TRelated>(this IQueryable<TEntity> target,
            string navigationProperty,
            IEnumerable<SearchyCondition> conditions,
            IEnumerable<SearchySort> orders = null,
            int pageSize = 0,
            int pageIndex = 0)
        {
            var param = Expression.Parameter(typeof(TEntity), "TEntity");
            var _parammany = Expression.Parameter(typeof(TRelated), "TRelated");
            var _finalexp = _parammany.BuildConditionsExpression<TRelated>(conditions);

            if (_finalexp != null)
            {
                var anyMethond = typeof(Enumerable).GetMethods().Where(m => m.Name == "Any" & m.GetParameters().Length == 2).Single().MakeGenericMethod(typeof(TRelated));
                var innerFunction = Expression.Lambda<Func<TRelated, bool>>(_finalexp, _parammany);
                var finalWherEexp = Expression.Lambda<Func<TEntity, bool>>(Expression.Call(anyMethond, Expression.Property(param, typeof(TEntity).GetProperty(navigationProperty, BindingFlags.IgnoreCase | BindingFlags.Public | BindingFlags.Instance)), innerFunction), new ParameterExpression[] { param });
                target = target.Where(finalWherEexp);
            }

            return target.SortAndPage(orders, pageSize, pageIndex);
        }

        static IQueryable<TEntity> SortAndPage<TEntity>(this IQueryable<TEntity> target,
            IEnumerable<SearchySort> orders, int pageSize, int pageIndex)
        {
            var first = true;
            foreach (var sort in orders ?? Enumerable.Empty<SearchySort>())
            {
                target = target.OrderByPath(sort.Field, sort.Sort == SearchySortOrder.DEC, thenBy: !first);
                first = false;
            }

            if (pageSize > 0 & pageIndex > 0)
                target = target.Skip(pageIndex * pageSize).Take(pageSize);
            else if (pageSize > 0)
                target = target.Take(pageSize);

            return target;
        }

        static readonly MethodInfo[] OrderingMethods = typeof(Queryable).GetMethods()
            .Where(m => m.GetParameters().Length == 2 && m.Name is nameof(Queryable.OrderBy) or nameof(Queryable.OrderByDescending)
                or nameof(Queryable.ThenBy) or nameof(Queryable.ThenByDescending))
            .ToArray();

        /// <summary>
        /// Orders by a property path ("Name", "Customer.Address.City") of any type.
        /// </summary>
        static IQueryable<TEntity> OrderByPath<TEntity>(this IQueryable<TEntity> query, string path, bool descending, bool thenBy)
        {
            var parameter = Expression.Parameter(typeof(TEntity), "e");
            var (member, type) = parameter.PropertyPath(typeof(TEntity), path);
            var name = (thenBy ? "ThenBy" : "OrderBy") + (descending ? "Descending" : "");
            var method = OrderingMethods.Single(m => m.Name == name).MakeGenericMethod(typeof(TEntity), type);
            return (IQueryable<TEntity>)method.Invoke(null,
                new object[] { query, Expression.Lambda(member, parameter) });
        }
    }
}
