using System;
using System.Collections;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage;
using SW.PrimitiveTypes;

namespace SW.EfCoreExtensions
{
    public static class AuditCaptureExtensions
    {
        /// <summary>
        /// Captures what the pending changes do to each aggregate root of <paramref name="model"/>.
        /// Call it right before <c>SaveChanges</c>, after anything that still edits entities (audit
        /// columns, soft deletion). It only reads, except for one query per entity type when an
        /// audited entity's parent isn't loaded and its root can't be told from its foreign keys.
        /// </summary>
        public static Task<AuditCapture> CaptureChangeSetsAsync(this ChangeTracker changeTracker, AuditModel model,
            CancellationToken cancellationToken = default) =>
            AuditCapture.CaptureAsync(changeTracker, model, cancellationToken);
    }

    /// <summary>
    /// The change sets of one <c>SaveChanges</c>, grouped by aggregate root. See
    /// <see cref="AuditCaptureExtensions.CaptureChangeSetsAsync"/>.
    /// </summary>
    /// <remarks>
    /// Keys that the database generates (identity columns) are only known after the save. With
    /// HiLo, sequences or client-generated keys, <see cref="Complete"/> works before the save, so the
    /// change sets can be written in the same transaction as the data.
    /// </remarks>
    public sealed class AuditCapture
    {
        const int ChunkSize = 1000;

        readonly DbContext context;
        readonly ResolvedAuditModel model;
        readonly List<AuditChangeSet> changeSets = new();
        readonly List<(AuditChange Change, string Field, EntityEntry Entry, string Property)> temporaryFields = new();
        TrackedIndex index;

        AuditCapture(DbContext context, ResolvedAuditModel model)
        {
            this.context = context;
            this.model = model;
        }

        /// <summary>The change sets, one per changed root. Keys may still be temporary; see <see cref="Complete"/>.</summary>
        public IReadOnlyList<AuditChangeSet> ChangeSets => changeSets;

        /// <summary>True when no key or field still holds a temporary value, so <see cref="Complete"/> can run now.</summary>
        public bool KeysAreFinal =>
            changeSets.All(s => s.RootEntry == null || !HasTemporaryKey(s.RootEntry))
            && changeSets.SelectMany(s => s.Changes).All(c => !HasTemporaryKey(c.Entry))
            && temporaryFields.All(t => !t.Entry.Property(t.Property).IsTemporary);

        internal static async Task<AuditCapture> CaptureAsync(ChangeTracker tracker, AuditModel configuration, CancellationToken ct)
        {
            if (tracker == null) throw new ArgumentNullException(nameof(tracker));
            if (configuration == null) throw new ArgumentNullException(nameof(configuration));

            var capture = new AuditCapture(tracker.Context, configuration.Resolve(tracker.Context.Model));
            await capture.RunAsync(tracker, ct);
            return capture;
        }

        /// <summary>
        /// Increases the version of every changed root that has one, in one statement per root type,
        /// and stamps the new value on each change set. A root inserted by this save starts at 1.
        /// Call it inside the transaction that will save the changes, right before saving: the
        /// statement locks the roots' rows (in key order, on PostgreSQL) until that transaction ends,
        /// so concurrent writers to one root get consecutive versions.
        /// </summary>
        public async Task BumpVersionsAsync(CancellationToken cancellationToken = default)
        {
            var versioned = changeSets.Where(s => s.RootConfiguration.VersionProperty != null).ToList();
            if (versioned.Count == 0) return;
            if (context.Database.CurrentTransaction == null)
                throw new InvalidOperationException(
                    "BumpVersionsAsync must run inside the transaction that saves the changes; begin one first.");

            foreach (var set in versioned.Where(s => s.RootEntry?.State == EntityState.Added))
            {
                var version = set.RootEntry.Property(set.RootConfiguration.VersionProperty);
                version.CurrentValue = ConvertTo(1L, version.Metadata.ClrType);
                set.Version = 1;
            }

            foreach (var group in versioned.Where(s => s.RootEntry?.State != EntityState.Added)
                         .GroupBy(s => s.RootConfiguration))
            {
                var rootType = model.Model.FindEntityType(group.Key.Entity.ClrType);
                var pk = rootType.FindPrimaryKey().Properties[0];
                var versionProperty = rootType.FindProperty(group.Key.VersionProperty);
                var byId = group.ToLookup(s => KeyString(s.RootId ?? s.RootEntry?.Property(pk.Name).CurrentValue));
                var ids = group.Select(s => s.RootId ?? s.RootEntry.Property(pk.Name).CurrentValue)
                    .Distinct().OrderBy(id => id).ToList();

                foreach (var chunk in ids.Chunk(ChunkSize))
                {
                    var rows = await QueryAsync(VersionBumpSql(rootType, pk, versionProperty, chunk.Length),
                        chunk.Select(id => ToProvider(pk, id)).ToList(), 2, cancellationToken);
                    foreach (var row in rows)
                    {
                        var id = FromProvider(pk, row[0]);
                        var version = Convert.ToInt64(row[1], CultureInfo.InvariantCulture);
                        foreach (var set in byId[KeyString(id)])
                        {
                            set.Version = version;
                            if (set.RootEntry != null)
                            {
                                var entry = set.RootEntry.Property(versionProperty.Name);
                                entry.CurrentValue = ConvertTo(version, versionProperty.ClrType);
                                entry.OriginalValue = entry.CurrentValue;
                                entry.IsModified = false;
                            }
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Fills in the keys and fields that were temporary at capture time, and returns the change
        /// sets. Throws while any is still temporary: call it after <c>SaveChanges</c> when the
        /// database generates keys, or before it otherwise (<see cref="KeysAreFinal"/>).
        /// </summary>
        public IReadOnlyList<AuditChangeSet> Complete()
        {
            foreach (var set in changeSets)
            {
                if (set.RootEntry != null)
                {
                    var key = KeyOf(set.RootEntry, original: false);
                    set.RootId = key.Count == 1 ? key.Values.First() : key.Values.ToArray();
                }
                foreach (var change in set.Changes)
                {
                    if (change.Entry != null && change.Key == null)
                        change.Key = KeyOf(change.Entry, change.Operation == AuditOperation.Removed);
                    if (change.ReplacedEntry != null && change.ReplacedKey == null)
                        change.ReplacedKey = KeyOf(change.ReplacedEntry, original: true);
                }
            }

            foreach (var (change, field, entry, property) in temporaryFields)
            {
                var value = entry.Property(property);
                if (value.IsTemporary)
                    throw new InvalidOperationException($"{change.Entity}.{field} is still temporary; complete after saving.");
                var fields = (Dictionary<string, AuditFieldChange>)change.Fields;
                if (fields.TryGetValue(field, out var current))
                    fields[field] = current with { New = value.CurrentValue };
            }
            temporaryFields.Clear();
            return changeSets;
        }

        // ---------------------------------------------------------------- capture

        async Task RunAsync(ChangeTracker tracker, CancellationToken ct)
        {
            tracker.DetectChanges();
            var entries = tracker.Entries().ToList();
            index = new TrackedIndex(entries);

            var raws = new List<RawChange>();
            var ownedFields = new Dictionary<EntityEntry, OwnedFields>();

            foreach (var entry in entries)
            {
                if (entry.State is not (EntityState.Added or EntityState.Modified or EntityState.Deleted))
                    continue;

                if (entry.Metadata.IsOwned())
                {
                    CollectOwned(entry, ownedFields);
                    continue;
                }

                var entity = model.Find(entry.Metadata);
                if (entity != null)
                    raws.Add(Diff(entity, entry));
            }

            // Owned changes belong to their owner, which may itself be unchanged (a changed address
            // leaves the owning row untouched in the change tracker).
            foreach (var (owner, owned) in ownedFields)
            {
                var entity = model.Find(owner.Metadata);
                if (entity == null) continue;
                var raw = raws.FirstOrDefault(r => r.Entry == owner);
                if (raw == null)
                {
                    raw = Diff(entity, owner); // an unchanged owner: no fields of its own, but a label
                    raws.Add(raw);
                }
                foreach (var (name, change) in owned.Fields)
                {
                    Put(raw.Fields, name, change.Old, change.New);
                    if (raw.Snapshot != null)
                        raw.Snapshot[name] = raw.Operation == AuditOperation.Removed ? change.Old : change.New;
                }
            }

            // Where does each change belong? Collect every walk first, so lookups for parents that
            // aren't loaded run as one query per entity type.
            var walks = new List<Walk>();
            foreach (var raw in raws)
                foreach (var path in raw.Entity.Paths)
                {
                    if (path.ForeignKeys.Count == 0)
                    {
                        walks.Add(Walk.Done(raw, path, original: false, new RootTarget(path, raw.Entry, null)));
                        continue;
                    }
                    var useOriginal = raw.Entry.State == EntityState.Deleted;
                    walks.Add(Walk.Start(raw, path, useOriginal, ForeignKeyValues(raw.Entry, path.ForeignKeys[0], useOriginal)));
                    if (raw.Entry.State == EntityState.Modified && ForeignKeyChanged(raw.Entry, path.ForeignKeys[0]))
                        walks.Add(Walk.Start(raw, path, original: true, ForeignKeyValues(raw.Entry, path.ForeignKeys[0], true)));
                }
            await ResolveAsync(walks, ct);

            var sets = new Dictionary<object, AuditChangeSet>();
            var commitId = Guid.NewGuid();
            var capturedAt = DateTime.UtcNow;

            AuditChangeSet SetFor(RootTarget target)
            {
                var identity = target.Entry != null
                    ? target.Entry.Entity
                    : (object)(target.Path.Root.Name + "\u001f" + KeyString(target.Id));
                if (sets.TryGetValue(identity, out var set)) return set;
                set = new AuditChangeSet
                {
                    Id = Guid.CreateVersion7(),
                    CommitId = commitId,
                    CapturedAt = capturedAt,
                    Root = target.Path.Root.Name,
                    RootConfiguration = target.Path.Root,
                    RootEntry = target.Entry,
                    RootId = target.Entry == null ? target.Id
                        : HasTemporaryKey(target.Entry) ? null
                        : target.Entry.Metadata.FindPrimaryKey().Properties is { Count: 1 } pk
                            ? target.Entry.Property(pk[0].Name).CurrentValue
                            : null,
                    RootKey = target.Path.Root.KeyProperty != null && target.Entry != null
                        ? target.Entry.Property(target.Path.Root.KeyProperty).CurrentValue
                        : null,
                    Changes = new List<AuditChange>(),
                };
                sets[identity] = set;
                return set;
            }

            foreach (var group in walks.GroupBy(w => w.Raw))
            {
                var raw = group.Key;
                foreach (var byPath in group.GroupBy(w => w.Path))
                {
                    var current = byPath.FirstOrDefault(w => !w.Original || raw.Entry.State == EntityState.Deleted)?.Target;
                    var before = byPath.FirstOrDefault(w => w.Original && raw.Entry.State != EntityState.Deleted)?.Target;

                    if (before == null || SameRoot(before, current))
                    {
                        if (current != null)
                            Changes(SetFor(current)).Add(raw.ToChange(raw.Operation, temporaryFields, this));
                        continue;
                    }

                    // Moved between roots, or detached from one (a nulled foreign key): it left the
                    // old root and joined the new one.
                    Changes(SetFor(before)).Add(raw.Detached(AuditOperation.Removed, this));
                    if (current != null)
                        Changes(SetFor(current)).Add(raw.Detached(AuditOperation.Added, this));
                }
            }

            foreach (var set in sets.Values)
            {
                var changes = Changes(set);
                FoldMatches(changes);
                changes.RemoveAll(c => c.Operation == AuditOperation.Modified && c.Fields.Count == 0);
                if (changes.Count == 0) continue;

                changes.Sort((a, b) =>
                {
                    var aRoot = a.Entry == set.RootEntry && a.Entry != null ? 0 : 1;
                    var bRoot = b.Entry == set.RootEntry && b.Entry != null ? 0 : 1;
                    if (aRoot != bRoot) return aRoot.CompareTo(bRoot);
                    if (a.Order != b.Order) return a.Order.CompareTo(b.Order);
                    return string.CompareOrdinal(KeyString(a.Key), KeyString(b.Key));
                });

                set.DomainEvents = changes.Select(c => c.Entry?.Entity)
                    .Prepend(set.RootEntry?.Entity)
                    .OfType<IGeneratesDomainEvents>()
                    .Distinct()
                    .SelectMany(e => e.Events.Cast<object>())
                    .ToList();
                changeSets.Add(set);
            }

            await FillRootKeysAsync(ct);
        }

        static List<AuditChange> Changes(AuditChangeSet set) => (List<AuditChange>)set.Changes;

        static bool SameRoot(RootTarget a, RootTarget b)
        {
            if (a == null || b == null) return a == b;
            if (a.Path.Root != b.Path.Root) return false;
            if (a.Entry != null || b.Entry != null) return a.Entry == b.Entry;
            return KeyString(a.Id) == KeyString(b.Id);
        }

        RawChange Diff(ResolvedEntity entity, EntityEntry entry)
        {
            var softDelete = model.Configuration.SoftDeleteProperty;
            var flag = softDelete == null ? null : entry.Metadata.FindProperty(softDelete);

            var operation = entry.State switch
            {
                EntityState.Added => AuditOperation.Added,
                EntityState.Deleted => AuditOperation.Removed,
                _ when flag != null
                       && Equals(entry.Property(flag.Name).OriginalValue, false)
                       && Equals(entry.Property(flag.Name).CurrentValue, true) => AuditOperation.Removed,
                _ => AuditOperation.Modified,
            };

            var raw = new RawChange(entity, entry, operation);
            foreach (var property in entry.Metadata.GetProperties())
            {
                if (!entity.IsAudited(property, model.Configuration)) continue;
                var value = entry.Property(property.Name);
                var name = entity.Configuration.FieldName(property.Name);

                switch (entry.State)
                {
                    case EntityState.Added:
                        raw.Snapshot[name] = value.CurrentValue;
                        Put(raw.Fields, name, null, Snapshot(property, value.CurrentValue));
                        if (value.IsTemporary) raw.TemporaryFields.Add((name, property.Name));
                        break;
                    case EntityState.Deleted:
                        raw.Snapshot[name] = value.OriginalValue;
                        Put(raw.Fields, name, value.OriginalValue, null);
                        break;
                    default:
                        raw.Snapshot[name] = value.CurrentValue;
                        if (property == flag && operation == AuditOperation.Removed) break;
                        if (!property.GetValueComparer().Equals(value.OriginalValue, value.CurrentValue))
                            Put(raw.Fields, name, value.OriginalValue, Snapshot(property, value.CurrentValue));
                        break;
                }
            }

            raw.Label = Join(entity.Configuration.LabelProperties, entry, operation == AuditOperation.Removed);
            if (entity.Configuration.MatchProperties != null)
                raw.MatchKey = Join(entity.Configuration.MatchProperties, entry, operation == AuditOperation.Removed) ?? "";
            return raw;
        }

        /// <summary>
        /// An owned entry's fields, under its top-level owner, with a dotted prefix. A replaced owned
        /// instance (the old one deleted, the new one added under the same key) is compared field
        /// by field, so only what really changed is written.
        /// </summary>
        void CollectOwned(EntityEntry entry, Dictionary<EntityEntry, OwnedFields> ownedFields)
        {
            var (owner, prefix) = OwnerOf(entry);
            if (owner == null) return;
            var entity = model.Find(owner.Metadata);
            if (entity == null) return;

            if (!ownedFields.TryGetValue(owner, out var owned))
                ownedFields[owner] = owned = new OwnedFields();
            if (!owned.Groups.TryGetValue(prefix, out var group))
                owned.Groups[prefix] = group = new List<EntityEntry>();
            group.Add(entry);

            // Recompute the whole group each time: the pair of a replacement arrives in either order.
            foreach (var name in owned.FieldsOf(prefix)) owned.Fields.Remove(name);
            owned.ClearPrefix(prefix);

            var deleted = group.FirstOrDefault(e => e.State == EntityState.Deleted);
            var added = group.FirstOrDefault(e => e.State == EntityState.Added);
            var modified = group.FirstOrDefault(e => e.State == EntityState.Modified);

            foreach (var property in entry.Metadata.GetProperties())
            {
                if (property.IsPrimaryKey() || property.GetContainingForeignKeys().Any(fk => fk.IsOwnership)) continue;
                var clrPath = prefix + "." + property.Name;
                if (model.Configuration.IgnoredPropertyNames.Contains(property.Name)
                    || entity.Configuration.Ignored.Contains(clrPath)
                    || entity.Configuration.Ignored.Contains(prefix))
                    continue;

                var name = entity.Configuration.FieldName(clrPath);
                var comparer = property.GetValueComparer();
                object old, @new;
                if (modified != null)
                {
                    old = modified.Property(property.Name).OriginalValue;
                    @new = modified.Property(property.Name).CurrentValue;
                }
                else
                {
                    old = deleted?.Property(property.Name).OriginalValue;
                    @new = added?.Property(property.Name).CurrentValue;
                }
                if (comparer.Equals(old, @new)) continue;
                owned.Add(prefix, name, new AuditFieldChange(old, Snapshot(property, @new)));
            }
        }

        (EntityEntry Owner, string Prefix) OwnerOf(EntityEntry entry)
        {
            var ownership = entry.Metadata.FindOwnership();
            if (ownership == null) return (null, null);
            var useOriginal = entry.State == EntityState.Deleted;
            var owner = index.Find(ownership.PrincipalKey, ForeignKeyValues(entry, ownership, useOriginal));
            if (owner == null) return (null, null);

            var navigation = ownership.PrincipalToDependent?.Name ?? entry.Metadata.ShortName();
            if (ownership.PrincipalToDependent?.IsCollection == true)
            {
                var own = entry.Metadata.FindPrimaryKey().Properties
                    .Where(p => !ownership.Properties.Contains(p))
                    .Select(p => useOriginal ? entry.Property(p.Name).OriginalValue : entry.Property(p.Name).CurrentValue);
                navigation += "[" + KeyString(own.ToArray()) + "]";
            }

            if (!owner.Metadata.IsOwned()) return (owner, navigation);
            var (top, prefix) = OwnerOf(owner);
            return (top, prefix == null ? null : prefix + "." + navigation);
        }

        /// <summary>
        /// Folds a removed and an added row with the same match key into one modification: rows an
        /// application replaces instead of updating would otherwise read as "removed X, added X".
        /// </summary>
        static void FoldMatches(List<AuditChange> changes)
        {
            var candidates = changes.Where(c => c.Match != null && c.Operation != AuditOperation.Modified)
                .GroupBy(c => (c.Entity, c.Match.Key));
            foreach (var group in candidates)
            {
                var removed = group.Where(c => c.Operation == AuditOperation.Removed).ToList();
                var added = group.Where(c => c.Operation == AuditOperation.Added).ToList();
                if (removed.Count != 1 || added.Count != 1) continue;

                var (old, @new) = (removed[0], added[0]);
                var fields = new Dictionary<string, AuditFieldChange>(StringComparer.Ordinal);
                foreach (var name in old.Match.Snapshot.Keys.Union(@new.Match.Snapshot.Keys))
                {
                    old.Match.Snapshot.TryGetValue(name, out var before);
                    @new.Match.Snapshot.TryGetValue(name, out var after);
                    Put(fields, name, before, after);
                }

                changes.Remove(old);
                changes.Remove(@new);
                if (fields.Count == 0) continue; // replaced by an identical row: nothing happened

                @new.Operation = AuditOperation.Modified;
                @new.Fields = fields;
                @new.ReplacedEntry = old.Entry;
                @new.ReplacedKey = old.Key;
                changes.Add(@new);
            }
        }

        /// <summary>Adds a field change, expanding dictionaries per key and skipping equal or all-null values.</summary>
        internal static void Put(IDictionary<string, AuditFieldChange> fields, string name, object old, object @new)
        {
            if (old is IDictionary || @new is IDictionary)
            {
                var before = old as IDictionary;
                var after = @new as IDictionary;
                var keys = new HashSet<string>(StringComparer.Ordinal);
                if (before != null) foreach (var k in before.Keys) keys.Add(Convert.ToString(k, CultureInfo.InvariantCulture));
                if (after != null) foreach (var k in after.Keys) keys.Add(Convert.ToString(k, CultureInfo.InvariantCulture));
                foreach (var key in keys.OrderBy(k => k, StringComparer.Ordinal))
                    Put(fields, name + "." + key, Lookup(before, key), Lookup(after, key));
                return;
            }

            if (old == null && @new == null) return;
            if (old != null && @new != null && ValuesEqual(old, @new)) return;
            fields[name] = new AuditFieldChange(old, @new);
        }

        static object Lookup(IDictionary dictionary, string key)
        {
            if (dictionary == null) return null;
            foreach (DictionaryEntry e in dictionary)
                if (Convert.ToString(e.Key, CultureInfo.InvariantCulture) == key)
                    return e.Value;
            return null;
        }

        static bool ValuesEqual(object a, object b)
        {
            if (Equals(a, b)) return true;
            if (a is string || b is string) return false;
            if (a is IEnumerable ea && b is IEnumerable eb)
                return ea.Cast<object>().SequenceEqual(eb.Cast<object>());
            return false;
        }

        static object Snapshot(IProperty property, object value) =>
            value == null ? null : property.GetValueComparer().Snapshot(value);

        static string Join(IReadOnlyList<string> properties, EntityEntry entry, bool original)
        {
            if (properties == null || properties.Count == 0) return null;
            var parts = properties
                .Select(p => entry.Metadata.FindProperty(p) == null
                    ? null
                    : original ? entry.Property(p).OriginalValue : entry.Property(p).CurrentValue)
                .Where(v => v != null)
                .Select(v => Convert.ToString(v, CultureInfo.InvariantCulture))
                .ToList();
            return parts.Count == 0 ? null : string.Join(" / ", parts);
        }

        // ---------------------------------------------------------------- roots

        static object[] ForeignKeyValues(EntityEntry entry, IForeignKey fk, bool original) =>
            fk.Properties.Select(p => original ? entry.Property(p.Name).OriginalValue : entry.Property(p.Name).CurrentValue).ToArray();

        static bool ForeignKeyChanged(EntityEntry entry, IForeignKey fk) =>
            fk.Properties.Any(p => !p.GetValueComparer().Equals(entry.Property(p.Name).OriginalValue, entry.Property(p.Name).CurrentValue));

        async Task ResolveAsync(List<Walk> walks, CancellationToken ct)
        {
            while (true)
            {
                foreach (var walk in walks.Where(w => w.Pending))
                    Advance(walk);

                var waiting = walks.Where(w => w.Pending && w.NeedsLookup).ToList();
                if (waiting.Count == 0) break;

                foreach (var group in waiting.GroupBy(w => w.Path.ForeignKeys[w.Hop].PrincipalKey))
                {
                    var key = group.Key;
                    if (key.Properties.Count != 1)
                        throw new NotSupportedException(
                            $"Finding the audit root of {group.First().Raw.Entry.Metadata.ShortName()} needs a lookup by " +
                            $"{key.DeclaringEntityType.ShortName()}'s composite key, which isn't supported. Load the parent first.");

                    var principal = group.First().Path.ForeignKeys[group.First().Hop].PrincipalEntityType;
                    var wanted = new List<IProperty> { key.Properties[0] };
                    foreach (var p in principal.FindPrimaryKey().Properties) if (!wanted.Contains(p)) wanted.Add(p);
                    foreach (var walk in group.Where(w => w.Hop + 1 < w.Path.ForeignKeys.Count))
                        foreach (var p in walk.Path.ForeignKeys[walk.Hop + 1].Properties)
                            if (!wanted.Contains(p)) wanted.Add(p);

                    var values = group.Select(w => w.Values[0]).Distinct().ToList();
                    var rows = await LookupAsync(principal, key.Properties[0], values, wanted, ct);
                    foreach (var walk in group)
                        walk.Arrive(rows.TryGetValue(KeyString(walk.Values[0]), out var row) ? row : null);
                }
            }
        }

        /// <summary>Moves a walk as far as the tracked entities take it.</summary>
        void Advance(Walk walk)
        {
            while (walk.Pending && !walk.NeedsLookup)
            {
                if (walk.Row != null)
                {
                    // A looked-up row: finish, or step to its own parent.
                    var fk = walk.Path.ForeignKeys[walk.Hop];
                    if (walk.Hop == walk.Path.ForeignKeys.Count - 1)
                    {
                        var pk = fk.PrincipalEntityType.FindPrimaryKey().Properties;
                        walk.Finish(new RootTarget(walk.Path, null, pk.Count == 1 ? walk.Row[pk[0]] : pk.Select(p => walk.Row[p]).ToArray()));
                        break;
                    }
                    var next = walk.Path.ForeignKeys[walk.Hop + 1];
                    walk.Step(next.Properties.Select(p => walk.Row[p]).ToArray());
                    continue;
                }

                if (walk.Values.Any(v => v == null))
                {
                    walk.Finish(null); // no parent: an orphan, as far as this root is concerned
                    break;
                }

                var hop = walk.Path.ForeignKeys[walk.Hop];
                var found = index.Find(hop.PrincipalKey, walk.Values);
                var last = walk.Hop == walk.Path.ForeignKeys.Count - 1;
                if (found != null)
                {
                    if (last)
                    {
                        walk.Finish(new RootTarget(walk.Path, found, null));
                        break;
                    }
                    walk.Step(ForeignKeyValues(found, walk.Path.ForeignKeys[walk.Hop + 1], found.State == EntityState.Deleted));
                    continue;
                }

                if (last && hop.PrincipalKey.IsPrimaryKey())
                {
                    walk.Finish(new RootTarget(walk.Path, null, walk.Values.Length == 1 ? walk.Values[0] : walk.Values));
                    break;
                }
                walk.NeedsLookup = true;
            }
        }

        async Task FillRootKeysAsync(CancellationToken ct)
        {
            var missing = changeSets.Where(s => s.RootEntry == null && s.RootConfiguration.KeyProperty != null && s.RootId != null);
            foreach (var group in missing.GroupBy(s => s.RootConfiguration))
            {
                var type = model.Model.FindEntityType(group.Key.Entity.ClrType);
                var pk = type.FindPrimaryKey().Properties;
                if (pk.Count != 1) continue;
                var keyProperty = type.FindProperty(group.Key.KeyProperty);
                var rows = await LookupAsync(type, pk[0], group.Select(s => s.RootId).Distinct().ToList(),
                    new List<IProperty> { pk[0], keyProperty }, ct);
                foreach (var set in group)
                    if (rows.TryGetValue(KeyString(set.RootId), out var row))
                        set.RootKey = row[keyProperty];
            }
        }

        // ---------------------------------------------------------------- SQL

        async Task<Dictionary<string, Dictionary<IProperty, object>>> LookupAsync(IEntityType type, IProperty by,
            IReadOnlyList<object> values, List<IProperty> columns, CancellationToken ct)
        {
            var sql = context.GetService<ISqlGenerationHelper>();
            var table = Table(type);
            var result = new Dictionary<string, Dictionary<IProperty, object>>();
            foreach (var chunk in values.Chunk(ChunkSize))
            {
                var text = $"SELECT {string.Join(", ", columns.Select(c => sql.DelimitIdentifier(Column(c, table))))} " +
                           $"FROM {sql.DelimitIdentifier(table.Name, table.Schema)} " +
                           $"WHERE {sql.DelimitIdentifier(Column(by, table))} IN ({Parameters(chunk.Length)})";
                var rows = await QueryAsync(text, chunk.Select(v => ToProvider(by, v)).ToList(), columns.Count, ct);
                foreach (var row in rows)
                {
                    var values2 = new Dictionary<IProperty, object>();
                    for (var i = 0; i < columns.Count; i++) values2[columns[i]] = FromProvider(columns[i], row[i]);
                    result[KeyString(values2[by])] = values2;
                }
            }
            return result;
        }

        string VersionBumpSql(IEntityType type, IProperty pk, IProperty version, int count)
        {
            var sql = context.GetService<ISqlGenerationHelper>();
            var table = Table(type);
            var name = sql.DelimitIdentifier(table.Name, table.Schema);
            var id = sql.DelimitIdentifier(Column(pk, table));
            var v = sql.DelimitIdentifier(Column(version, table));
            var provider = context.Database.ProviderName ?? "";

            if (provider.Contains("Npgsql", StringComparison.OrdinalIgnoreCase))
                // Lock in key order first, so two writers touching the same roots can't deadlock.
                return $"WITH locked AS (SELECT {id} FROM {name} WHERE {id} IN ({Parameters(count)}) ORDER BY {id} FOR UPDATE) " +
                       $"UPDATE {name} AS t SET {v} = COALESCE(t.{v}, 0) + 1 FROM locked WHERE t.{id} = locked.{id} " +
                       $"RETURNING t.{id}, t.{v}";
            if (provider.Contains("Sqlite", StringComparison.OrdinalIgnoreCase))
                return $"UPDATE {name} SET {v} = COALESCE({v}, 0) + 1 WHERE {id} IN ({Parameters(count)}) RETURNING {id}, {v}";
            if (provider.Contains("SqlServer", StringComparison.OrdinalIgnoreCase))
                return $"UPDATE {name} SET {v} = COALESCE({v}, 0) + 1 OUTPUT INSERTED.{id}, INSERTED.{v} WHERE {id} IN ({Parameters(count)})";
            throw new NotSupportedException($"Audit versions aren't supported on {provider}.");
        }

        async Task<List<object[]>> QueryAsync(string text, IReadOnlyList<object> parameters, int columns, CancellationToken ct)
        {
            var database = context.Database;
            var connection = database.GetDbConnection();
            var opened = connection.State != ConnectionState.Open;
            if (opened) await database.OpenConnectionAsync(ct);
            try
            {
                await using var command = connection.CreateCommand();
                command.CommandText = text;
                command.Transaction = database.CurrentTransaction?.GetDbTransaction();
                var timeout = database.GetCommandTimeout();
                if (timeout.HasValue) command.CommandTimeout = timeout.Value;
                for (var i = 0; i < parameters.Count; i++)
                {
                    var parameter = command.CreateParameter();
                    parameter.ParameterName = "@p" + i;
                    parameter.Value = parameters[i] ?? DBNull.Value;
                    command.Parameters.Add(parameter);
                }

                var rows = new List<object[]>();
                await using var reader = await command.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    var row = new object[columns];
                    for (var i = 0; i < columns; i++) row[i] = reader.IsDBNull(i) ? null : reader.GetValue(i);
                    rows.Add(row);
                }
                return rows;
            }
            finally
            {
                if (opened) await database.CloseConnectionAsync();
            }
        }

        static string Parameters(int count) => string.Join(", ", Enumerable.Range(0, count).Select(i => "@p" + i));

        static StoreObjectIdentifier Table(IEntityType type)
        {
            var name = type.GetTableName()
                ?? throw new NotSupportedException($"{type.ShortName()} isn't mapped to a table.");
            return StoreObjectIdentifier.Table(name, type.GetSchema());
        }

        static string Column(IProperty property, StoreObjectIdentifier table) =>
            property.GetColumnName(table)
            ?? throw new NotSupportedException($"{property.DeclaringType.ShortName()}.{property.Name} has no column in {table.Name}.");

        static object ToProvider(IProperty property, object value)
        {
            var converter = property.GetTypeMapping().Converter;
            return converter == null || value == null ? value : converter.ConvertToProvider(value);
        }

        static object FromProvider(IProperty property, object value)
        {
            if (value == null || value is DBNull) return null;
            var converter = property.GetTypeMapping().Converter;
            if (converter != null) return converter.ConvertFromProvider(value);
            return ConvertTo(value, property.ClrType);
        }

        static object ConvertTo(object value, Type type)
        {
            var target = Nullable.GetUnderlyingType(type) ?? type;
            if (value == null || target.IsInstanceOfType(value)) return value;
            if (target == typeof(Guid)) return value is string s ? Guid.Parse(s) : value;
            return value is IConvertible ? Convert.ChangeType(value, target, CultureInfo.InvariantCulture) : value;
        }

        // ---------------------------------------------------------------- keys

        static bool HasTemporaryKey(EntityEntry entry) =>
            entry != null && entry.Metadata.FindPrimaryKey().Properties.Any(p => entry.Property(p.Name).IsTemporary);

        static IReadOnlyDictionary<string, object> KeyOf(EntityEntry entry, bool original)
        {
            var key = new Dictionary<string, object>(StringComparer.Ordinal);
            foreach (var property in entry.Metadata.FindPrimaryKey().Properties)
            {
                var value = entry.Property(property.Name);
                if (!original && value.IsTemporary)
                    throw new InvalidOperationException(
                        $"{entry.Metadata.ShortName()}'s key is still temporary; complete the audit after saving.");
                key[property.Name] = original ? value.OriginalValue : value.CurrentValue;
            }
            return key;
        }

        /// <summary>A comparable form of a key value: numbers by value, whatever their CLR type.</summary>
        internal static string KeyString(object value) => value switch
        {
            null => "\u0000",
            IReadOnlyDictionary<string, object> map => string.Join("\u001f", map.Values.Select(KeyString)),
            object[] parts => string.Join("\u001f", parts.Select(KeyString)),
            string s => s,
            Guid g => g.ToString("D"),
            DateTime d => d.ToString("O", CultureInfo.InvariantCulture),
            IConvertible c when IsNumber(value) => Convert.ToDecimal(c, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture),
            _ => Convert.ToString(value, CultureInfo.InvariantCulture),
        };

        static bool IsNumber(object value) => value is byte or sbyte or short or ushort or int or uint or long or ulong or decimal;

        // ---------------------------------------------------------------- helpers

        sealed class TrackedIndex
        {
            readonly List<EntityEntry> entries;
            readonly Dictionary<IKey, Dictionary<string, EntityEntry>> byKey = new();

            public TrackedIndex(List<EntityEntry> entries) => this.entries = entries;

            public EntityEntry Find(IKey key, object[] values)
            {
                if (!byKey.TryGetValue(key, out var map))
                {
                    map = new Dictionary<string, EntityEntry>();
                    foreach (var entry in entries)
                    {
                        if (!key.DeclaringEntityType.IsAssignableFrom(entry.Metadata)) continue;
                        var current = key.Properties.Select(p => entry.Property(p.Name).CurrentValue).ToArray();
                        map[KeyString(current)] = entry;
                        if (entry.State == EntityState.Deleted || entry.State == EntityState.Modified)
                            map.TryAdd(KeyString(key.Properties.Select(p => entry.Property(p.Name).OriginalValue).ToArray()), entry);
                    }
                    byKey[key] = map;
                }
                return map.TryGetValue(KeyString(values), out var found) ? found : null;
            }
        }

        sealed class OwnedFields
        {
            public Dictionary<string, AuditFieldChange> Fields { get; } = new(StringComparer.Ordinal);
            public Dictionary<string, List<EntityEntry>> Groups { get; } = new(StringComparer.Ordinal);
            readonly Dictionary<string, List<string>> byPrefix = new(StringComparer.Ordinal);

            public IEnumerable<string> FieldsOf(string prefix) =>
                byPrefix.TryGetValue(prefix, out var names) ? names : Enumerable.Empty<string>();

            public void ClearPrefix(string prefix) => byPrefix.Remove(prefix);

            public void Add(string prefix, string name, AuditFieldChange change)
            {
                if (change.Old is IDictionary || change.New is IDictionary)
                {
                    var expanded = new Dictionary<string, AuditFieldChange>(StringComparer.Ordinal);
                    Put(expanded, name, change.Old, change.New);
                    foreach (var (n, c) in expanded) Add(prefix, n, c);
                    return;
                }
                if (change.Old == null && change.New == null) return;
                Fields[name] = change;
                if (!byPrefix.TryGetValue(prefix, out var names)) byPrefix[prefix] = names = new List<string>();
                names.Add(name);
            }
        }

        sealed class RootTarget
        {
            public RootTarget(AuditRootPath path, EntityEntry entry, object id)
            {
                Path = path;
                Entry = entry;
                Id = id;
            }

            public AuditRootPath Path { get; }
            public EntityEntry Entry { get; }
            public object Id { get; }
        }

        sealed class Walk
        {
            public RawChange Raw { get; private init; }
            public AuditRootPath Path { get; private init; }
            public bool Original { get; private init; }
            public int Hop { get; private set; }
            public object[] Values { get; private set; }
            public Dictionary<IProperty, object> Row { get; private set; }
            public bool Pending { get; private set; } = true;
            public bool NeedsLookup { get; set; }
            public RootTarget Target { get; private set; }

            public static Walk Start(RawChange raw, AuditRootPath path, bool original, object[] values) =>
                new() { Raw = raw, Path = path, Original = original, Values = values };

            public static Walk Done(RawChange raw, AuditRootPath path, bool original, RootTarget target) =>
                new() { Raw = raw, Path = path, Original = original, Pending = false, Target = target };

            public void Step(object[] values)
            {
                Hop++;
                Values = values;
                Row = null;
            }

            public void Arrive(Dictionary<IProperty, object> row)
            {
                NeedsLookup = false;
                if (row == null) Finish(null);
                else Row = row;
            }

            public void Finish(RootTarget target)
            {
                Target = target;
                Pending = false;
                NeedsLookup = false;
            }
        }

        sealed class RawChange
        {
            public RawChange(ResolvedEntity entity, EntityEntry entry, AuditOperation operation)
            {
                Entity = entity;
                Entry = entry;
                Operation = operation;
            }

            public ResolvedEntity Entity { get; }
            public EntityEntry Entry { get; }
            public AuditOperation Operation { get; }
            public Dictionary<string, AuditFieldChange> Fields { get; } = new(StringComparer.Ordinal);

            /// <summary>Every audited value (current, or original for a removal), for match folding and detaching.</summary>
            public Dictionary<string, object> Snapshot { get; } = new(StringComparer.Ordinal);

            public List<(string Field, string Property)> TemporaryFields { get; } = new();
            public string Label { get; set; }
            public string MatchKey { get; set; }

            public AuditChange ToChange(AuditOperation operation, List<(AuditChange, string, EntityEntry, string)> temporary, AuditCapture capture)
            {
                var change = New(operation, new Dictionary<string, AuditFieldChange>(Fields, StringComparer.Ordinal), capture);
                foreach (var (field, property) in TemporaryFields)
                    temporary.Add((change, field, Entry, property));
                return change;
            }

            /// <summary>The whole row, as it left one root (its original values) or joined another (its current ones).</summary>
            public AuditChange Detached(AuditOperation operation, AuditCapture capture)
            {
                var fields = new Dictionary<string, AuditFieldChange>(StringComparer.Ordinal);
                foreach (var property in Entry.Metadata.GetProperties())
                {
                    if (!Entity.IsAudited(property, capture.model.Configuration)) continue;
                    var value = Entry.Property(property.Name);
                    var name = Entity.Configuration.FieldName(property.Name);
                    if (operation == AuditOperation.Removed) Put(fields, name, value.OriginalValue, null);
                    else Put(fields, name, null, Snapshot(property, value.CurrentValue));
                }
                var change = New(operation, fields, capture);
                if (operation == AuditOperation.Removed)
                    change.Key = KeyOf(Entry, original: true);
                return change;
            }

            AuditChange New(AuditOperation operation, Dictionary<string, AuditFieldChange> fields, AuditCapture capture)
            {
                var change = new AuditChange
                {
                    Entity = Entity.Configuration.Name,
                    Label = Label,
                    Operation = operation,
                    Fields = fields,
                    Entry = Entry,
                    Order = Entity.Configuration.Order,
                    Match = MatchKey == null ? null : new MatchInfo(MatchKey, Snapshot),
                };
                if (!HasTemporaryKey(Entry) || operation == AuditOperation.Removed)
                    change.Key = KeyOf(Entry, operation == AuditOperation.Removed || Entry.State == EntityState.Deleted);
                return change;
            }
        }
    }

    internal sealed record MatchInfo(string Key, IReadOnlyDictionary<string, object> Snapshot);
}
