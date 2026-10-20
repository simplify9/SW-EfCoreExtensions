using System.Linq.Expressions;

namespace SW.EfCoreExtensions
{
    internal static partial class ExpressionExtensions
    {
        // string.StartsWith/Contains: LIKE on relational providers, case as the column's collation has it.
        private static partial Expression TextMatch(Expression field, string value, bool prefixOnly) =>
            Expression.Call(field,
                typeof(string).GetMethod(prefixOnly ? nameof(string.StartsWith) : nameof(string.Contains), new[] { typeof(string) }),
                SearchyValue.Captured(value, typeof(string)));
    }
}
