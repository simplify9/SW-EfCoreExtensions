using System;
using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;

namespace SW.EfCoreExtensions
{
    internal static partial class ExpressionExtensions
    {
        // The value is escaped so a '%' or '_' in it matches literally, as string.Contains/StartsWith
        // do. The escape character has to be passed explicitly: ILike without one makes Npgsql emit
        // ESCAPE '', which turns escaping off altogether.
        const string LikeEscapeCharacter = "\\";

        // ILIKE: case-insensitive, and served by pg_trgm indexes like LIKE is.
        private static partial Expression TextMatch(Expression field, string value, TextMatchKind kind) =>
            Expression.Call(
                typeof(NpgsqlDbFunctionsExtensions),
                nameof(NpgsqlDbFunctionsExtensions.ILike),
                Type.EmptyTypes,
                Expression.Property(null, typeof(EF), nameof(EF.Functions)),
                field,
                SearchyValue.Captured(kind switch
                {
                    TextMatchKind.Prefix => $"{EscapeLikePattern(value)}%",
                    TextMatchKind.Suffix => $"%{EscapeLikePattern(value)}",
                    _ => $"%{EscapeLikePattern(value)}%",
                }, typeof(string)),
                Expression.Constant(LikeEscapeCharacter)
            );

        static string EscapeLikePattern(string value) =>
            value
                .Replace(LikeEscapeCharacter, LikeEscapeCharacter + LikeEscapeCharacter)
                .Replace("%", LikeEscapeCharacter + "%")
                .Replace("_", LikeEscapeCharacter + "_");
    }
}
