using System.Linq.Expressions;

namespace SW.EfCoreExtensions
{
    internal static partial class ExpressionExtensions
    {
        // string.StartsWith/Contains/EndsWith: LIKE on relational providers, case as the column's collation has it.
        private static partial Expression TextMatch(Expression field, string value, TextMatchKind kind) =>
            Expression.Call(field,
                typeof(string).GetMethod(kind switch
                {
                    TextMatchKind.Prefix => nameof(string.StartsWith),
                    TextMatchKind.Suffix => nameof(string.EndsWith),
                    _ => nameof(string.Contains),
                }, new[] { typeof(string) }),
                SearchyValue.Captured(value, typeof(string)));
    }
}
