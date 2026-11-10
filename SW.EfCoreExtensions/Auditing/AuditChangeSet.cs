using System;
using System.Collections.Generic;

namespace SW.EfCoreExtensions
{
    /// <summary>What happened to one entity, as seen from the aggregate it belongs to.</summary>
    public enum AuditOperation
    {
        /// <summary>The entity was inserted, or attached to this aggregate.</summary>
        Added,

        /// <summary>Some of the entity's fields changed.</summary>
        Modified,

        /// <summary>The entity was deleted, soft-deleted, or detached from this aggregate.</summary>
        Removed
    }

    /// <summary>One field's value before and after. <c>Old</c> is null on an add, <c>New</c> on a removal.</summary>
    public readonly record struct AuditFieldChange(object Old, object New);

    /// <summary>One entity's change inside an <see cref="AuditChangeSet"/>.</summary>
    public sealed class AuditChange
    {
        internal AuditChange() { }

        /// <summary>The entity's audit name: <c>item</c>, <c>box</c>, or the root's own name.</summary>
        public string Entity { get; internal set; }

        /// <summary>The entity's primary key, property name to value.</summary>
        public IReadOnlyDictionary<string, object> Key { get; internal set; }

        /// <summary>
        /// Set when a removed and an added entity were folded into one modification by a match key:
        /// the key of the row that was removed. <see cref="Key"/> is then the new row's.
        /// </summary>
        public IReadOnlyDictionary<string, object> ReplacedKey { get; internal set; }

        /// <summary>A human handle for the entity (a product code, a box number), or null.</summary>
        public string Label { get; internal set; }

        public AuditOperation Operation { get; internal set; }

        /// <summary>
        /// Changed fields by audit name. Owned types are folded in with dotted names
        /// (<c>consignee.city</c>), dictionaries per key (<c>extraData.someKey</c>).
        /// </summary>
        public IReadOnlyDictionary<string, AuditFieldChange> Fields { get; internal set; }

        internal Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry Entry { get; set; }
        internal Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry ReplacedEntry { get; set; }
        internal int Order { get; set; }
        internal MatchInfo Match { get; set; }
    }

    /// <summary>
    /// Everything one <c>SaveChanges</c> did to one aggregate root: the root's own row and every
    /// audited entity under it.
    /// </summary>
    public sealed class AuditChangeSet
    {
        internal AuditChangeSet() { }

        /// <summary>A time-ordered (version 7) id for this change set.</summary>
        public Guid Id { get; internal set; }

        /// <summary>Shared by every change set captured from the same <c>SaveChanges</c>.</summary>
        public Guid CommitId { get; internal set; }

        /// <summary>When the changes were captured, UTC.</summary>
        public DateTime CapturedAt { get; internal set; }

        /// <summary>The root's audit name, e.g. <c>order</c>.</summary>
        public string Root { get; internal set; }

        /// <summary>The root's primary key value.</summary>
        public object RootId { get; internal set; }

        /// <summary>The root's business key (e.g. a uid), when one is configured.</summary>
        public object RootKey { get; internal set; }

        /// <summary>
        /// The root's version after this change set, when the root has a version column and
        /// <see cref="AuditCapture.BumpVersionsAsync"/> has run. Strictly increasing per root.
        /// </summary>
        public long? Version { get; internal set; }

        /// <summary>The changes, the root's own first.</summary>
        public IReadOnlyList<AuditChange> Changes { get; internal set; }

        /// <summary>
        /// Domain events pending on the root and on the entities in <see cref="Changes"/>, as the
        /// objects themselves. Read before the events are dispatched, which clears them.
        /// </summary>
        public IReadOnlyList<object> DomainEvents { get; internal set; }

        internal Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry RootEntry { get; set; }
        internal AuditRootConfiguration RootConfiguration { get; set; }
    }
}
