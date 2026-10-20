using System;
using System.Linq.Expressions;

namespace SW.EfCoreExtensions
{
    /// <summary>How a text match anchors the search text.</summary>
    public enum TextMatchKind
    {
        Contains,
        Prefix,
        Suffix,
    }

    /// <summary>
    /// Building blocks for code that builds its own filter expressions: values as SQL parameters, and
    /// the package's text match (case-insensitive ILIKE in the PostgreSQL package).
    /// </summary>
    public static class QueryExpressions
    {
        /// <summary>
        /// <paramref name="value"/>, converted to <paramref name="type"/>, as an expression EF Core sends
        /// as a parameter rather than a literal, so a query shape compiles once for every value.
        /// </summary>
        public static Expression Parameter(object value, Type type) => SearchyValue.Captured(value, type);

        /// <summary>
        /// Converts <paramref name="value"/> to <paramref name="type"/> with the invariant culture,
        /// covering Guid, enums by name or number and booleans written as 1/0.
        /// </summary>
        public static object Convert(object value, Type type) => SearchyValue.Convert(value, type);

        /// <summary>
        /// <paramref name="field"/> (a string expression) matching <paramref name="text"/>, with
        /// wildcard characters in the text matched literally.
        /// </summary>
        public static Expression TextMatch(Expression field, string text, TextMatchKind kind) =>
            ExpressionExtensions.ProviderTextMatch(field, text, kind);
    }
}
