using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static SW.EfCoreExtensions.PgSql.IntegrationTests.OrdersDatabase;

namespace SW.EfCoreExtensions.PgSql.IntegrationTests;

[TestClass]
public class QueryShapingTests
{
    static LambdaExpression Column<T, TValue>(Expression<Func<T, TValue>> column) => column;

    [TestMethod]
    public async Task A_row_reads_only_the_chosen_columns()
    {
        ClearCommands();
        await using var db = NewContext();
        var rows = await db.Orders.OrderBy(o => o.Id).Take(3)
            .Select(RowProjection.Of<Order>(Column((Order o) => o.Number), Column((Order o) => o.Customer.Name)))
            .ToListAsync();

        Assert.AreEqual(All[0].Number, rows[0][0]);
        Assert.AreEqual(All[0].Customer.Name, rows[0][1]);

        var sql = Commands.Last();
        var select = sql[sql.IndexOf("SELECT", StringComparison.Ordinal)..sql.IndexOf("FROM", StringComparison.Ordinal)];
        StringAssert.Contains(select, "\"Number\"");
        StringAssert.Contains(select, "\"Name\"");
        foreach (var unselected in new[] { "\"Total\"", "\"Discount\"", "\"ExternalId\"", "\"PlacedOn\"", "\"Country\"" })
            Assert.IsFalse(select.Contains(unselected), $"{unselected} was read:\n{sql}");
    }

    [TestMethod]
    public async Task A_collection_column_reads_its_own_chosen_columns_in_order_and_capped()
    {
        await using var db = NewContext();
        var row = RowProjection.Of<Order>(
            Column((Order o) => o.Number),
            RowProjection.Many<Order, OrderLine>(o => o.Lines, take: 1,
                orderBy: Column((OrderLine l) => l.Quantity), descending: true,
                Column((OrderLine l) => l.Sku), Column((OrderLine l) => l.Quantity)));

        ClearCommands();
        var rows = await db.Orders.Where(o => o.Id <= 3).OrderBy(o => o.Id).Select(row).ToListAsync();

        var first = (List<object[]>)rows[0][1];
        Assert.AreEqual(1, first.Count);
        Assert.AreEqual(All[0].Lines.Single().Sku, first[0][0]);
        Assert.IsFalse(Commands.Last().Contains("\"Total\""), Commands.Last());
    }

    [TestMethod]
    public async Task The_same_columns_compile_once()
    {
        await using (var warm = NewContext())
            await warm.Orders.Where(o => o.Paid).Select(RowProjection.Of<Order>(Column((Order o) => o.Number))).ToListAsync();
        var before = Compilations;

        for (var i = 0; i < 5; i++)
        {
            await using var db = NewContext();
            // A new expression tree each time, as a request builds it.
            await db.Orders.Where(o => o.Paid).Select(RowProjection.Of<Order>(Column((Order o) => o.Number))).ToListAsync();
        }

        Assert.AreEqual(before, Compilations);
    }

    public static IEnumerable<object[]> Keysets => new[]
    {
        // Unique key alone.
        new object[] { new[] { KeysetKey.Asc((Order o) => o.Id) }, 4 },
        // Many orders share a status: the id breaks the ties.
        new object[] { new[] { KeysetKey.Asc((Order o) => o.Status), KeysetKey.Asc((Order o) => o.Id) }, 4 },
        new object[] { new[] { KeysetKey.Desc((Order o) => o.Total), KeysetKey.Desc((Order o) => o.Id) }, 7 },
        new object[] { new[] { KeysetKey.Asc((Order o) => o.Customer.Name), KeysetKey.Desc((Order o) => o.PlacedOn), KeysetKey.Asc((Order o) => o.Id) }, 5 },
    };

    [TestMethod]
    [DynamicData(nameof(Keysets))]
    public async Task Keyset_pages_visit_every_row_once_in_order(KeysetKey[] keys, int pageSize)
    {
        await using var db = NewContext();
        var expected = await db.Orders.OrderByKeyset(keys).Select(o => o.Id).ToListAsync();

        var visited = new List<int>();
        object[] after = null;
        var compiled = 0;
        for (var page = 0; page < 100; page++)
        {
            var before = Compilations;
            var (items, hasMore) = await db.Orders.Include(o => o.Customer).AfterKeyset(keys, after).OrderByKeyset(keys)
                .AsNoTracking().ToPageAsync(pageSize);
            if (page > 1) compiled += Compilations - before;

            visited.AddRange(items.Select(o => o.Id));
            if (!hasMore) break;
            var last = items.Last();
            after = keys.Select(k => k.Key.Compile().DynamicInvoke(last)).ToArray();
        }

        CollectionAssert.AreEqual(expected, visited);
        Assert.AreEqual(0, compiled, "a new position must not compile a new query");
    }

    [TestMethod]
    public void A_nullable_key_is_refused()
    {
        Assert.ThrowsException<ArgumentException>(() =>
            NewContext().Orders.OrderByKeyset(new[] { KeysetKey.Asc((Order o) => o.Discount) }));
    }

    [TestMethod]
    public async Task A_page_says_whether_more_rows_follow()
    {
        await using var db = NewContext();
        var (items, hasMore) = await db.Orders.OrderBy(o => o.Id).ToPageAsync(All.Count - 1);
        Assert.AreEqual(All.Count - 1, items.Count);
        Assert.IsTrue(hasMore);

        (items, hasMore) = await db.Orders.OrderBy(o => o.Id).ToPageAsync(All.Count);
        Assert.AreEqual(All.Count, items.Count);
        Assert.IsFalse(hasMore);
    }

    [TestMethod]
    public async Task A_count_stops_at_its_limit()
    {
        await using var db = NewContext();
        Assert.AreEqual(new CountUpTo(10, true), await db.Orders.CountUpToAsync(10));
        Assert.AreEqual(new CountUpTo(All.Count, false), await db.Orders.CountUpToAsync(All.Count));
        Assert.AreEqual(new CountUpTo(0, false), await db.Orders.Where(o => o.Id < 0).CountUpToAsync(10));
    }
}
