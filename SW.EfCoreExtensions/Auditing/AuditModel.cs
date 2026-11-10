using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore.Metadata;

namespace SW.EfCoreExtensions
{
    /// <summary>
    /// Which entities are audited, grouped under which aggregate roots, and how they read. Built once
    /// per DbContext type and kept in a static field:
    /// <code>
    /// static readonly AuditModel Audit = AuditModel.Configure(a =>
    /// {
    ///     a.IgnoreProperties("CreatedOn", "CreatedBy", "ModifiedOn", "ModifiedBy");
    ///     a.SoftDelete("Deleted");
    ///     a.Root&lt;Order&gt;("order", key: o =&gt; o.Number, version: o =&gt; o.ChangeVersion);
    ///     a.Entity&lt;OrderLine&gt;("line").Label(l =&gt; l.Sku).MatchBy(l =&gt; l.Sku);
    /// });
    /// </code>
    /// </summary>
    /// <remarks>
    /// Only the listed entity types are audited. A listed entity that isn't a root is attached to the
    /// roots it reaches by following foreign keys from dependent to principal (the shortest path to
    /// each root), so children and grandchildren need no code of their own. Owned types are folded
    /// into their owner. This is configuration kept outside the EF model on purpose: model annotations
    /// would be written into every migration snapshot.
    /// </remarks>
    public sealed class AuditModel
    {
        readonly ConditionalWeakTable<IModel, ResolvedAuditModel> resolved = new();

        internal List<AuditEntityConfiguration> Entities { get; } = new();
        internal HashSet<string> IgnoredPropertyNames { get; } = new(StringComparer.Ordinal);
        internal string SoftDeleteProperty { get; set; }

        AuditModel() { }

        public static AuditModel Configure(Action<AuditModelBuilder> configure)
        {
            if (configure == null) throw new ArgumentNullException(nameof(configure));
            var model = new AuditModel();
            configure(new AuditModelBuilder(model));
            if (!model.Entities.Any(e => e.Root != null))
                throw new InvalidOperationException("An audit model needs at least one root.");
            return model;
        }

        internal ResolvedAuditModel Resolve(IModel model) =>
            resolved.GetValue(model, m => new ResolvedAuditModel(this, m));

        /// <summary>
        /// Every field the audit can write, per root and entity, with its kind (<c>string</c>,
        /// <c>integer</c>, <c>number?</c>, <c>map&lt;string&gt;</c>...). Commit this listing and diff it on
        /// pull requests: a removed or renamed field changes what history and change events say.
        /// </summary>
        public IReadOnlyList<AuditVocabularyEntry> Describe(IModel model) => AuditVocabulary.Describe(Resolve(model));
    }

    /// <summary>One line of <see cref="AuditModel.Describe"/>.</summary>
    public sealed record AuditVocabularyEntry(string Root, string Entity, string Field, string Kind)
    {
        public override string ToString() => $"{Root}\t{Entity}\t{Field}\t{Kind}";
    }

    public sealed class AuditModelBuilder
    {
        readonly AuditModel model;

        internal AuditModelBuilder(AuditModel model) => this.model = model;

        /// <summary>
        /// An aggregate root. <paramref name="key"/> is a business key written beside the primary key
        /// (a uid, a number). <paramref name="version"/> is a numeric column that
        /// <see cref="AuditCapture.BumpVersionsAsync"/> increases by one per change set.
        /// </summary>
        public AuditEntityBuilder<T> Root<T>(string name, Expression<Func<T, object>> key = null,
            Expression<Func<T, object>> version = null) where T : class
        {
            var entity = Add(typeof(T), name);
            entity.Root = new AuditRootConfiguration(entity)
            {
                KeyProperty = key == null ? null : MemberNames.Single(key),
                VersionProperty = version == null ? null : MemberNames.Single(version),
            };
            return new AuditEntityBuilder<T>(entity);
        }

        /// <summary>An audited entity under one or more roots. <paramref name="name"/> defaults to the camelCased type name.</summary>
        public AuditEntityBuilder<T> Entity<T>(string name = null) where T : class =>
            new(Add(typeof(T), name));

        /// <summary>Properties left out of every entity, by CLR name (audit columns, typically).</summary>
        public AuditModelBuilder IgnoreProperties(params string[] names)
        {
            foreach (var name in names) model.IgnoredPropertyNames.Add(name);
            return this;
        }

        /// <summary>
        /// A boolean property that marks a row as deleted. A change from false to true reads as
        /// <see cref="AuditOperation.Removed"/>.
        /// </summary>
        public AuditModelBuilder SoftDelete(string flagProperty)
        {
            model.SoftDeleteProperty = flagProperty;
            return this;
        }

        AuditEntityConfiguration Add(Type type, string name)
        {
            if (model.Entities.Any(e => e.ClrType == type))
                throw new InvalidOperationException($"{type.Name} is configured twice in the audit model.");
            var entity = new AuditEntityConfiguration(type, name ?? Naming.Camel(type.Name), model.Entities.Count);
            model.Entities.Add(entity);
            return entity;
        }
    }

    public sealed class AuditEntityBuilder<T> where T : class
    {
        readonly AuditEntityConfiguration entity;

        internal AuditEntityBuilder(AuditEntityConfiguration entity) => this.entity = entity;

        /// <summary>The properties whose values name a row on screen, joined by " / ".</summary>
        public AuditEntityBuilder<T> Label(Expression<Func<T, object>> properties)
        {
            entity.LabelProperties = MemberNames.Of(properties);
            return this;
        }

        /// <summary>
        /// The properties that identify a row when the application replaces rows instead of updating
        /// them. In one change set, a removed and an added row with the same values here are folded
        /// into one modification listing only the fields that differ. Use <c>x =&gt; new { x.A, x.B }</c>
        /// for several.
        /// </summary>
        public AuditEntityBuilder<T> MatchBy(Expression<Func<T, object>> properties)
        {
            entity.MatchProperties = MemberNames.Of(properties);
            return this;
        }

        /// <summary>Leaves properties out. Owned members by dotted CLR path: <c>"Consignee.Notes"</c>.</summary>
        public AuditEntityBuilder<T> Ignore(params string[] properties)
        {
            foreach (var property in properties) entity.Ignored.Add(property);
            return this;
        }

        public AuditEntityBuilder<T> Ignore(Expression<Func<T, object>> properties)
        {
            foreach (var property in MemberNames.Of(properties)) entity.Ignored.Add(property);
            return this;
        }

        /// <summary>
        /// Pins a property's (or owned navigation's) audit name, so a C# rename doesn't change what
        /// history says. Owned members by dotted CLR path: <c>"Consignee.City"</c>.
        /// </summary>
        public AuditEntityBuilder<T> FieldName(string property, string auditName)
        {
            entity.FieldNames[property] = auditName;
            return this;
        }

        public AuditEntityBuilder<T> FieldName(Expression<Func<T, object>> property, string auditName) =>
            FieldName(MemberNames.Single(property), auditName);
    }

    internal sealed class AuditEntityConfiguration
    {
        public AuditEntityConfiguration(Type clrType, string name, int order)
        {
            ClrType = clrType;
            Name = name;
            Order = order;
        }

        public Type ClrType { get; }
        public string Name { get; }
        public int Order { get; }
        public AuditRootConfiguration Root { get; set; }
        public IReadOnlyList<string> LabelProperties { get; set; } = Array.Empty<string>();
        public IReadOnlyList<string> MatchProperties { get; set; }
        public HashSet<string> Ignored { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, string> FieldNames { get; } = new(StringComparer.Ordinal);

        public string FieldName(string clrPath)
        {
            if (FieldNames.TryGetValue(clrPath, out var pinned)) return pinned;
            var dot = clrPath.LastIndexOf('.');
            return dot < 0
                ? Naming.Camel(clrPath)
                : FieldName(clrPath[..dot]) + "." + Naming.Camel(clrPath[(dot + 1)..]);
        }
    }

    internal sealed class AuditRootConfiguration
    {
        public AuditRootConfiguration(AuditEntityConfiguration entity) => Entity = entity;

        public AuditEntityConfiguration Entity { get; }
        public string Name => Entity.Name;
        public string KeyProperty { get; init; }
        public string VersionProperty { get; init; }
    }

    /// <summary>A way from an audited entity to one of its roots: the foreign keys to follow, in order.</summary>
    internal sealed record AuditRootPath(AuditRootConfiguration Root, IEntityType RootType, IReadOnlyList<IForeignKey> ForeignKeys);

    internal sealed class ResolvedEntity
    {
        readonly System.Collections.Concurrent.ConcurrentDictionary<IProperty, bool> audited = new();

        public AuditEntityConfiguration Configuration { get; init; }
        public IEntityType EntityType { get; init; }
        public IReadOnlyList<AuditRootPath> Paths { get; init; }

        /// <summary>
        /// Whether a property is written as a field. Never: keys, the foreign keys that lead to a root
        /// (they say where the row belongs, which the change set already says), shadow foreign keys,
        /// discriminators, store-generated concurrency tokens, the root's version, ignored properties.
        /// </summary>
        public bool IsAudited(IProperty property, AuditModel configuration) =>
            audited.GetOrAdd(property, p =>
                !(p.IsPrimaryKey()
                  || p.IsShadowProperty() && p.IsForeignKey()
                  || Paths.Any(path => path.ForeignKeys.Count > 0 && path.ForeignKeys[0].Properties.Contains(p))
                  || ((IEntityType)p.DeclaringType).FindDiscriminatorProperty() == p
                  || p.IsConcurrencyToken && p.ValueGenerated == ValueGenerated.OnAddOrUpdate
                  || Configuration.Root?.VersionProperty == p.Name
                  || configuration.IgnoredPropertyNames.Contains(p.Name)
                  || Configuration.Ignored.Contains(p.Name)));
    }

    internal sealed class ResolvedAuditModel
    {
        readonly Dictionary<IEntityType, ResolvedEntity> byEntityType = new();

        public AuditModel Configuration { get; }
        public IModel Model { get; }
        public IReadOnlyCollection<ResolvedEntity> Entities => byEntityType.Values;

        public ResolvedAuditModel(AuditModel configuration, IModel model)
        {
            Configuration = configuration;
            Model = model;

            var roots = new Dictionary<IEntityType, AuditRootConfiguration>();
            foreach (var entity in configuration.Entities.Where(e => e.Root != null))
            {
                var type = Find(entity.ClrType);
                roots[type] = entity.Root;
                Validate(type, entity.Root);
            }

            foreach (var entity in configuration.Entities)
            {
                var type = Find(entity.ClrType);
                var paths = entity.Root != null
                    ? new[] { new AuditRootPath(entity.Root, type, Array.Empty<IForeignKey>()) }
                    : FindPaths(type, roots);
                if (paths.Count == 0)
                    throw new InvalidOperationException(
                        $"{entity.ClrType.Name} is audited but no foreign key leads from it to a root " +
                        $"({string.Join(", ", roots.Keys.Select(r => r.ClrType.Name))}).");

                byEntityType[type] = new ResolvedEntity
                {
                    Configuration = entity,
                    EntityType = type,
                    Paths = paths,
                };
            }
        }

        /// <summary>The audited configuration for an entity type, or for its nearest audited base type.</summary>
        public ResolvedEntity Find(IEntityType type)
        {
            for (var t = type; t != null; t = t.BaseType)
                if (byEntityType.TryGetValue(t, out var entity))
                    return entity;
            return null;
        }

        IEntityType Find(Type clrType) =>
            Model.FindEntityType(clrType)
            ?? throw new InvalidOperationException($"{clrType.Name} is in the audit model but not in the EF model.");

        static void Validate(IEntityType type, AuditRootConfiguration root)
        {
            if (root.KeyProperty != null && type.FindProperty(root.KeyProperty) == null)
                throw new InvalidOperationException($"{type.ClrType.Name} has no property {root.KeyProperty}.");
            if (root.VersionProperty != null)
            {
                var version = type.FindProperty(root.VersionProperty)
                    ?? throw new InvalidOperationException($"{type.ClrType.Name} has no property {root.VersionProperty}.");
                var clr = Nullable.GetUnderlyingType(version.ClrType) ?? version.ClrType;
                if (clr != typeof(long) && clr != typeof(int))
                    throw new InvalidOperationException($"The version {type.ClrType.Name}.{root.VersionProperty} must be an int or a long.");
                if (type.FindPrimaryKey()?.Properties.Count != 1)
                    throw new InvalidOperationException($"A versioned root needs a single-column primary key ({type.ClrType.Name}).");
            }
        }

        /// <summary>Shortest foreign-key path from <paramref name="start"/> to each root, dependent to principal.</summary>
        static IReadOnlyList<AuditRootPath> FindPaths(IEntityType start, Dictionary<IEntityType, AuditRootConfiguration> roots)
        {
            var found = new List<AuditRootPath>();
            var seen = new HashSet<IEntityType> { start };
            var frontier = new List<(IEntityType Type, List<IForeignKey> Path)> { (start, new List<IForeignKey>()) };

            while (frontier.Count > 0)
            {
                var next = new List<(IEntityType, List<IForeignKey>)>();
                foreach (var (type, path) in frontier)
                    foreach (var fk in type.GetForeignKeys().Where(fk => !fk.IsOwnership))
                    {
                        var principal = fk.PrincipalEntityType;
                        var extended = new List<IForeignKey>(path) { fk };
                        var root = RootOf(principal, roots);
                        if (root != null)
                        {
                            if (found.All(p => p.Root != root.Value.Config))
                                found.Add(new AuditRootPath(root.Value.Config, root.Value.Type, extended));
                            continue;
                        }
                        if (seen.Add(principal))
                            next.Add((principal, extended));
                    }
                frontier = next;
            }
            return found;
        }

        static (AuditRootConfiguration Config, IEntityType Type)? RootOf(IEntityType type, Dictionary<IEntityType, AuditRootConfiguration> roots)
        {
            foreach (var candidate in roots)
                if (candidate.Key.IsAssignableFrom(type))
                    return (candidate.Value, candidate.Key);
            return null;
        }
    }

    internal static class Naming
    {
        public static string Camel(string name) =>
            string.IsNullOrEmpty(name) || char.IsLower(name[0]) ? name : char.ToLowerInvariant(name[0]) + name[1..];
    }

    internal static class MemberNames
    {
        public static string Single(LambdaExpression expression)
        {
            var names = Of(expression);
            if (names.Count != 1)
                throw new ArgumentException($"Expected one property, got: {expression}.");
            return names[0];
        }

        public static IReadOnlyList<string> Of(LambdaExpression expression)
        {
            var body = expression.Body is UnaryExpression { NodeType: ExpressionType.Convert } convert ? convert.Operand : expression.Body;
            return body switch
            {
                MemberExpression member => new[] { Path(member) },
                NewExpression created => created.Arguments.Select(a => a is MemberExpression m
                    ? Path(m)
                    : throw new ArgumentException($"Expected properties, got: {expression}.")).ToArray(),
                _ => throw new ArgumentException($"Expected a property or new {{ ... }} of properties, got: {expression}."),
            };
        }

        static string Path(MemberExpression member)
        {
            var parts = new List<string>();
            for (Expression e = member; e is MemberExpression m; e = m.Expression)
                parts.Insert(0, m.Member.Name);
            return string.Join(".", parts);
        }
    }
}
