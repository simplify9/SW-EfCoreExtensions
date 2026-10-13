using Microsoft.EntityFrameworkCore;
using SW.PrimitiveTypes;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Text;

namespace SW.EfCoreExtensions
{
    internal static class ExpressionExtensions
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

        static Expression BuildFilterExpression<TEntity>(this Expression parameter, ISearchyFilter filter)
        {
            var fieldSegments = filter.Field.Split(new[] { '.' }, StringSplitOptions.RemoveEmptyEntries);
            var fieldNameExpression = parameter;
            var fieldType = typeof(TEntity);

            foreach (var fieldSegment in fieldSegments)
            {
                fieldNameExpression = Expression.Property(fieldNameExpression, fieldSegment);
                fieldType = fieldType.GetProperty(fieldSegment, BindingFlags.IgnoreCase | BindingFlags.Public | BindingFlags.Instance).PropertyType;
            }

            switch (filter.Rule)
            {
                case SearchyRule.StartsWith:
                    return BuildILikeExpression(fieldNameExpression, $"{EscapeLikePattern(filter.Value)}%");

                case SearchyRule.Contains:
                    return BuildILikeExpression(fieldNameExpression, $"%{EscapeLikePattern(filter.Value)}%");

                case SearchyRule.EqualsTo:
                    return Expression.Equal(fieldNameExpression, GetValueAsConstantExpression(filter.Value, fieldType));

                case SearchyRule.NotEqualsTo:
                    return Expression.NotEqual(fieldNameExpression, GetValueAsConstantExpression(filter.Value, fieldType));

                case SearchyRule.LessThan:
                    return Expression.LessThan(fieldNameExpression, GetValueAsConstantExpression(filter.Value, fieldType));

                case SearchyRule.LessThanOrEquals:
                    return Expression.LessThanOrEqual(fieldNameExpression, GetValueAsConstantExpression(filter.Value, fieldType));

                case SearchyRule.GreaterThan:
                    return Expression.GreaterThan(fieldNameExpression, GetValueAsConstantExpression(filter.Value, fieldType));

                case SearchyRule.GreaterThanOrEquals:
                    return Expression.GreaterThanOrEqual(fieldNameExpression, GetValueAsConstantExpression(filter.Value, fieldType));

                case SearchyRule.Range:
                    var rangeValues = PopulateRangeValues(filter.Value);
                    var lowerRangeExpression = Expression.GreaterThanOrEqual(fieldNameExpression, GetValueAsConstantExpression(rangeValues.Lower, fieldType));
                    var upperRangeExpression = Expression.LessThan(fieldNameExpression, GetValueAsConstantExpression(rangeValues.Upper, fieldType));
                    return Expression.AndAlso(lowerRangeExpression, upperRangeExpression);

                case SearchyRule.EqualsToList:
                    var containsMethod = typeof(Enumerable).GetMethods().Where(o => o.Name == "Contains" & o.GetParameters().Count() == 2).First();
                    containsMethod = containsMethod.MakeGenericMethod(fieldType);
                    var equalToListListType = typeof(List<>).MakeGenericType(new[] { fieldType });
                    var addMethod = equalToListListType.GetMethod("Add");
                    var equalToListList = Activator.CreateInstance(equalToListListType);
                    foreach (var item in (IEnumerable)filter.Value)
                        addMethod.Invoke(equalToListList, new object[] { item.ConvertValueToType(fieldType) });
                    //filter.Value = equalToList;
                    return Expression.Call(containsMethod, new[] { Expression.Constant(equalToListList), fieldNameExpression });

                default:
                    return null;
            }
        }

        // The value is escaped so a '%' or '_' in it matches literally, as string.Contains/StartsWith
        // do. The escape character has to be passed explicitly: ILike without one makes Npgsql emit
        // ESCAPE '', which turns escaping off altogether.
        const string LikeEscapeCharacter = "\\";

        static Expression BuildILikeExpression(Expression fieldNameExpression, string pattern) =>
            Expression.Call(
                typeof(NpgsqlDbFunctionsExtensions),
                nameof(NpgsqlDbFunctionsExtensions.ILike),
                Type.EmptyTypes,
                Expression.Property(null, typeof(EF), nameof(EF.Functions)),
                fieldNameExpression,
                Expression.Constant(pattern),
                Expression.Constant(LikeEscapeCharacter)
            );

        static string EscapeLikePattern(object value) =>
            $"{value}"
                .Replace(LikeEscapeCharacter, LikeEscapeCharacter + LikeEscapeCharacter)
                .Replace("%", LikeEscapeCharacter + "%")
                .Replace("_", LikeEscapeCharacter + "_");

        static Expression GetValueAsConstantExpression(object value, Type type)
        {
            Expression constantExpression = Expression.Constant(value.ConvertValueToType(type));
            if (Nullable.GetUnderlyingType(type) != null)
                constantExpression = Expression.Convert(constantExpression, type);
            return constantExpression;
        }

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
