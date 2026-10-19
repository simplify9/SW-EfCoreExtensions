using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SW.PrimitiveTypes;
using static SW.EfCoreExtensions.PgSql.IntegrationTests.OrdersDatabase;

[assembly: DoNotParallelize]

namespace SW.EfCoreExtensions.PgSql.IntegrationTests;

[TestClass]
public class SearchyTests
{
    static SearchyCondition Where(string field, SearchyRule rule, object value) =>
        new(new SearchyFilter { Field = field, Rule = rule, Value = value });

    static async Task<List<Order>> Search(params SearchyCondition[] conditions)
    {
        await using var db = NewContext();
        return await db.Orders.AsNoTracking().Search(conditions).OrderBy(o => o.Id).ToListAsync();
    }

    static List<Order> Expected(Func<Order, bool> predicate) => All.Where(predicate).OrderBy(o => o.Id).ToList();

    static void AssertSameOrders(IEnumerable<Order> expected, IEnumerable<Order> actual) =>
        CollectionAssert.AreEqual(expected.Select(o => o.Id).ToList(), actual.Select(o => o.Id).ToList());

    [TestMethod]
    public async Task Values_are_sent_as_parameters_and_the_query_compiles_once()
    {
        // Warm up the shape, then search for ten different values.
        await Search(Where(nameof(Order.Number), SearchyRule.EqualsTo, "ORD-001"));
        var before = Compilations;
        ClearCommands();

        foreach (var i in Enumerable.Range(10, 10))
            Assert.AreEqual(1, (await Search(Where(nameof(Order.Number), SearchyRule.EqualsTo, $"ORD-{i:000}"))).Count);

        Assert.AreEqual(before, Compilations, "a new value must not compile a new query");
        foreach (var sql in Commands)
            StringAssert.DoesNotMatch(sql, new System.Text.RegularExpressions.Regex("ORD-01"), "values must not appear in the SQL text");
    }

    [TestMethod]
    public async Task Every_rule_with_a_value_uses_a_parameter()
    {
        ClearCommands();
        await Search(
            Where(nameof(Order.Total), SearchyRule.GreaterThan, 12.25m),
            Where(nameof(Order.Number), SearchyRule.Contains, "zz-literal-zz"),
            Where(nameof(Order.Number), SearchyRule.StartsWith, "yy-literal-yy"),
            Where(nameof(Order.Quantity), SearchyRule.Range, new long[] { 7_777_777, 8_888_888 }),
            Where(nameof(Order.Number), SearchyRule.EqualsToList, new[] { "xx-literal-xx" }));

        var sql = string.Join("\n", Commands);
        foreach (var literal in new[] { "12.25", "zz-literal-zz", "yy-literal-yy", "7777777", "8888888", "xx-literal-xx" })
            Assert.IsFalse(sql.Contains(literal), $"'{literal}' appears in the SQL text:\n{sql}");
    }

    [TestMethod]
    public async Task A_list_is_one_array_parameter()
    {
        ClearCommands();
        var numbers = Enumerable.Range(1, 200).Select(i => $"ORD-{i:000}").ToArray();
        var found = await Search(Where(nameof(Order.Number), SearchyRule.EqualsToList, numbers));

        AssertSameOrders(Expected(o => numbers.Contains(o.Number)), found);
        StringAssert.Contains(Commands.Last(), "= ANY (");
    }

    [TestMethod]
    public async Task Comparison_rules_return_the_right_rows()
    {
        AssertSameOrders(Expected(o => o.Total > 100m), await Search(Where(nameof(Order.Total), SearchyRule.GreaterThan, 100m)));
        AssertSameOrders(Expected(o => o.Total >= 100.5m), await Search(Where(nameof(Order.Total), SearchyRule.GreaterThanOrEquals, "100.5")));
        AssertSameOrders(Expected(o => o.Total < 50m), await Search(Where(nameof(Order.Total), SearchyRule.LessThan, 50)));
        AssertSameOrders(Expected(o => o.Total <= 50.5m), await Search(Where(nameof(Order.Total), SearchyRule.LessThanOrEquals, 50.5)));
        AssertSameOrders(Expected(o => o.Status != OrderStatus.Draft), await Search(Where(nameof(Order.Status), SearchyRule.NotEqualsTo, "Draft")));
        AssertSameOrders(Expected(o => o.Total >= 50m && o.Total < 150m),
            await Search(Where(nameof(Order.Total), SearchyRule.Range, new[] { 50m, 150m })));
    }

    [TestMethod]
    public async Task Text_rules_are_case_insensitive_and_match_wildcards_literally()
    {
        AssertSameOrders(Expected(o => o.Number.StartsWith("ORD-01")), await Search(Where(nameof(Order.Number), SearchyRule.StartsWith, "ord-01")));
        AssertSameOrders(Expected(o => o.Number == "PROMO_50%"), await Search(Where(nameof(Order.Number), SearchyRule.Contains, "_50%")));
        AssertSameOrders(Expected(o => o.Customer.Name.Contains("wind")), await Search(Where("Customer.Name", SearchyRule.Contains, "WIND")));
    }

    [TestMethod]
    public async Task Values_convert_to_any_field_type()
    {
        var external = All[4].ExternalId;
        AssertSameOrders(Expected(o => o.ExternalId == external), await Search(Where(nameof(Order.ExternalId), SearchyRule.EqualsTo, external.ToString())));
        AssertSameOrders(Expected(o => o.Status == OrderStatus.Shipped), await Search(Where(nameof(Order.Status), SearchyRule.EqualsTo, "shipped")));
        AssertSameOrders(Expected(o => o.Status == OrderStatus.Shipped), await Search(Where(nameof(Order.Status), SearchyRule.EqualsTo, 2L)));
        AssertSameOrders(Expected(o => o.Paid), await Search(Where(nameof(Order.Paid), SearchyRule.EqualsTo, "1")));
        AssertSameOrders(Expected(o => o.Discount == null), await Search(Where(nameof(Order.Discount), SearchyRule.EqualsTo, null)));
        AssertSameOrders(Expected(o => o.PlacedOn < new DateTime(2026, 10, 3, 0, 0, 0, DateTimeKind.Utc)),
            await Search(Where(nameof(Order.PlacedOn), SearchyRule.LessThan, "2026-10-03T00:00:00Z")));
    }

    [TestMethod]
    public async Task Numbers_parse_the_same_whatever_the_server_culture()
    {
        var culture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("fr-FR");
            AssertSameOrders(Expected(o => o.Total > 100.5m), await Search(Where(nameof(Order.Total), SearchyRule.GreaterThan, "100.5")));
        }
        finally
        {
            CultureInfo.CurrentCulture = culture;
        }
    }

    public static IEnumerable<object[]> SortFields => new[]
    {
        new object[] { nameof(Order.Paid) },        // bool
        new object[] { nameof(Order.Discount) },    // decimal?
        new object[] { nameof(Order.Status) },      // enum
        new object[] { nameof(Order.ExternalId) },  // Guid
        new object[] { nameof(Order.Quantity) },    // long
        new object[] { nameof(Order.PlacedOn) },    // DateTime
        new object[] { "Customer.Name" },           // a path
        new object[] { "customer.country" },        // a path, any case
    };

    [TestMethod]
    [DynamicData(nameof(SortFields))]
    public async Task Sorts_on_any_type_and_path(string field)
    {
        await using var db = NewContext();
        // Id as the second key makes the expected order total.
        var sorted = await db.Orders.AsNoTracking().Include(o => o.Customer)
            .Search(Array.Empty<SearchyCondition>(), new[] { new SearchySort(field, SearchySortOrder.DEC), new SearchySort(nameof(Order.Id), SearchySortOrder.ASC) })
            .ToListAsync();

        Func<Order, object> key = field.ToLowerInvariant() switch
        {
            "paid" => o => o.Paid,
            "discount" => o => o.Discount,
            "status" => o => o.Status,
            "externalid" => o => o.ExternalId,
            "quantity" => o => o.Quantity,
            "placedon" => o => o.PlacedOn,
            "customer.name" => o => o.Customer.Name,
            _ => o => o.Customer.Country,
        };
        // Postgres puts NULLs first in a descending sort, as LINQ's default comparer does in reverse.
        var expected = All.OrderByDescending(key, Comparer<object>.Create(PostgresOrder)).ThenBy(o => o.Id).Select(o => o.Id).ToList();

        CollectionAssert.AreEqual(expected, sorted.Select(o => o.Id).ToList());
    }

    static int PostgresOrder(object a, object b) => (a, b) switch
    {
        (null, null) => 0,
        (null, _) => 1,
        (_, null) => -1,
        (string x, string y) => string.CompareOrdinal(x, y),
        _ => Comparer<object>.Default.Compare(a, b),
    };

    [TestMethod]
    public async Task An_unknown_field_names_the_field()
    {
        var error = await Assert.ThrowsExceptionAsync<SWException>(() => Search(Where("Nope", SearchyRule.EqualsTo, 1)));
        StringAssert.Contains(error.Message, "Nope");
    }

    [TestMethod]
    public async Task Paging_and_counting_still_work()
    {
        await using var db = NewContext();
        var request = new SearchyRequest { PageSize = 5, PageIndex = 1 };
        request.Sorts.Add(new SearchySort(nameof(Order.Id), SearchySortOrder.ASC));
        request.Conditions.Add(Where(nameof(Order.Paid), SearchyRule.EqualsTo, true));

        var response = await db.Orders.AsNoTracking().ToSearchyResponseAsync(request);

        var paid = All.Where(o => o.Paid).OrderBy(o => o.Id).ToList();
        Assert.AreEqual(paid.Count, response.TotalCount);
        CollectionAssert.AreEqual(paid.Skip(5).Take(5).Select(o => o.Id).ToList(), response.Result.Select(o => o.Id).ToList());
    }
}
