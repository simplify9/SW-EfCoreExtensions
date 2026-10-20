using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Testcontainers.PostgreSql;

namespace SW.EfCoreExtensions.PgSql.IntegrationTests;

public enum OrderStatus { Draft = 0, Placed = 1, Shipped = 2, Cancelled = 3 }

public class Customer
{
    public int Id { get; set; }
    public string Name { get; set; }
    public string Country { get; set; }
}

public class Order
{
    public int Id { get; set; }
    public string Number { get; set; }
    public int CustomerId { get; set; }
    public Customer Customer { get; set; }
    public OrderStatus Status { get; set; }
    public decimal Total { get; set; }
    public decimal? Discount { get; set; }
    public bool Paid { get; set; }
    public long Quantity { get; set; }
    public Guid ExternalId { get; set; }
    public DateTime PlacedOn { get; set; }
    public List<OrderLine> Lines { get; set; } = new();
}

public class OrderLine
{
    public int Id { get; set; }
    public string Sku { get; set; }
    public int Quantity { get; set; }
}

public class OrdersContext(DbContextOptions<OrdersContext> options) : DbContext(options)
{
    public DbSet<Order> Orders => Set<Order>();
    public DbSet<Customer> Customers => Set<Customer>();
}

/// <summary>
/// One Postgres 17 container per test run with a small order book, plus what EF Core did: the SQL
/// it sent and how many times it compiled a query.
/// </summary>
[TestClass]
public static class OrdersDatabase
{
    static PostgreSqlContainer container;
    static DbContextOptions<OrdersContext> options;

    static int compilations;
    static readonly List<string> commands = new();

    public static int Compilations => Volatile.Read(ref compilations);

    public static IReadOnlyList<string> Commands
    {
        get { lock (commands) return commands.ToList(); }
    }

    public static void ClearCommands()
    {
        lock (commands) commands.Clear();
    }

    public static List<Order> All { get; private set; }

    [AssemblyInitialize]
    public static async Task Start(TestContext _)
    {
        container = new PostgreSqlBuilder().WithImage("postgres:17-alpine").Build();
        await container.StartAsync();

        // One options instance for the run, so every context shares EF Core's query cache, as the
        // contexts of one application do.
        options = new DbContextOptionsBuilder<OrdersContext>()
            .UseNpgsql(container.GetConnectionString())
            .LogTo(Record, new[] { CoreEventId.QueryCompilationStarting, RelationalEventId.CommandExecuted }, LogLevel.Debug)
            .Options;

        await using var db = NewContext();
        await db.Database.EnsureCreatedAsync();

        var customers = new[]
        {
            new Customer { Name = "Northwind", Country = "FR" },
            new Customer { Name = "Contoso", Country = "DE" },
            new Customer { Name = "Fabrikam", Country = "ES" },
        };
        db.Customers.AddRange(customers);

        var start = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
        for (var i = 1; i <= 30; i++)
            db.Orders.Add(new Order
            {
                Number = i == 7 ? "PROMO_50%" : $"ORD-{i:000}",
                Customer = customers[i % 3],
                Status = (OrderStatus)(i % 4),
                Total = 10m * i + 0.5m,
                Discount = i % 5 == 0 ? null : i % 7,
                Paid = i % 2 == 0,
                Quantity = 1_000_000_000L * (i % 6),
                ExternalId = new Guid($"00000000-0000-0000-0000-{i:000000000000}"),
                PlacedOn = start.AddHours(13 * i),
                Lines = { new OrderLine { Sku = $"SKU-{i % 4}", Quantity = i } },
            });
        await db.SaveChangesAsync();

        All = await db.Orders.AsNoTracking().Include(o => o.Customer).Include(o => o.Lines).ToListAsync();
    }

    [AssemblyCleanup]
    public static async Task Stop()
    {
        if (container != null) await container.DisposeAsync();
    }

    public static OrdersContext NewContext() => new(options);

    static void Record(string message)
    {
        if (message.Contains(nameof(CoreEventId.QueryCompilationStarting)))
            Interlocked.Increment(ref compilations);
        else
            lock (commands) commands.Add(message);
    }
}
