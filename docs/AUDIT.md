# Audit Trail Feature — `SW.EfCoreExtensions`

> Package: **`SimplyWorks.EfCoreExtensions`**  
> Namespace: `SW.EfCoreExtensions`  
> Class: `AuditBuilderExtension`

---

## Table of Contents

1. [Overview](#overview)
2. [Core Types](#core-types)
   - [PropertyDiff](#propertydiff)
   - [DomainEventEnvelope](#domaineventenvelope)
   - [PendingAuditEntry](#pendingauditentry)
   - [GenericAuditDiffJson](#genericauditdifffson)
3. [Methods](#methods)
   - [CapturePendingAuditDiffs](#capturependingauditdiffs)
   - [FinalizeAuditDiffJson](#finalizeauditdifffson)
   - [ReconstructState](#reconstructstate)
4. [End-to-End Usage](#end-to-end-usage)
   - [Basic Audit Logging](#basic-audit-logging)
   - [With Domain Events](#with-domain-events)
   - [Overriding SaveChangesAsync](#overriding-savechangesasync)
   - [Reconstructing Entity State](#reconstructing-entity-state)
5. [Domain Events Support](#domain-events-support)
6. [Difference vs ChangeTrackerExtensions](#difference-vs-changetracker-extensions)
7. [Notes & Best Practices](#notes--best-practices)

---

## Overview

The audit feature lets you capture a **structured, JSON-serializable diff** of every entity change that passes through EF Core's `SaveChanges`. Each captured diff records:

- **Who** made the change (`UserId`)
- **When** it happened (`Timestamp` — always UTC)
- **What** changed (`Changes` — old/new values per property)
- **Which entity** was affected (`EntityName`, `EntityType`, `PrimaryKey`)
- **What state** it was in (`Added`, `Modified`, `Deleted`)
- **What domain events** were raised (optional, if the entity implements `IGeneratesDomainEvents`)
- **Correlation** across all changes in a single `SaveChanges` call (`CorrelationId`, `Sequence`)

The workflow is a two-step process:

```
Before SaveChanges              After SaveChanges
─────────────────────           ────────────────────────────
CapturePendingAuditDiffs()  →   FinalizeAuditDiffJson()
                                     ↓
                            Store / ship JSON records
```

> **Why two steps?**  
> For `Added` entities, the database-generated primary key is only available *after* `SaveChanges`. `FinalizeAuditDiffJson` is therefore called **after** the save to guarantee PK values are present.

---

## Core Types

### `PropertyDiff`

```csharp
public sealed record PropertyDiff(object? Old, object? New);
```

Represents the before/after snapshot of a single property.

| Parameter | Type      | Description                        |
|-----------|-----------|------------------------------------|
| `Old`     | `object?` | The value **before** the change. `null` for newly added entities. |
| `New`     | `object?` | The value **after** the change. `null` for deleted entities. |

**Example value:**
```json
{ "Old": "John", "New": "Jonathan" }
```

---

### `DomainEventEnvelope`

```csharp
public sealed record DomainEventEnvelope(
    string EventId,
    string EventType,
    string EventName,
    object Payload
);
```

Wraps a domain event raised by an entity during a save operation.

| Parameter   | Type     | Description |
|-------------|----------|-------------|
| `EventId`   | `string` | A newly generated `Guid` string unique to this event instance. |
| `EventType` | `string` | The full CLR type name (e.g. `"MyApp.Domain.Events.OrderShipped"`). |
| `EventName` | `string` | The simple class name (e.g. `"OrderShipped"`). Useful as a message type key. |
| `Payload`   | `object` | The actual event object. Serialize this to store or dispatch the event. |

---

### `PendingAuditEntry`

```csharp
public sealed class PendingAuditEntry { ... }
```

An **intermediate** audit record created before `SaveChanges`. It holds a live reference to the EF `EntityEntry` so that primary keys can be read after the save.

| Property             | Type                                        | Description |
|----------------------|---------------------------------------------|-------------|
| `AuditCorrelationId` | `string`                                    | Shared GUID string across all entries in the same `SaveChanges` call. Use this to group related changes together. |
| `Sequence`           | `int`                                       | 1-based ordering of this change within the batch. Useful for replaying events in order. |
| `Timestamp`          | `DateTimeOffset`                            | UTC timestamp captured **once** for all entries in the batch (not per-entity). |
| `UserId`             | `string?`                                   | The identifier of the actor making the change. Pass from your authentication context. |
| `Entry`              | `EntityEntry`                               | The live EF Core change-tracker entry. **Do not serialize this directly.** Call `FinalizeAuditDiffJson` instead. |
| `EntityName`         | `string`                                    | The display name of the entity (from EF metadata). |
| `EntityType`         | `string`                                    | The full CLR type name of the entity class. |
| `State`              | `EntityState`                               | The change state: `Added`, `Modified`, or `Deleted`. |
| `Changes`            | `IReadOnlyDictionary<string, PropertyDiff>` | Per-property old/new values. Temporary properties (e.g. shadow keys before insert) are excluded. |
| `DomainEvents`       | `IReadOnlyCollection<DomainEventEnvelope>?` | Domain events raised by the entity, or `null` if none. |

---

### `GenericAuditDiffJson`

```csharp
public sealed record GenericAuditDiffJson(
    string CorrelationId,
    int Sequence,
    DateTimeOffset Timestamp,
    string? UserId,
    string EntityName,
    string EntityType,
    string State,
    object PrimaryKey,
    IReadOnlyDictionary<string, PropertyDiff> Changes,
    IReadOnlyCollection<DomainEventEnvelope>? DomainEvents
);
```

The **finalized, serializable** audit record. Safe to serialize to JSON and store in a database, send to a message bus, or write to a log file.

| Parameter       | Type                                        | Description |
|-----------------|---------------------------------------------|-------------|
| `CorrelationId` | `string`                                    | Shared GUID for all changes in the same `SaveChanges` call. Enables grouping/tracing a single transaction. |
| `Sequence`      | `int`                                       | 1-based position of this change within the transaction batch. |
| `Timestamp`     | `DateTimeOffset`                            | UTC time when the changes were captured (before save). |
| `UserId`        | `string?`                                   | The actor who triggered the change. `null` if not provided. |
| `EntityName`    | `string`                                    | EF display name of the entity (usually the class name). |
| `EntityType`    | `string`                                    | Full CLR type name of the entity (e.g. `"MyApp.Domain.Order"`). |
| `State`         | `string`                                    | `"Added"`, `"Modified"`, or `"Deleted"`. |
| `PrimaryKey`    | `object`                                    | A `Dictionary<string, object?>` of PK column names to their values. For composite keys, multiple entries will be present. |
| `Changes`       | `IReadOnlyDictionary<string, PropertyDiff>` | Map of property names to their `PropertyDiff` (old/new). |
| `DomainEvents`  | `IReadOnlyCollection<DomainEventEnvelope>?` | Domain events associated with this entity change, or `null`. |

**Example serialized JSON:**
```json
{
  "correlationId": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
  "sequence": 1,
  "timestamp": "2026-03-26T10:00:00.000Z",
  "userId": "user-42",
  "entityName": "Order",
  "entityType": "MyApp.Domain.Order",
  "state": "Modified",
  "primaryKey": { "Id": 101 },
  "changes": {
    "Status": { "old": "Pending", "new": "Shipped" },
    "UpdatedOn": { "old": "2026-03-25T08:00:00Z", "new": "2026-03-26T10:00:00Z" }
  },
  "domainEvents": [
    {
      "eventId": "d1e2f3a4-...",
      "eventType": "MyApp.Domain.Events.OrderShipped",
      "eventName": "OrderShipped",
      "payload": { "orderId": 101, "shippedAt": "2026-03-26T10:00:00Z" }
    }
  ]
}
```

---

## Methods

### `CapturePendingAuditDiffs`

```csharp
public static IReadOnlyCollection<PendingAuditEntry>
    CapturePendingAuditDiffs(this ChangeTracker changeTracker, string? userId = null)
```

**Call this BEFORE `SaveChanges`.**

Scans the EF Core change tracker for all entities in `Added`, `Modified`, or `Deleted` state and builds a list of `PendingAuditEntry` objects.

| Parameter      | Type      | Required | Description |
|----------------|-----------|----------|-------------|
| `changeTracker`| `ChangeTracker` | yes (extension) | The EF Core change tracker from your `DbContext`. |
| `userId`       | `string?` | no       | The current user/actor identifier. Pass from your HTTP context, JWT claim, or service identity. Defaults to `null`. |

**Returns:** `IReadOnlyCollection<PendingAuditEntry>` — one entry per changed entity. Entities with no meaningful property changes (e.g. only EF-internal temporary properties) are excluded.

**Behavior details:**
- Calls `DetectChanges()` automatically — you don't need to call it beforehand.
- Generates a single `CorrelationId` (GUID) shared by all entries in this call.
- Captures a single `DateTimeOffset.UtcNow` timestamp shared by all entries.
- Assigns a `Sequence` counter (1, 2, 3 …) to each entry in the order they were enumerated.
- Skips properties where `prop.IsTemporary == true` (e.g. auto-increment PKs before insert).
- Captures domain events from entities implementing `IGeneratesDomainEvents`.

---

### `FinalizeAuditDiffJson`

```csharp
public static IReadOnlyCollection<GenericAuditDiffJson>
    FinalizeAuditDiffJson(this IEnumerable<PendingAuditEntry> pending)
```

**Call this AFTER `SaveChanges`.**

Converts the collection of `PendingAuditEntry` objects into the final, serializable `GenericAuditDiffJson` records. At this point, auto-generated primary keys are resolved and included.

| Parameter | Type                              | Required | Description |
|-----------|-----------------------------------|----------|-------------|
| `pending` | `IEnumerable<PendingAuditEntry>`  | yes (extension) | The pending entries returned by `CapturePendingAuditDiffs`. |

**Returns:** `IReadOnlyCollection<GenericAuditDiffJson>` — finalized, JSON-safe records.

**Behavior details:**
- Reads `CurrentValue` of all primary key properties from the live `EntityEntry` (now available after save).
- Copies all other fields directly from `PendingAuditEntry`.
- Safe to serialize with `System.Text.Json` or `Newtonsoft.Json`.

> ⚠️ **Do not call this before `SaveChanges`** if any entity was in the `Added` state — the generated PK will be missing or `null`.

---

### `ReconstructState`

```csharp
public static IDictionary<string, object?>
    ReconstructState(this IEnumerable<GenericAuditDiffJson> diffs)
```

Replays a sequence of audit entries to reconstruct the **final known state** of an entity.

| Parameter | Type                                  | Required | Description |
|-----------|---------------------------------------|----------|-------------|
| `diffs`   | `IEnumerable<GenericAuditDiffJson>`   | yes (extension) | A sequence of audit diffs for the **same entity**, typically ordered by time. |

**Returns:** `IDictionary<string, object?>` — a flat dictionary of property names to their most recent values.

**Behavior details:**
- Processes entries in ascending `Sequence` order.
- For each entry, applies the `New` value of every changed property to the running state dictionary.
- The result reflects the state as of the **last** diff in the sequence.

**Use cases:**
- Viewing the current state of a deleted entity.
- Time-travel queries: pass only diffs up to a specific timestamp.
- Snapshot reconstruction for debugging or compliance.

---

## End-to-End Usage

### Basic Audit Logging

```csharp
public class MyDbContext : DbContext
{
    private readonly string _currentUserId;

    public MyDbContext(DbContextOptions options, ICurrentUserService userService)
        : base(options)
    {
        _currentUserId = userService.UserId;
    }

    public override async Task<int> SaveChangesAsync(CancellationToken ct = default)
    {
        // 1. Capture diffs BEFORE saving
        var pending = ChangeTracker.CapturePendingAuditDiffs(userId: _currentUserId);

        // 2. Perform the actual save
        var result = await base.SaveChangesAsync(ct);

        // 3. Finalize AFTER saving (PKs are now resolved)
        var auditRecords = pending.FinalizeAuditDiffJson();

        // 4. Persist / ship the audit records
        foreach (var record in auditRecords)
        {
            var json = JsonSerializer.Serialize(record);
            // e.g. write to AuditLogs table, send to a queue, write to a file...
        }

        return result;
    }
}
```

---

### With Domain Events

If your entity implements `IGeneratesDomainEvents`, events are automatically captured inside the audit entry:

```csharp
public class Order : IGeneratesDomainEvents
{
    public int Id { get; private set; }
    public string Status { get; private set; }
    public IList<object> Events { get; } = new List<object>();

    public void Ship()
    {
        Status = "Shipped";
        Events.Add(new OrderShipped(Id, DateTimeOffset.UtcNow));
    }
}
```

When `CapturePendingAuditDiffs` runs, it reads `entity.Events` and wraps each event in a `DomainEventEnvelope`. The `Payload` field contains the original event object and can be serialized alongside the diff.

> **Note:** `CapturePendingAuditDiffs` reads but does **not** clear the events list. Events are cleared separately by `DispatchDomainEvents` or `PublishDomainEvents` from `ChangeTrackerExtensions`.

---

### Overriding SaveChangesAsync

A complete, production-ready override pattern:

```csharp
public override async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
{
    // Apply conventional audit stamps (CreatedOn, ModifiedOn, etc.)
    ChangeTracker.ApplyAuditValues(_currentUserId);
    ChangeTracker.ApplySoftDeletion(_currentUserId);

    // Capture audit diffs before the save
    var pending = ChangeTracker.CapturePendingAuditDiffs(userId: _currentUserId);

    // Save to database
    var rows = await base.SaveChangesAsync(cancellationToken);

    // Finalize and persist audit records
    if (pending.Count > 0)
    {
        var records = pending.FinalizeAuditDiffJson();
        var auditEntities = records
            .Select(r => new AuditLog { Json = JsonSerializer.Serialize(r) })
            .ToList();

        // Use a separate DbContext or direct ADO.NET to avoid re-triggering SaveChanges
        await _auditDbContext.AuditLogs.AddRangeAsync(auditEntities, cancellationToken);
        await _auditDbContext.SaveChangesAsync(cancellationToken);
    }

    // Dispatch domain events after save
    await ChangeTracker.PublishDomainEvents(_publisher);

    return rows;
}
```

---

### Reconstructing Entity State

```csharp
// Load all audit records for a specific entity from your audit log store
var auditHistory = await auditStore
    .GetAuditsByEntityAsync(entityType: "MyApp.Domain.Order", primaryKey: 101);

// Replay all changes to get the final known state
var finalState = auditHistory
    .OrderBy(a => a.Sequence)
    .ReconstructState();

Console.WriteLine(finalState["Status"]);   // "Shipped"
Console.WriteLine(finalState["TotalAmount"]); // 250.00

// Time-travel: get state as of a specific date
var stateOnDate = auditHistory
    .Where(a => a.Timestamp <= new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero))
    .ReconstructState();
```

---

## Domain Events Support

The audit feature integrates with `IGeneratesDomainEvents` from `SW.PrimitiveTypes`. When entities raise domain events before a save, those events are bundled into the audit entry's `DomainEvents` collection.

Each event is wrapped as a `DomainEventEnvelope` with:

| Field       | Value |
|-------------|-------|
| `EventId`   | Freshly generated `Guid` — unique per event instance |
| `EventType` | `ev.GetType().FullName` — for deserialization/routing |
| `EventName` | `ev.GetType().Name` — short name for display/logging |
| `Payload`   | The original event object — serialize to JSON for storage |

This means your audit log can act as an **event log** too, giving you a single source of truth for what happened to an entity and what events it raised.

---

## Difference vs ChangeTracker Extensions

`SW.EfCoreExtensions` also provides `ChangeTrackerExtensions` with methods like `ApplyAuditValues` and `ApplySoftDeletion`. These two features serve different purposes:

| Feature | `ChangeTrackerExtensions` | `AuditBuilderExtension` |
|---------|--------------------------|------------------------|
| **Purpose** | Mutates entity properties before save (stamps) | Captures a read-only diff of what changed |
| **Output** | Modifies the entity in place | Returns audit records |
| **Timestamp** | `DateTime.UtcNow` (non-offset) | `DateTimeOffset.UtcNow` (timezone-safe) |
| **Use case** | Setting `CreatedOn`, `ModifiedBy`, soft-delete | Building an audit log / event sourcing |
| **Call timing** | Before `SaveChanges` | Capture before, finalize after `SaveChanges` |
| **Domain events** | Dispatches events | Captures events into the audit entry |

They are **complementary** — use both together for full audit coverage:

```csharp
// Stamp entity fields
ChangeTracker.ApplyAuditValues(userId);
ChangeTracker.ApplySoftDeletion(userId);

// Then capture the diff (including the newly stamped fields)
var pending = ChangeTracker.CapturePendingAuditDiffs(userId);
await base.SaveChangesAsync(ct);
var records = pending.FinalizeAuditDiffJson();
```

---

## Notes & Best Practices

- **Always call `FinalizeAuditDiffJson` after `SaveChanges`**, not before. For entities in `Added` state, the database-generated PK is only available after the insert.
- **Store audit records in a separate table or database** to avoid re-triggering your `SaveChanges` override.
- **`CorrelationId`** ties all changes from a single `SaveChanges` call together. Store and index it for grouped queries (e.g. "show me everything that changed in this request").
- **`Sequence`** is 1-based and local to each `SaveChanges` call. To get a global ordering, sort by `Timestamp` then `Sequence`.
- **Temporary properties** (shadow properties, not-yet-generated auto-increment PKs) are automatically excluded from `Changes` to keep the diff clean.
- **`ReconstructState` does not validate** that all diffs belong to the same entity — it is your responsibility to filter by entity type and primary key before calling it.
- The `Timestamp` uses `DateTimeOffset.UtcNow` and is **shared across all entries in the same batch** — this gives a consistent point-in-time snapshot per transaction rather than per entity.

