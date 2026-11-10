using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace SW.EfCoreExtensions.UnitTests.Auditing
{
    [TestClass]
    public class AggregateAuditTests
    {
        AuditDbContext db;
        SqliteConnection connection;

        [TestInitialize]
        public void Init() => (db, connection) = AuditDbContext.Create();

        [TestCleanup]
        public void Cleanup()
        {
            db.Dispose();
            connection.Dispose();
        }

        /// <summary>An order with explicit keys, saved, in a fresh context: what an edit starts from.</summary>
        async Task<AuditDbContext> Seeded()
        {
            db.Add(new Order
            {
                Id = 1, Number = "ORD-1", ShipTo = new Address { City = "Lyon", Street = "1 Rue A" },
                Notes = new Dictionary<string, string> { ["gift"] = "no" },
                Lines = { new OrderLine { Id = 10, Sku = "A", Quantity = 2 }, new OrderLine { Id = 11, Sku = "B", Quantity = 1 } },
                Parcels = { new Parcel { Id = 20, Number = "P1", Items = { new ParcelItem { Id = 30, Sku = "A", Quantity = 1, Serials = new[] { "s1" } } } } },
            });
            db.Add(new Order { Id = 2, Number = "ORD-2" });
            await db.SaveChangesAsync();
            return db.Fresh();
        }

        static async Task<IReadOnlyList<AuditChangeSet>> Capture(AuditDbContext context)
        {
            var capture = await context.ChangeTracker.CaptureChangeSetsAsync(AuditDbContext.Audit);
            return capture.Complete();
        }

        [TestMethod]
        public async Task A_child_change_lands_under_its_root_with_only_the_changed_field()
        {
            var context = await Seeded();
            var order = await context.Set<Order>().Include(o => o.Lines).SingleAsync(o => o.Id == 1);
            order.Lines.Single(l => l.Sku == "A").Quantity = 3;

            var set = (await Capture(context)).Single();

            Assert.AreEqual("order", set.Root);
            Assert.AreEqual(1, set.RootId);
            Assert.AreEqual("ORD-1", set.RootKey);
            var change = set.Changes.Single();
            Assert.AreEqual("line", change.Entity);
            Assert.AreEqual("A", change.Label);
            Assert.AreEqual(AuditOperation.Modified, change.Operation);
            Assert.AreEqual(10, change.Key["Id"]);
            CollectionAssert.AreEqual(new[] { "quantity" }, change.Fields.Keys.ToArray());
            Assert.AreEqual(new AuditFieldChange(2m, 3m), change.Fields["quantity"]);
        }

        [TestMethod]
        public async Task A_new_root_is_one_change_set_with_its_children_and_keys_come_after_the_save()
        {
            var order = new Order { Number = "ORD-9", ShipTo = new Address { City = "Paris" }, Lines = { new OrderLine { Sku = "Z", Quantity = 4 } } };
            order.Events.Add(new OrderPlaced { Number = "ORD-9" });
            db.Add(order);

            var capture = await db.ChangeTracker.CaptureChangeSetsAsync(AuditDbContext.Audit);
            Assert.IsFalse(capture.KeysAreFinal, "SQLite generates the keys");
            Assert.ThrowsException<InvalidOperationException>(() => capture.Complete());

            await db.SaveChangesAsync();
            var set = capture.Complete().Single();

            Assert.AreEqual(order.Id, set.RootId);
            Assert.AreEqual("order", set.Changes[0].Entity, "the root's own change comes first");
            Assert.AreEqual(AuditOperation.Added, set.Changes[0].Operation);
            Assert.AreEqual("Paris", set.Changes[0].Fields["shipTo.city"].New);
            Assert.IsFalse(set.Changes[0].Fields.ContainsKey("shipTo.street"), "nulls are left out of an add");
            Assert.IsFalse(set.Changes[0].Fields.ContainsKey("modifiedOn"), "ignored everywhere");
            Assert.IsFalse(set.Changes[0].Fields.ContainsKey("changeVersion"), "the version is the change set's, not a field");
            var line = set.Changes.Single(c => c.Entity == "line");
            Assert.AreEqual(order.Lines[0].Id, line.Key["Id"]);
            Assert.IsFalse(line.Fields.ContainsKey("order_id"), "the foreign key to the root is implied");
            Assert.IsInstanceOfType(set.DomainEvents.Single(), typeof(OrderPlaced));
        }

        [TestMethod]
        public async Task A_grandchild_whose_parent_is_not_loaded_is_traced_to_its_root_with_one_lookup()
        {
            var context = await Seeded();
            var item = await context.Set<ParcelItem>().SingleAsync(i => i.Id == 30); // parcel not loaded
            item.Quantity = 5;

            var set = (await Capture(context)).Single();

            Assert.AreEqual(1L, Convert.ToInt64(set.RootId));
            Assert.AreEqual("ORD-1", set.RootKey, "read with the lookup, since the root isn't tracked");
            Assert.IsFalse(context.ChangeTracker.Entries<Order>().Any());
            Assert.AreEqual("parcelItem", set.Changes.Single().Entity);
        }

        [TestMethod]
        public async Task A_replaced_row_with_the_same_match_key_reads_as_one_modification()
        {
            var context = await Seeded();
            var order = await context.Set<Order>().Include(o => o.Lines).SingleAsync(o => o.Id == 1);
            var old = order.Lines.Single(l => l.Sku == "A");
            context.Remove(old);
            var replacement = new OrderLine { Id = 12, Sku = "A", Quantity = 7 };
            context.Add(replacement); // as HiLo keys are: assigned on Add, so the row is Added
            order.Lines.Add(replacement);

            var change = (await Capture(context)).Single().Changes.Single();

            Assert.AreEqual(AuditOperation.Modified, change.Operation);
            CollectionAssert.AreEqual(new[] { "quantity" }, change.Fields.Keys.ToArray());
            Assert.AreEqual(12, change.Key["Id"]);
            Assert.AreEqual(10, change.ReplacedKey["Id"]);
        }

        [TestMethod]
        public async Task A_row_replaced_by_an_identical_one_is_no_change()
        {
            var context = await Seeded();
            var order = await context.Set<Order>().Include(o => o.Lines).SingleAsync(o => o.Id == 1);
            context.Remove(order.Lines.Single(l => l.Sku == "B"));
            var replacement = new OrderLine { Id = 13, Sku = "B", Quantity = 1 };
            context.Add(replacement);
            order.Lines.Add(replacement);

            Assert.AreEqual(0, (await Capture(context)).Count);
        }

        [TestMethod]
        public async Task A_replaced_owned_value_writes_only_the_fields_that_differ()
        {
            var context = await Seeded();
            var order = await context.Set<Order>().SingleAsync(o => o.Id == 1);
            order.ShipTo = new Address { City = "Paris", Street = "1 Rue A" };

            var change = (await Capture(context)).Single().Changes.Single();

            Assert.AreEqual("order", change.Entity);
            CollectionAssert.AreEqual(new[] { "shipTo.city" }, change.Fields.Keys.ToArray());
            Assert.AreEqual(new AuditFieldChange("Lyon", "Paris"), change.Fields["shipTo.city"]);
        }

        [TestMethod]
        public async Task An_owned_value_edited_in_place_is_captured_although_the_owner_row_is_unchanged()
        {
            var context = await Seeded();
            var order = await context.Set<Order>().SingleAsync(o => o.Id == 1);
            order.ShipTo.Street = "2 Rue B";

            var change = (await Capture(context)).Single().Changes.Single();

            Assert.AreEqual(new AuditFieldChange("1 Rue A", "2 Rue B"), change.Fields["shipTo.street"]);
        }

        [TestMethod]
        public async Task A_dictionary_is_diffed_per_key()
        {
            var context = await Seeded();
            var order = await context.Set<Order>().SingleAsync(o => o.Id == 1);
            order.Notes = new Dictionary<string, string> { ["gift"] = "yes", ["door"] = "4B" };

            var fields = (await Capture(context)).Single().Changes.Single().Fields;

            Assert.AreEqual(new AuditFieldChange("no", "yes"), fields["notes.gift"]);
            Assert.AreEqual(new AuditFieldChange(null, "4B"), fields["notes.door"]);
            Assert.AreEqual(2, fields.Count);
        }

        [TestMethod]
        public async Task An_ignored_property_alone_is_no_change()
        {
            var context = await Seeded();
            var order = await context.Set<Order>().SingleAsync(o => o.Id == 1);
            order.ModifiedOn = DateTime.UtcNow;

            Assert.AreEqual(0, (await Capture(context)).Count);
        }

        [TestMethod]
        public async Task A_soft_delete_reads_as_removed()
        {
            var context = await Seeded();
            var parcel = await context.Set<Parcel>().SingleAsync(p => p.Id == 20);
            parcel.Deleted = true;

            var change = (await Capture(context)).Single().Changes.Single();

            Assert.AreEqual(AuditOperation.Removed, change.Operation);
            Assert.AreEqual("P1", change.Label);
            Assert.IsFalse(change.Fields.ContainsKey("deleted"));
        }

        [TestMethod]
        public async Task A_hard_delete_writes_the_old_values()
        {
            var context = await Seeded();
            var item = await context.Set<ParcelItem>().Include(i => i.Parcel).SingleAsync(i => i.Id == 30);
            context.Remove(item);

            var change = (await Capture(context)).Single().Changes.Single();

            Assert.AreEqual(AuditOperation.Removed, change.Operation);
            Assert.AreEqual(1m, change.Fields["quantity"].Old);
            CollectionAssert.AreEqual(new[] { "s1" }, (string[])change.Fields["serials"].Old);
            Assert.AreEqual(30, change.Key["Id"]);
        }

        [TestMethod]
        public async Task A_row_moved_to_another_root_leaves_one_and_joins_the_other()
        {
            var context = await Seeded();
            var one = await context.Set<Order>().Include(o => o.Lines).SingleAsync(o => o.Id == 1);
            var two = await context.Set<Order>().Include(o => o.Lines).SingleAsync(o => o.Id == 2);
            var line = one.Lines.Single(l => l.Sku == "B");
            one.Lines.Remove(line);
            two.Lines.Add(line);

            var sets = await Capture(context);

            Assert.AreEqual(AuditOperation.Removed, sets.Single(s => (int)s.RootId == 1).Changes.Single().Operation);
            var joined = sets.Single(s => (int)s.RootId == 2).Changes.Single();
            Assert.AreEqual(AuditOperation.Added, joined.Operation);
            Assert.AreEqual("B", joined.Fields["sku"].New);
        }

        [TestMethod]
        public async Task A_row_detached_by_a_nulled_foreign_key_is_removed_from_its_root()
        {
            var context = await Seeded();
            var order = await context.Set<Order>().Include(o => o.Lines).SingleAsync(o => o.Id == 1);
            order.Lines.Remove(order.Lines.Single(l => l.Sku == "B")); // optional relationship: the FK goes null

            var change = (await Capture(context)).Single().Changes.Single();

            Assert.AreEqual(AuditOperation.Removed, change.Operation);
            Assert.AreEqual("B", change.Label);
            Assert.AreEqual(11, change.Key["Id"]);
        }

        [TestMethod]
        public async Task A_non_root_foreign_key_is_an_ordinary_field()
        {
            var context = await Seeded();
            var parcel = await context.Set<Parcel>().SingleAsync(p => p.Id == 20);
            parcel.LineId = 10;

            var change = (await Capture(context)).Single().Changes.Single();

            Assert.AreEqual(new AuditFieldChange(null, 10), change.Fields["lineId"]);
        }

        [TestMethod]
        public async Task Entities_outside_the_model_are_never_captured()
        {
            db.Add(new Customer { Name = "x" });
            var capture = await db.ChangeTracker.CaptureChangeSetsAsync(AuditDbContext.Audit);
            Assert.AreEqual(0, capture.ChangeSets.Count);
        }

        [TestMethod]
        public async Task Versions_increase_by_one_per_save_and_a_new_root_starts_at_one()
        {
            var context = await Seeded();

            for (var expected = 1L; expected <= 2; expected++)
            {
                await using var tx = await context.Database.BeginTransactionAsync();
                var order = await context.Set<Order>().SingleAsync(o => o.Id == 1);
                order.Printed = !order.Printed;
                var capture = await context.ChangeTracker.CaptureChangeSetsAsync(AuditDbContext.Audit);
                await capture.BumpVersionsAsync();
                Assert.AreEqual(expected, capture.ChangeSets.Single().Version);
                await context.SaveChangesAsync();
                await tx.CommitAsync();
                Assert.AreEqual(expected, order.ChangeVersion);
            }

            await using (var tx = await context.Database.BeginTransactionAsync())
            {
                context.Add(new Order { Id = 3, Number = "ORD-3" });
                var capture = await context.ChangeTracker.CaptureChangeSetsAsync(AuditDbContext.Audit);
                await capture.BumpVersionsAsync();
                Assert.AreEqual(1L, capture.ChangeSets.Single().Version);
                await context.SaveChangesAsync();
                await tx.CommitAsync();
            }

            var stored = await context.Fresh().Set<Order>().AsNoTracking().OrderBy(o => o.Id).Select(o => o.ChangeVersion).ToListAsync();
            CollectionAssert.AreEqual(new[] { 2L, 0L, 1L }, stored);
        }

        [TestMethod]
        public async Task A_version_bump_for_an_untracked_root_leaves_it_unwritten_by_the_save()
        {
            var context = await Seeded();
            await using var tx = await context.Database.BeginTransactionAsync();
            var item = await context.Set<ParcelItem>().SingleAsync(i => i.Id == 30);
            item.Quantity = 9;
            var capture = await context.ChangeTracker.CaptureChangeSetsAsync(AuditDbContext.Audit);
            await capture.BumpVersionsAsync();
            await context.SaveChangesAsync();
            await tx.CommitAsync();

            Assert.AreEqual(1L, capture.ChangeSets.Single().Version);
            Assert.AreEqual(1L, await context.Fresh().Set<Order>().Where(o => o.Id == 1).Select(o => o.ChangeVersion).SingleAsync());
        }

        [TestMethod]
        public async Task A_tracked_unchanged_root_stays_unchanged_after_its_version_bump()
        {
            var context = await Seeded();
            await using var tx = await context.Database.BeginTransactionAsync();
            var order = await context.Set<Order>().Include(o => o.Lines).SingleAsync(o => o.Id == 1);
            order.Lines[0].Quantity = 8;
            var capture = await context.ChangeTracker.CaptureChangeSetsAsync(AuditDbContext.Audit);
            await capture.BumpVersionsAsync();

            Assert.AreEqual(EntityState.Unchanged, context.Entry(order).State);
            Assert.AreEqual(1L, order.ChangeVersion);
        }

        [TestMethod]
        public async Task A_version_bump_outside_a_transaction_is_refused()
        {
            var context = await Seeded();
            var order = await context.Set<Order>().SingleAsync(o => o.Id == 1);
            order.Printed = true;
            var capture = await context.ChangeTracker.CaptureChangeSetsAsync(AuditDbContext.Audit);
            await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => capture.BumpVersionsAsync());
        }

        [TestMethod]
        public void The_vocabulary_lists_every_field_per_root_and_entity()
        {
            var lines = AuditDbContext.Audit.Describe(db.Model).Select(l => l.ToString()).ToList();

            CollectionAssert.Contains(lines, "order\torder\tprinted\tboolean");
            CollectionAssert.Contains(lines, "order\torder\tshipTo.city\tstring?");
            CollectionAssert.Contains(lines, "order\torder\tnotes.*\tstring?");
            CollectionAssert.Contains(lines, "order\tline\tquantity\tnumber");
            CollectionAssert.Contains(lines, "order\tparcel\tlineId\tinteger?");
            CollectionAssert.Contains(lines, "order\tparcelItem\tserials\tlist<string>?");
            Assert.IsFalse(lines.Any(l => l.Contains("modifiedOn") || l.Contains("changeVersion") || l.Contains("order_id") || l.Contains("\torderId")),
                string.Join("\n", lines));
        }

        [TestMethod]
        public void An_audited_entity_with_no_way_to_a_root_is_refused()
        {
            var model = AuditModel.Configure(a =>
            {
                a.Root<Order>("order");
                a.Entity<Customer>();
            });
            Assert.ThrowsException<InvalidOperationException>(() => model.Describe(db.Model));
        }
    }
}
