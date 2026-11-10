using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace SW.EfCoreExtensions
{
    /// <summary>Lists every field an <see cref="AuditModel"/> can write, for a committed, diffable listing.</summary>
    internal static class AuditVocabulary
    {
        public static IReadOnlyList<AuditVocabularyEntry> Describe(ResolvedAuditModel model)
        {
            var lines = new List<AuditVocabularyEntry>();
            foreach (var entity in model.Entities.OrderBy(e => e.Configuration.Order))
            {
                var fields = new SortedDictionary<string, string>(StringComparer.Ordinal);
                foreach (var type in entity.EntityType.GetDerivedTypesInclusive())
                {
                    foreach (var property in type.GetDeclaredProperties().Concat(type == entity.EntityType ? type.GetProperties() : Enumerable.Empty<IProperty>()))
                        if (entity.IsAudited(property, model.Configuration))
                            Add(fields, entity.Configuration.FieldName(property.Name), property);
                    foreach (var navigation in type.GetDeclaredNavigations().Where(n => n.ForeignKey.IsOwnership && n.TargetEntityType.IsOwned()))
                        Owned(fields, entity, model.Configuration, navigation, navigation.Name);
                }

                foreach (var root in entity.Paths.Select(p => p.Root.Name).Distinct())
                    foreach (var (field, kind) in fields)
                        lines.Add(new AuditVocabularyEntry(root, entity.Configuration.Name, field, kind));
            }
            return lines;
        }

        static void Owned(SortedDictionary<string, string> fields, ResolvedEntity owner, AuditModel configuration,
            INavigation navigation, string clrPrefix)
        {
            if (owner.Configuration.Ignored.Contains(clrPrefix)) return;
            var target = navigation.TargetEntityType;
            var path = navigation.IsCollection ? clrPrefix + "[]" : clrPrefix;
            foreach (var property in target.GetProperties())
            {
                if (property.IsPrimaryKey() || property.GetContainingForeignKeys().Any(fk => fk.IsOwnership)) continue;
                var clrPath = clrPrefix + "." + property.Name;
                if (configuration.IgnoredPropertyNames.Contains(property.Name) || owner.Configuration.Ignored.Contains(clrPath)) continue;
                var name = owner.Configuration.FieldName(clrPath);
                if (navigation.IsCollection)
                    name = owner.Configuration.FieldName(clrPrefix) + "[]." + name[(owner.Configuration.FieldName(clrPrefix).Length + 1)..];
                Add(fields, name, property);
            }
            foreach (var nested in target.GetNavigations().Where(n => n.ForeignKey.IsOwnership && n.TargetEntityType.IsOwned()))
                Owned(fields, owner, configuration, nested, path + "." + nested.Name);
        }

        static void Add(SortedDictionary<string, string> fields, string name, IProperty property)
        {
            var type = property.ClrType;
            if (Dictionary(type) is { } valueType)
            {
                fields[name + ".*"] = Kind(valueType) + "?";
                return;
            }
            fields[name] = Kind(type) + (property.IsNullable ? "?" : "");
        }

        static Type Dictionary(Type type)
        {
            var map = type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IDictionary<,>)
                ? type
                : type.GetInterfaces().FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IDictionary<,>));
            return map?.GetGenericArguments()[1];
        }

        internal static string Kind(Type type)
        {
            var t = Nullable.GetUnderlyingType(type) ?? type;
            if (t == typeof(string)) return "string";
            if (t == typeof(bool)) return "boolean";
            if (t == typeof(byte) || t == typeof(sbyte) || t == typeof(short) || t == typeof(ushort)
                || t == typeof(int) || t == typeof(uint) || t == typeof(long) || t == typeof(ulong)) return "integer";
            if (t == typeof(decimal) || t == typeof(double) || t == typeof(float)) return "number";
            if (t == typeof(DateTime) || t == typeof(DateTimeOffset) || t == typeof(DateOnly)) return "datetime";
            if (t == typeof(TimeSpan) || t == typeof(TimeOnly)) return "time";
            if (t == typeof(Guid)) return "uuid";
            if (t.IsEnum) return "enum";
            if (t == typeof(byte[])) return "bytes";
            if (Dictionary(t) is { } value) return $"map<{Kind(value)}>";
            if (typeof(IEnumerable).IsAssignableFrom(t))
            {
                var element = t.IsArray
                    ? t.GetElementType()
                    : t.GetInterfaces().Concat(new[] { t })
                        .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>))
                        ?.GetGenericArguments()[0];
                return $"list<{(element == null ? "object" : Kind(element))}>";
            }
            return "object";
        }
    }
}
