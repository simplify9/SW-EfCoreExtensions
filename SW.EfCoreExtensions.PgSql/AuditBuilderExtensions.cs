using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using SW.PrimitiveTypes;

namespace SW.EfCoreExtensions;

/// <summary>
/// Represents the before and after values of a property change in an audit trail.
/// </summary>
/// <param name="Old">The original value before the change.</param>
/// <param name="New">The new value after the change.</param>
public sealed record PropertyDiff(
    object? Old,
    object? New
);

/// <summary>
/// Represents a finalized audit entry that can be serialized to JSON for storage or transmission.
/// Contains all information about a single entity change, including metadata and domain events.
/// </summary>
/// <param name="CorrelationId">A unique identifier shared by all changes in the same transaction/SaveChanges call.</param>
/// <param name="Sequence">The sequential order of this change within the transaction (1-based).</param>
/// <param name="Timestamp">The UTC timestamp when the change was captured.</param>
/// <param name="UserId">Optional identifier of the user or actor who made the change.</param>
/// <param name="EntityName">The display name of the entity type.</param>
/// <param name="EntityType">The full CLR type name of the entity.</param>
/// <param name="State">The state of the entity (Added, Modified, or Deleted).</param>
/// <param name="PrimaryKey">A dictionary containing the primary key property names and values.</param>
/// <param name="Changes">A dictionary of property names to their before/after values.</param>
/// <param name="DomainEvents">Optional collection of domain events associated with this change.</param>
public sealed record GenericAuditDiffJson(
    string CorrelationId,
    int Sequence,
    DateTime Timestamp,
    string? UserId,
    string EntityName,
    string EntityType,
    string State,
    object PrimaryKey,
    IReadOnlyDictionary<string, PropertyDiff> Changes,
    IReadOnlyCollection<DomainEventEnvelope>? DomainEvents
);

/// <summary>
/// Wraps a domain event with metadata for auditing purposes.
/// </summary>
/// <param name="EventId">A unique identifier for this event instance.</param>
/// <param name="EventType">The full CLR type name of the event.</param>
/// <param name="EventName">The simple name of the event type.</param>
/// <param name="Payload">The actual event object containing the event data.</param>
public sealed record DomainEventEnvelope(
    string EventId,
    string EventType,
    string EventName,
    object Payload
);

/// <summary>
/// Represents a pending audit entry that has not yet been finalized.
/// Contains the Entity Framework change tracker entry alongside the audit metadata.
/// This intermediate representation is used before finalizing to JSON.
/// </summary>
public sealed class PendingAuditEntry
{
    /// <summary>
    /// Gets or sets the correlation ID shared by all changes in the same transaction.
    /// </summary>
    public string AuditCorrelationId { get; init; } = default!;
    
    /// <summary>
    /// Gets or sets the sequence number of this entry within the transaction (1-based).
    /// </summary>
    public int Sequence { get; init; }
    
    /// <summary>
    /// Gets or sets the UTC timestamp when this change was captured.
    /// </summary>
    public DateTime Timestamp { get; init; }
    
    /// <summary>
    /// Gets or sets the optional identifier of the user or actor who made the change.
    /// </summary>
    public string? UserId { get; init; }

    /// <summary>
    /// Gets or sets the Entity Framework change tracker entry for this entity.
    /// </summary>
    public EntityEntry Entry { get; init; } = default!;
    
    /// <summary>
    /// Gets or sets the display name of the entity type.
    /// </summary>
    public string EntityName { get; init; } = default!;
    
    /// <summary>
    /// Gets or sets the full CLR type name of the entity.
    /// </summary>
    public string EntityType { get; init; } = default!;
    
    /// <summary>
    /// Gets or sets the state of the entity (Added, Modified, or Deleted).
    /// </summary>
    public EntityState State { get; init; }

    /// <summary>
    /// Gets or sets the dictionary of property changes (property name to before/after values).
    /// </summary>
    public IReadOnlyDictionary<string, PropertyDiff> Changes { get; init; }
        = default!;

    /// <summary>
    /// Gets or sets the optional collection of domain events associated with this change.
    /// </summary>
    public IReadOnlyCollection<DomainEventEnvelope>? DomainEvents { get; init; }
}


/// <summary>
/// Provides extension methods for building audit trails from Entity Framework Core change tracking.
/// Enables capturing, finalizing, and reconstructing entity changes for audit logging purposes.
/// </summary>
public static class AuditBuilderExtension
{
    /// <summary>
    /// Captures pending audit differences from the Entity Framework change tracker.
    /// Detects all entities in Added, Modified, or Deleted states and creates audit entries for them.
    /// All captured changes share the same correlation ID and timestamp.
    /// </summary>
    /// <param name="changeTracker">The Entity Framework change tracker to capture changes from.</param>
    /// <param name="userId">Optional identifier of the user or actor making the changes. Used for audit accountability.</param>
    /// <returns>A read-only collection of pending audit entries, each representing a single entity change.</returns>
    /// <remarks>
    /// This method performs the following:
    /// <list type="bullet">
    /// <item><description>Calls DetectChanges() to ensure all changes are tracked</description></item>
    /// <item><description>Generates a shared correlation ID for all changes in this batch</description></item>
    /// <item><description>Records a UTC timestamp shared by all changes</description></item>
    /// <item><description>Skips entities with no meaningful changes (e.g., only temporary properties changed)</description></item>
    /// <item><description>Captures domain events from entities implementing IGeneratesDomainEvents</description></item>
    /// </list>
    /// Use <see cref="FinalizeAuditDiffJson"/> to convert the results to JSON-serializable format.
    /// </remarks>
    /// <example>
    /// <code>
    /// var pendingAudits = dbContext.ChangeTracker.CapturePendingAuditDiffs(userId: "user123");
    /// // ... perform SaveChanges ...
    /// var finalizedAudits = pendingAudits.FinalizeAuditDiffJson();
    /// </code>
    /// </example>
    public static IReadOnlyCollection<PendingAuditEntry>
        CapturePendingAuditDiffs(this ChangeTracker changeTracker, string? userId = null)
    {
        changeTracker.DetectChanges();

        var audits = new List<PendingAuditEntry>();
        
        var correlationId = Guid.NewGuid().ToString();
        var timestamp = DateTime.UtcNow;
        var sequence = 0;
        
        foreach (var entry in changeTracker.Entries()
                     .Where(e => e.State is EntityState.Added
                         or EntityState.Modified
                         or EntityState.Deleted))
        {
            var changes = BuildDiff(entry);

            if (changes.Count == 0)
                continue; // nothing meaningful changed

            IReadOnlyCollection<DomainEventEnvelope> domainEvents = null;

            if (entry.Entity is IGeneratesDomainEvents e && e.Events.Count != 0)
                domainEvents = e.Events.Select(ev => new DomainEventEnvelope(
                        EventId: Guid.NewGuid().ToString(),
                        EventType: ev.GetType().FullName!,
                        EventName: ev.GetType().Name,
                        Payload: ev
                    ))
                    .ToArray();
            
            audits.Add(new PendingAuditEntry
            {
                AuditCorrelationId = correlationId,
                Sequence = ++sequence,
                Timestamp = timestamp,
                UserId = userId,
                Entry = entry,
                EntityName = entry.Metadata.DisplayName(),
                EntityType = entry.Metadata.ClrType.FullName!,
                State = entry.State,
                Changes = changes,
                DomainEvents = domainEvents
            });
        }

        return audits;
    }

    /// <summary>
    /// Finalizes pending audit entries into JSON-serializable audit records.
    /// Extracts primary key values and converts EntityEntry references into plain data structures.
    /// </summary>
    /// <param name="pending">The collection of pending audit entries to finalize.</param>
    /// <returns>A read-only collection of finalized audit entries ready for JSON serialization and storage.</returns>
    /// <remarks>
    /// This method should be called after SaveChanges() to ensure primary key values are available
    /// for entities that were in the Added state. The resulting objects can be safely serialized
    /// to JSON and stored in audit tables or logging systems.
    /// </remarks>
    /// <example>
    /// <code>
    /// var pendingAudits = dbContext.ChangeTracker.CapturePendingAuditDiffs();
    /// await dbContext.SaveChangesAsync();
    /// var finalizedAudits = pendingAudits.FinalizeAuditDiffJson();
    /// await SaveToAuditLog(finalizedAudits);
    /// </code>
    /// </example>
    public static IReadOnlyCollection<GenericAuditDiffJson>
        FinalizeAuditDiffJson(this IEnumerable<PendingAuditEntry> pending)
    {
        return pending.Select(p =>
        {
            var pk = p.Entry.Properties
                .Where(x => x.Metadata.IsPrimaryKey())
                .ToDictionary(x => x.Metadata.Name, x => x.CurrentValue);

            return new GenericAuditDiffJson(
                CorrelationId: p.AuditCorrelationId,
                Sequence: p.Sequence,
                Timestamp: p.Timestamp,
                UserId: p.UserId,
                EntityName: p.EntityName,
                EntityType: p.EntityType,
                State: p.State.ToString(),
                PrimaryKey: pk,
                Changes: p.Changes,
                DomainEvents: p.DomainEvents
            );
        }).ToArray();
    }

    /// <summary>
    /// Reconstructs the final state of an entity by replaying a sequence of audit entries.
    /// Applies changes in sequence order to build up the current state from historical audit records.
    /// </summary>
    /// <param name="diffs">The collection of audit entries to replay, typically for a single entity.</param>
    /// <returns>A dictionary representing the reconstructed state, with property names as keys and their final values.</returns>
    /// <remarks>
    /// This method processes audit entries in sequence order, applying each property change
    /// to build the final state. It's useful for:
    /// <list type="bullet">
    /// <item><description>Reconstructing an entity's state at a specific point in time</description></item>
    /// <item><description>Understanding the complete history of an entity</description></item>
    /// <item><description>Implementing temporal queries or "time travel" features</description></item>
    /// </list>
    /// The method assumes the diffs are for the same entity and should be ordered by Sequence.
    /// </remarks>
    /// <example>
    /// <code>
    /// var entityAudits = auditLog.Where(a => a.PrimaryKey == entityId);
    /// var currentState = entityAudits.ReconstructState();
    /// </code>
    /// </example>
    public static IDictionary<string, object?> 
        ReconstructState(
            this IEnumerable<GenericAuditDiffJson> diffs)
    {
        var state = new Dictionary<string, object?>();

        foreach (var diff in diffs.OrderBy(d => d.Sequence))
        {
            foreach (var change in diff.Changes)
            {
                state[change.Key] = change.Value.New;
            }
        }

        return state;
    }
    private static Dictionary<string, PropertyDiff>
        BuildDiff(EntityEntry entry)
    {
        var diffs = new Dictionary<string, PropertyDiff>();

        foreach (var prop in entry.Properties)
        {
            if (prop.IsTemporary)
                continue;

            if (entry.State == EntityState.Added)
            {
                diffs[prop.Metadata.Name] =
                    new PropertyDiff(null, prop.CurrentValue);
                continue;
            }

            if (entry.State == EntityState.Deleted)
            {
                diffs[prop.Metadata.Name] =
                    new PropertyDiff(prop.OriginalValue, null);
                continue;
            }

            // Modified
            if (prop.IsModified)
            {
                diffs[prop.Metadata.Name] =
                    new PropertyDiff(
                        prop.OriginalValue,
                        prop.CurrentValue
                    );
            }
        }

        return diffs;
    }

}