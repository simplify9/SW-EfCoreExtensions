using SW.PrimitiveTypes;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;

namespace SW.EfCoreExtensions
{
    // Shared by both packages; each package supplies TextMatch (string methods in the core package,
    // ILIKE in the PostgreSQL one).
    internal static partial class ExpressionExtensions
    {
        public static Expression BuildConditionsExpression<TEntity>(this Expression parameter, IEnumerable<SearchyCondition> searchyConditions)
        {
            Expression finalExpression = null;

            foreach (var searchyCondition in searchyConditions)
            {
                Expression conditionExpression = null;

                foreach (var searchyFilter in searchyCondition.Filters.Where(f => f.IsValid()))
                {
                    var filterExpression = parameter.BuildFilterExpression<TEntity>(searchyFilter);

                    if (conditionExpression == null)
                        conditionExpression = filterExpression;

                    else if (filterExpression != null)
                        conditionExpression = Expression.AndAlso(conditionExpression, filterExpression);
                }

                if (finalExpression == null)
                    finalExpression = conditionExpression;
                else if (conditionExpression != null)
                    finalExpression = Expression.OrElse(finalExpression, conditionExpression);
            }

            return finalExpression;
        }

        /// <summary>
        /// The member access for a dotted path ("Customer.Address.City"), matched case-insensitively,
        /// and the path's type.
        /// </summary>
        public static (Expression Member, Type Type) PropertyPath(this Expression parameter, Type rootType, string path)
        {
            var member = parameter;
            var type = rootType;

            foreach (var segment in path.Split(new[] { '.' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var property = type.GetProperty(segment, BindingFlags.IgnoreCase | BindingFlags.Public | BindingFlags.Instance)
                    ?? throw new SWException($"'{segment}' is not a property of {type.Name} (in '{path}').");
                member = Expression.Property(member, property);
                type = property.PropertyType;
            }

            return (member, type);
        }

        static Expression BuildFilterExpression<TEntity>(this Expression parameter, ISearchyFilter filter)
        {
            var (fieldNameExpression, fieldType) = parameter.PropertyPath(typeof(TEntity), filter.Field);

            switch (filter.Rule)
            {
                case SearchyRule.StartsWith:
                    return TextMatch(fieldNameExpression, $"{filter.Value}", prefixOnly: true);

                case SearchyRule.Contains:
                    return TextMatch(fieldNameExpression, $"{filter.Value}", prefixOnly: false);

                case SearchyRule.EqualsTo:
                    return Expression.Equal(fieldNameExpression, SearchyValue.Captured(filter.Value, fieldType));

                case SearchyRule.NotEqualsTo:
                    return Expression.NotEqual(fieldNameExpression, SearchyValue.Captured(filter.Value, fieldType));

                case SearchyRule.LessThan:
                    return Expression.LessThan(fieldNameExpression, SearchyValue.Captured(filter.Value, fieldType));

                case SearchyRule.LessThanOrEquals:
                    return Expression.LessThanOrEqual(fieldNameExpression, SearchyValue.Captured(filter.Value, fieldType));

                case SearchyRule.GreaterThan:
                    return Expression.GreaterThan(fieldNameExpression, SearchyValue.Captured(filter.Value, fieldType));

                case SearchyRule.GreaterThanOrEquals:
                    return Expression.GreaterThanOrEqual(fieldNameExpression, SearchyValue.Captured(filter.Value, fieldType));

                case SearchyRule.Range:
                    var rangeValues = PopulateRangeValues(filter.Value);
                    var lowerRangeExpression = Expression.GreaterThanOrEqual(fieldNameExpression, SearchyValue.Captured(rangeValues.Lower, fieldType));
                    var upperRangeExpression = Expression.LessThan(fieldNameExpression, SearchyValue.Captured(rangeValues.Upper, fieldType));
                    return Expression.AndAlso(lowerRangeExpression, upperRangeExpression);

                case SearchyRule.EqualsToList:
                    var list = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(fieldType));
                    foreach (var item in (IEnumerable)filter.Value)
                        list.Add(SearchyValue.Convert(item, fieldType));
                    var containsMethod = typeof(Enumerable).GetMethods()
                        .First(o => o.Name == nameof(Enumerable.Contains) && o.GetParameters().Length == 2)
                        .MakeGenericMethod(fieldType);
                    return Expression.Call(containsMethod, SearchyValue.Captured(list, list.GetType()), fieldNameExpression);

                default:
                    return null;
            }
        }

        /// <summary>
        /// The text rules, which differ by provider. <paramref name="value"/> is the raw search text;
        /// the implementation must pass it as a captured value, never as a constant.
        /// </summary>
        private static partial Expression TextMatch(Expression field, string value, bool prefixOnly);

        static RangeValues PopulateRangeValues(object valueCollection)
        {
            var rangeValues = new RangeValues();
            int index = 0;
            foreach (var item in (IEnumerable)valueCollection)
            {
                if (index == 0)
                    rangeValues.Lower = item;
                else if (index == 1)
                    rangeValues.Upper = item;

                index++;
            }

            if (index != 2)
                throw new ArgumentException("Range rule value collection should contain exactly two values.");

            return rangeValues;
        }

        private class RangeValues
        {
            public object Lower { get; set; }
            public object Upper { get; set; }
        }
    }
}
