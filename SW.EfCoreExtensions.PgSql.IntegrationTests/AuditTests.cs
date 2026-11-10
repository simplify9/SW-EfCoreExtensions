using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Npgsql;

namespace SW.EfCoreExtensions.PgSql.IntegrationTests;

public class Delivery
{
    public int Id { get; set; }
    public string Number { get; set; }
    public long ChangeVersion { get; set; }
    public bool Printed { get; set; }
    public Place Destination { get; set; } = new();
    public Dictionary<string, string> Extra { get; set; } = new();
    public List<DeliveryItem> Items { get; set; } = new();
    public List<Box> Boxes { get; set; } = new();
}

public class Place
{
    public string City { get; set; }
    public string Street { get; set; }
}

public class DeliveryItem
{
    public int Id { get; set; }
    public string Sku { get; set; }
    public decimal Quantity { get; set; }
}

public class Box
{
    public int Id { get; set; }
    public int DeliveryId { get; set; }
    public string Number { get; set; }
    public List<BoxItem> Items { get; set; } = new();
}

public class BoxItem
{
    public int Id { get; set; }
    public int BoxId { get; set; }
    public string Sku { get; set; }
    public decimal Quantity { get; set; }
    public string[] Serials { get; set; }
}

/// <summary>Mapped the way applications commonly map theirs on PostgreSQL: owned columns, jsonb through dynamic JSON, native arrays, HiLo keys.</summary>
public class DeliveryContext(DbContextOptions<DeliveryContext> options) : DbContext(options)
{
    public static readonly AuditModel Audit = AuditModel.Configure(a =>
    {
        a.Root<Delivery>("delivery", key: d => d.Number, version: d => d.ChangeVersion);
        a.Entity<DeliveryItem>("item").Label(i => i.Sku).MatchBy(i => i.Sku);
        a.Entity<Box>("box").Label(b => b.Number);
        a.Entity<BoxItem>("boxItem").Label(i => i.Sku);
    });

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("audit");
        modelBuilder.UseHiLo("audit_hilo", "audit");
        modelBuilder.Entity<Delivery>(b =>
        {
            b.OwnsOne(d => d.Destination);
            b.Property(d => d.Extra).HasColumnType("jsonb");
            b.HasMany(d => d.Items).WithOne().HasForeignKey("delivery_id").IsRequired(false);
            b.HasMany(d => d.Boxes).WithOne().HasForeignKey(x => x.DeliveryId);
        });
        modelBuilder.Entity<Box>().HasMany(x => x.Items).WithOne().HasForeignKey(i => i.BoxId);
    }
}

[TestClass]
public class AuditTests
{
    static DbContextOptions<DeliveryContext> options;

    [ClassInitialize]
    public static async Task Start(TestContext _)
    {
        var source = new NpgsqlDataSourceBuilder(OrdersDatabase.ConnectionString);
        source.EnableDynamicJson();
        options = new DbContextOptionsBuilder<DeliveryContext>().UseNpgsql(source.Build()).Options;

        await using var db = New();
        await db.Database.ExecuteSqlRawAsync("CREATE SCHEMA IF NOT EXISTS audit");
        await db.GetService<IRelationalDatabaseCreator>().CreateTablesAsync();
        db.Add(new Delivery
        {
            Number = "D-1",
            Destination = new Place { City = "Lyon", Street = "1 Rue A" },
            Extra = new Dictionary<string, string> { ["gift"] = "no" },
            Items = { new DeliveryItem { Sku = "A", Quantity = 2 } },
            Boxes = { new Box { Number = "B1", Items = { new BoxItem { Sku = "A", Quantity = 1, Serials = new[] { "s1" } } } } },
        });
        // Owned values need a non-null column: EF loads an all-null one as null and then never
        // tracks the object a property initializer left there, so its edits are neither saved nor audited.
        db.Add(new Delivery { Number = "D-2", Destination = new Place { City = "Lille" } });
        db.Add(new Delivery { Number = "D-3", Destination = new Place { City = "Metz" } });
        await db.SaveChangesAsync();
    }

    static DeliveryContext New() => new(options);

    static Task<int> Id(DeliveryContext db, string number) =>
        db.Set<Delivery>().Where(d => d.Number == number).Select(d => d.Id).SingleAsync();

    [TestMethod]
    public async Task New_rows_have_their_hilo_keys_before_the_save()
    {
        await using var db = New();
        var delivery = new Delivery { Number = "D-9", Items = { new DeliveryItem { Sku = "Z", Quantity = 1 } } };
        db.Add(delivery);

        var capture = await db.ChangeTracker.CaptureChangeSetsAsync(Audit);

        Assert.IsTrue(capture.KeysAreFinal);
        var set = capture.Complete().Single();
        Assert.AreEqual(delivery.Id, set.RootId);
        Assert.AreEqual(delivery.Items[0].Id, set.Changes.Single(c => c.Entity == "item").Key["Id"]);
    }

    static AuditModel Audit => DeliveryContext.Audit;

    [TestMethod]
    public async Task A_replaced_owned_value_on_a_shared_table_writes_only_what_differs()
    {
        await using var db = New();
        var delivery = await db.Set<Delivery>().SingleAsync(d => d.Number == "D-1");
        delivery.Destination = new Place { City = "Paris", Street = "1 Rue A" };

        var change = (await db.ChangeTracker.CaptureChangeSetsAsync(Audit)).Complete().Single().Changes.Single();

        CollectionAssert.AreEqual(new[] { "destination.city" }, change.Fields.Keys.ToArray());
        Assert.AreEqual(new AuditFieldChange("Lyon", "Paris"), change.Fields["destination.city"]);
    }

    [TestMethod]
    public async Task A_jsonb_dictionary_is_diffed_per_key()
    {
        await using var db = New();
        var delivery = await db.Set<Delivery>().SingleAsync(d => d.Number == "D-1");
        delivery.Extra = new Dictionary<string, string> { ["gift"] = "yes" };

        var fields = (await db.ChangeTracker.CaptureChangeSetsAsync(Audit)).Complete().Single().Changes.Single().Fields;

        Assert.AreEqual(new AuditFieldChange("no", "yes"), fields["extra.gift"]);
        Assert.AreEqual(1, fields.Count);
    }

    [TestMethod]
    public async Task A_native_array_change_is_captured_on_a_grandchild_whose_parent_is_not_loaded()
    {
        await using var db = New();
        var item = await db.Set<BoxItem>().SingleAsync(i => i.Sku == "A");
        item.Serials = new[] { "s1", "s2" };

        var set = (await db.ChangeTracker.CaptureChangeSetsAsync(Audit)).Complete().Single();

        Assert.AreEqual("D-1", set.RootKey);
        var serials = set.Changes.Single().Fields["serials"];
        CollectionAssert.AreEqual(new[] { "s1" }, (string[])serials.Old);
        CollectionAssert.AreEqual(new[] { "s1", "s2" }, (string[])serials.New);
    }

    /// <summary>
    /// The point of the version: two writers to one root get consecutive numbers. The second one's
    /// bump waits on the row lock the first holds until it commits.
    /// </summary>
    [TestMethod]
    public async Task Concurrent_writers_to_one_root_get_consecutive_versions()
    {
        await using var first = New();
        await using var second = New();
        var id = await Id(first, "D-2");
        var start = await first.Set<Delivery>().Where(d => d.Id == id).Select(d => d.ChangeVersion).SingleAsync();

        await using var tx1 = await first.Database.BeginTransactionAsync();
        (await first.Set<Delivery>().SingleAsync(d => d.Id == id)).Printed = true;
        var capture1 = await first.ChangeTracker.CaptureChangeSetsAsync(Audit);
        await capture1.BumpVersionsAsync();

        await using var tx2 = await second.Database.BeginTransactionAsync();
        (await second.Set<Delivery>().SingleAsync(d => d.Id == id)).Destination.City = "Nice";
        var capture2 = await second.ChangeTracker.CaptureChangeSetsAsync(Audit);
        var bump2 = capture2.BumpVersionsAsync();

        await Task.Delay(500);
        Assert.AreEqual(1, capture2.ChangeSets.Count);
        Assert.IsFalse(bump2.IsCompleted, "the second bump must wait for the first transaction");

        await first.SaveChangesAsync();
        await tx1.CommitAsync();
        await bump2;
        await second.SaveChangesAsync();
        await tx2.CommitAsync();

        Assert.AreEqual(start + 1, capture1.ChangeSets.Single().Version);
        Assert.AreEqual(start + 2, capture2.ChangeSets.Single().Version);
        await using var check = New();
        var stored = await check.Set<Delivery>().AsNoTracking().SingleAsync(d => d.Id == id);
        Assert.AreEqual(start + 2, stored.ChangeVersion);
        Assert.IsTrue(stored.Printed, "the first save's change survived");
        Assert.AreEqual("Nice", stored.Destination.City);
    }

    /// <summary>Roots are locked in key order, so two writers touching the same roots in opposite orders queue instead of deadlocking.</summary>
    [TestMethod]
    public async Task Writers_touching_the_same_roots_in_opposite_orders_do_not_deadlock()
    {
        await using var first = New();
        await using var second = New();
        var a = await Id(first, "D-2");
        var b = await Id(first, "D-3");

        await using var tx1 = await first.Database.BeginTransactionAsync();
        foreach (var id in new[] { b, a })
            (await first.Set<Delivery>().SingleAsync(d => d.Id == id)).Printed ^= true;
        var capture1 = await first.ChangeTracker.CaptureChangeSetsAsync(Audit);
        await capture1.BumpVersionsAsync();

        await using var tx2 = await second.Database.BeginTransactionAsync();
        foreach (var id in new[] { a, b })
            (await second.Set<Delivery>().SingleAsync(d => d.Id == id)).Destination.Street = "x";
        var capture2 = await second.ChangeTracker.CaptureChangeSetsAsync(Audit);
        var bump2 = capture2.BumpVersionsAsync();

        await Task.Delay(300);
        await first.SaveChangesAsync();
        await tx1.CommitAsync();
        await bump2; // a deadlock would throw here
        await second.SaveChangesAsync();
        await tx2.CommitAsync();

        Assert.AreEqual(2, capture1.ChangeSets.Count);
        Assert.AreEqual(2, capture2.ChangeSets.Count);
        foreach (var set in capture2.ChangeSets)
            Assert.AreEqual(capture1.ChangeSets.Single(s => Equals(s.RootId, set.RootId)).Version + 1, set.Version);
    }

    [TestMethod]
    public async Task The_postgres_bump_reads_back_the_root_key_type_and_leaves_an_untracked_root_alone()
    {
        await using var db = New();
        await using var tx = await db.Database.BeginTransactionAsync();
        var box = await db.Set<Box>().SingleAsync(x => x.Number == "B1");
        box.Number = "B1-renamed";
        var capture = await db.ChangeTracker.CaptureChangeSetsAsync(Audit);
        await capture.BumpVersionsAsync();
        await db.SaveChangesAsync();
        await tx.RollbackAsync();

        var set = capture.ChangeSets.Single();
        Assert.IsNotNull(set.Version);
        Assert.IsInstanceOfType(set.RootId, typeof(int));
    }
}
