using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using SW.PrimitiveTypes;

namespace SW.EfCoreExtensions.UnitTests.Auditing
{
    public class OrderPlaced : IDomainEvent
    {
        public string Number { get; set; }
        public DateTime On { get; } = DateTime.UtcNow;
    }

    /// <summary>An aggregate root shaped like the ones the capture is for: owned values, a dictionary, children, grandchildren.</summary>
    public class Order : IGeneratesDomainEvents
    {
        public int Id { get; set; }
        public string Number { get; set; }
        public long ChangeVersion { get; set; }
        public bool Printed { get; set; }
        public bool Deleted { get; set; }
        public DateTime? ModifiedOn { get; set; }
        public Address ShipTo { get; set; } = new();
        public Dictionary<string, string> Notes { get; set; } = new();
        public List<OrderLine> Lines { get; set; } = new();
        public List<Parcel> Parcels { get; set; } = new();
        public ICollection<IDomainEvent> Events { get; } = new List<IDomainEvent>();
    }

    public class Address
    {
        public string City { get; set; }
        public string Street { get; set; }
    }

    /// <summary>Joined to its order by a shadow, optional foreign key: removing a line from the collection nulls the key instead of deleting the row.</summary>
    public class OrderLine
    {
        public int Id { get; set; }
        public string Sku { get; set; }
        public decimal Quantity { get; set; }
    }

    public class Parcel
    {
        public int Id { get; set; }
        public int OrderId { get; set; }
        public string Number { get; set; }
        public int? LineId { get; set; }
        public bool Deleted { get; set; }
        public List<ParcelItem> Items { get; set; } = new();
    }

    public class ParcelItem
    {
        public int Id { get; set; }
        public int ParcelId { get; set; }
        public Parcel Parcel { get; set; }
        public string Sku { get; set; }
        public decimal Quantity { get; set; }
        public string[] Serials { get; set; }
    }

    /// <summary>Not in the audit model: must never appear.</summary>
    public class Customer
    {
        public int Id { get; set; }
        public string Name { get; set; }
    }

    public class AuditDbContext(DbContextOptions<AuditDbContext> options) : DbContext(options)
    {
        public static readonly AuditModel Audit = AuditModel.Configure(a =>
        {
            a.IgnoreProperties("ModifiedOn");
            a.SoftDelete("Deleted");
            a.Root<Order>("order", key: o => o.Number, version: o => o.ChangeVersion);
            a.Entity<OrderLine>("line").Label(l => l.Sku).MatchBy(l => l.Sku);
            a.Entity<Parcel>().Label(p => p.Number);
            a.Entity<ParcelItem>("parcelItem").Label(i => new { i.Sku }).MatchBy(i => i.Sku);
        });

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            var dictionaryComparer = new ValueComparer<Dictionary<string, string>>(
                (a, b) => a == null ? b == null : b != null && a.Count == b.Count && !a.Except(b).Any(),
                d => d == null ? 0 : d.Aggregate(0, (h, kv) => HashCode.Combine(h, kv.Key, kv.Value)),
                d => d == null ? null : new Dictionary<string, string>(d));
            var arrayComparer = new ValueComparer<string[]>(
                (a, b) => a == null ? b == null : b != null && a.SequenceEqual(b),
                a => a == null ? 0 : a.Aggregate(0, HashCode.Combine),
                a => a == null ? null : a.ToArray());

            modelBuilder.Entity<Order>(b =>
            {
                b.Ignore(o => o.Events);
                b.OwnsOne(o => o.ShipTo);
                b.Property(o => o.Notes).HasConversion(
                        d => JsonSerializer.Serialize(d, (JsonSerializerOptions)null),
                        s => JsonSerializer.Deserialize<Dictionary<string, string>>(s, (JsonSerializerOptions)null))
                    .Metadata.SetValueComparer(dictionaryComparer);
                b.HasMany(o => o.Lines).WithOne().HasForeignKey("order_id").IsRequired(false);
                b.HasMany(o => o.Parcels).WithOne().HasForeignKey(p => p.OrderId);
            });
            modelBuilder.Entity<Parcel>().HasMany(p => p.Items).WithOne(i => i.Parcel).HasForeignKey(i => i.ParcelId);
            modelBuilder.Entity<Parcel>().HasOne<OrderLine>().WithMany().HasForeignKey(p => p.LineId).IsRequired(false);
            modelBuilder.Entity<ParcelItem>().Property(i => i.Serials).HasConversion(
                    a => a == null ? null : string.Join(",", a),
                    s => s == null ? null : s.Split(',', StringSplitOptions.None))
                .Metadata.SetValueComparer(arrayComparer);
            modelBuilder.Entity<Customer>();
        }

        /// <summary>A fresh in-memory database; the open connection keeps it alive.</summary>
        public static (AuditDbContext Context, SqliteConnection Connection) Create()
        {
            var connection = new SqliteConnection("DataSource=:memory:");
            connection.Open();
            var context = new AuditDbContext(new DbContextOptionsBuilder<AuditDbContext>().UseSqlite(connection).Options);
            context.Database.EnsureCreated();
            return (context, connection);
        }

        public AuditDbContext Fresh() =>
            new(new DbContextOptionsBuilder<AuditDbContext>().UseSqlite(Database.GetDbConnection()).Options);
    }
}
