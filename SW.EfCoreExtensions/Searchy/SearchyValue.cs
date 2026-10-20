using System;
using System.Globalization;
using System.Linq.Expressions;

namespace SW.EfCoreExtensions
{
    /// <summary>
    /// Turns a filter value into the field's type and into an expression EF Core sends as a parameter.
    /// </summary>
    internal static class SearchyValue
    {
        /// <summary>
        /// The value as a member of a captured object, the shape of a closure variable. EF Core turns
        /// that into a SQL parameter, so one query shape compiles once and its plan is reused for every
        /// value. A constant would compile, cache and send a new SQL text per distinct value.
        /// </summary>
        public static Expression Captured(object value, Type type)
        {
            var converted = value is null || type.IsInstanceOfType(value) ? value : Convert(value, type);
            var holder = Activator.CreateInstance(typeof(Holder<>).MakeGenericType(type), converted);
            return Expression.Property(Expression.Constant(holder), nameof(Holder<object>.Value));
        }

        /// <summary>
        /// Converts with the invariant culture, so a value means the same on every server. Also covers
        /// Guid, enums by name or number, and booleans written as 1/0.
        /// </summary>
        public static object Convert(object value, Type type)
        {
            var target = Nullable.GetUnderlyingType(type) ?? type;
            var nullable = !type.IsValueType || Nullable.GetUnderlyingType(type) != null;

            if (value is null || value is string { Length: 0 } && nullable && target != typeof(string))
                return nullable ? null : Activator.CreateInstance(type);

            if (target.IsInstanceOfType(value))
                return value;

            var text = value as string ?? (value as IFormattable)?.ToString(null, CultureInfo.InvariantCulture) ?? value.ToString();

            if (target == typeof(Guid))
                return Guid.Parse(text);

            if (target.IsEnum)
                return Enum.Parse(target, text, ignoreCase: true);

            if (target == typeof(bool))
                return text switch
                {
                    "1" => true,
                    "0" => false,
                    _ => bool.Parse(text),
                };

            if (target == typeof(DateTime))
                // A UTC designator keeps the value in UTC rather than shifting it to server time.
                return value is DateTimeOffset offset
                    ? offset.UtcDateTime
                    : DateTime.Parse(text, CultureInfo.InvariantCulture,
                        text.EndsWith("Z", StringComparison.OrdinalIgnoreCase)
                            ? DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal
                            : DateTimeStyles.None);

            if (target == typeof(DateTimeOffset))
                return value is DateTime dateTime
                    ? new DateTimeOffset(dateTime)
                    : DateTimeOffset.Parse(text, CultureInfo.InvariantCulture);

            if (target == typeof(TimeSpan))
                return TimeSpan.Parse(text, CultureInfo.InvariantCulture);

            if (target == typeof(DateOnly))
                return DateOnly.Parse(text, CultureInfo.InvariantCulture);

            return System.Convert.ChangeType(value is IConvertible ? value : text, target, CultureInfo.InvariantCulture);
        }

        private sealed class Holder<T>
        {
            public Holder(T value) => Value = value;

            public T Value { get; }
        }
    }
}
