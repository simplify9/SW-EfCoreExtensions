# Aggregate audit — change sets per aggregate root

> Packages: **`SimplyWorks.EfCoreExtensions`** and **`SimplyWorks.EfCoreExtensions.PgSql`** (same source)
> Namespace: `SW.EfCoreExtensions`
> Entry points: `AuditModel.Configure`, `ChangeTracker.CaptureChangeSetsAsync`

`CapturePendingAuditDiffs` ([AUDIT.md](AUDIT.md)) gives one entry per changed entity. That answers "what happened to this row". It doesn't answer "what happened to this order", once the order's lines, parcels and parcel contents live in tables of their own.

The aggregate audit answers that. One `SaveChanges` produces **one change set per aggregate root it touched**, holding the root's own change and the changes of every audited entity under it, with only the fields that changed.

It only captures. Where the change sets go (a table, an outbox, object storage, a message) is up to the application.

## Configure

```csharp
public class ShopContext : DbContext
{
    public static readonly AuditModel Audit = AuditModel.Configure(a =>
    {
        a.IgnoreProperties("CreatedOn", "CreatedBy", "ModifiedOn", "ModifiedBy");
        a.SoftDelete("Deleted");

        a.Root<Order>("order", key: o => o.Number, version: o => o.ChangeVersion);
        a.Entity<OrderLine>("line").Label(l => l.Sku).MatchBy(l => l.Sku);
        a.Entity<Parcel>().Label(p => p.Number);
        a.Entity<ParcelItem>("parcelItem").Label(i => new { i.Sku, i.Location });
    });
}
```

- **Only listed entity types are audited.** An entity that isn't a root belongs to every root reachable by following its foreign keys from dependent to principal. It takes the shortest path to each root, through any type, listed or not. `ParcelItem → Parcel → Order` needs no code. An entity with no path to a root is refused when the model is first used.
- **Owned types** (`OwnsOne`, `OwnsMany`, JSON columns) are folded into their owner as dotted fields: `shipTo.city`, `lines[3].sku`.
- **The configuration lives outside the EF model on purpose.** Custom model annotations would be written into every migration snapshot.

| Option | Effect |
|---|---|
| `Root<T>(name, key, version)` | An aggregate root. `key` is a business key reported beside the primary key. `version` is an `int`/`long` column that `BumpVersionsAsync` increases by one per change set. |
| `Entity<T>(name)` | An audited entity. The name defaults to the camelCased type name. |
| `.Label(x => ...)` | Properties that name a row on screen, joined by ` / `. |
| `.MatchBy(x => ...)` | Folds a removed and an added row with the same values into one modification (below). |
| `.Ignore(...)`, `IgnoreProperties(...)` | Leaves properties out of one entity, or of all. Owned members by dotted CLR path: `"ShipTo.Notes"`. Applies to adds as well, so a secret is never written. |
| `.FieldName(x => x.Prop, "name")` | Pins a field's name, so renaming the C# property doesn't change what history says. |
| `SoftDelete("Deleted")` | A `false → true` change of that flag reads as `Removed`. |

## Capture

```csharp
public override async Task<int> SaveChangesAsync(CancellationToken ct = default)
{
    ChangeTracker.ApplyAuditValues(user);      // anything that still edits entities runs first
    ChangeTracker.ApplySoftDeletion(user);

    await using var tx = Database.CurrentTransaction == null ? await Database.BeginTransactionAsync(ct) : null;
    var capture = await ChangeTracker.CaptureChangeSetsAsync(Audit, ct);
    await capture.BumpVersionsAsync(ct);       // optional; needs the transaction

    if (capture.KeysAreFinal)                  // HiLo, sequences, client keys: write in the same save
        Write(capture.Complete());
    var saved = await base.SaveChangesAsync(ct);
    if (!capture.KeysAreFinal) { Write(capture.Complete()); await base.SaveChangesAsync(ct); }

    if (tx != null) await tx.CommitAsync(ct);
    return saved;
}
```

Each `AuditChangeSet` has:
- `Id`: a version-7 GUID.
- `CommitId`: shared by every set from one save.
- `CapturedAt`.
- `Root`, `RootId`, `RootKey` and `Version`.
- `Changes`: the root's own change first, then by configuration order.
- `DomainEvents`: the pending events of the root and the changed entities, read before they're dispatched.

Each `AuditChange` has `Entity`, `Key`, `Label`, `Operation` (`Added`, `Modified`, `Removed`) and `Fields`, a map of field name to `AuditFieldChange(Old, New)`. `ReplacedKey` is set when two rows were folded into one.

### What a change contains

- **Modified:** the fields whose values differ, compared with each property's `ValueComparer`. A row whose only changes are ignored properties produces nothing, so audit columns alone never make a change set.
- **Added:** every audited field that isn't null.
- **Removed:** every old value that wasn't null. A soft delete lists what changed with it.
- **Dictionaries** (a `jsonb` `Dictionary<string, string>`, for instance) are diffed per key: `notes.gift: no → yes`.
- **Scalar arrays and lists** are written whole, old and new.
- **Never fields:** primary keys; the foreign key that leads to the root (the change set already says where the row belongs); shadow foreign keys; discriminators; store-generated concurrency tokens such as `xmin`; the root's version.

### Rows that move or get replaced

- **A row moved to another root,** or detached by a nulled optional foreign key, is `Removed` (with its old values) from the root it left, and `Added` to the one it joined.
- **A replaced owned value** (assigning a new `Address` instance) is compared with the old one. Only the fields that differ are written, not every column of both.
- **A replaced row with a match key.** Some code replaces child rows instead of updating them, for example value-equal collections synced with `Except`. With `MatchBy`, a removed and an added row of the same entity and match key, in the same change set, become one `Modified` change with only the differing fields. An identical replacement disappears. If the key isn't unique in the set, both rows are reported as they are.

### Parents that aren't loaded

When an audited row's parent isn't tracked (a parcel item updated without its parcel), the capture looks the parent up. It runs one query per parent entity type, inside the current transaction, and only selects the key and foreign-key columns. The root's business key is looked up the same way when the root isn't tracked. Lookups need single-column keys.

### Keys the database generates

Identity keys are temporary until the save. `KeysAreFinal` says whether `Complete()` can run before saving; otherwise call it after, while still inside the transaction.

## Versions

`BumpVersionsAsync()` gives every changed root that has a version column its next value, in one statement per root type, and writes it on each change set. A root inserted by this save starts at 1.

- **It must run inside the transaction that saves the changes;** it refuses otherwise. The rows stay locked until that transaction ends, so two writers to one root get consecutive versions. The second waits for the first to commit.
- **On PostgreSQL the rows are locked in key order** (`WITH locked AS (SELECT … ORDER BY id FOR UPDATE) UPDATE …`), so writers touching the same roots in different orders queue instead of deadlocking.
- **SQLite** uses `UPDATE … RETURNING`. **SQL Server** uses `UPDATE … OUTPUT` (no ordering guarantee). Other providers aren't supported.
- **A tracked root keeps its state.** The new value is set as both current and original, so the save doesn't write it again, and a root that only had child changes stays `Unchanged`.
- **Why a version:** consumers of the change sets (a history view, an index, a projection) can order them, ignore duplicates and late arrivals, and notice a gap. None of that can be added later.

## Vocabulary

`Audit.Describe(context.Model)` lists every field the model can write, one line per root, entity and field:

```
order	order	shipTo.city	string?
order	order	notes.*	string?
order	line	quantity	number
order	parcelItem	serials	list<string>?
```

Commit it and diff it on pull requests. A removed or renamed field changes what history and change events say. The fix for an accidental rename is `.FieldName(...)`.

## Limits

- **Writes that bypass the change tracker are invisible:** `ExecuteUpdate`/`ExecuteDelete` and raw SQL. Audit those yourself.
- **An owned value whose columns are all null loads as null.** If the CLR property has an initializer, EF never tracks the object it left there, so edits to it are neither saved nor audited. Make sure such values have a non-null column, or assign a new instance.
- **No complex types (`ComplexProperty`) and no many-to-many skip navigations yet.**
