using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SW.EfCoreExtensions.UnitTests.Domain;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace SW.EfCoreExtensions.UnitTests
{
    [TestClass]
    public class AuditTests
    {
        static TestServer server;

        [ClassInitialize]
        public static void ClassInitialize(TestContext tcontext)
        {
            server = new TestServer(WebHost.CreateDefaultBuilder()
                .UseDefaultServiceProvider((context, options) => { options.ValidateScopes = true; })
                .UseEnvironment("Development")
                .UseStartup<TestStartup>());
        }

        [ClassCleanup]
        public static void ClassCleanup()
        {
            server.Dispose();
        }

        static Employee NewEmployee() => new Employee
        {
            UserName = "adam",
            Email = "adam@example.com",
            FirstName = "Adam",
            LastName = "Smith"
        };

        [TestMethod]
        public void CapturesEveryPropertyWhenNoOptionsAreGiven()
        {
            using var scope = server.Services.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<TestDbContext>();
            dbContext.Add(NewEmployee());

            var changes = SingleEmployeeEntry(dbContext.ChangeTracker.CapturePendingAuditDiffs()).Changes;

            Assert.IsTrue(changes.ContainsKey(nameof(Employee.UserName)));
            Assert.IsTrue(changes.ContainsKey(nameof(Employee.Email)));
        }

        /// <summary>
        /// The case worth guarding: an Added entity is captured as a full snapshot rather than a
        /// diff, so an exclusion that only applied to modifications would still write the secret out
        /// in full the very first time the row was inserted.
        /// </summary>
        [TestMethod]
        public void ExcludedPropertyIsAbsentFromAnAddedSnapshot()
        {
            using var scope = server.Services.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<TestDbContext>();
            dbContext.Add(NewEmployee());

            var changes = SingleEmployeeEntry(
                dbContext.ChangeTracker.CapturePendingAuditDiffs(options: ExcludeEmail)).Changes;

            Assert.IsFalse(changes.ContainsKey(nameof(Employee.Email)));
            Assert.IsTrue(changes.ContainsKey(nameof(Employee.UserName)));
        }

        [TestMethod]
        async public Task ExcludedPropertyIsAbsentFromAModifiedDiff()
        {
            using var scope = server.Services.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<TestDbContext>();

            var employee = NewEmployee();
            dbContext.Add(employee);
            await dbContext.SaveChangesAsync();

            employee.Email = "adam@changed.com";
            employee.LastName = "Jones";

            var changes = SingleEmployeeEntry(
                dbContext.ChangeTracker.CapturePendingAuditDiffs(options: ExcludeEmail)).Changes;

            Assert.IsFalse(changes.ContainsKey(nameof(Employee.Email)));
            Assert.AreEqual("Jones", changes[nameof(Employee.LastName)].New);
            Assert.AreEqual("Smith", changes[nameof(Employee.LastName)].Old);
        }

        [TestMethod]
        public void EntityFilterSkipsUnauditedEntitiesEntirely()
        {
            using var scope = server.Services.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<TestDbContext>();
            dbContext.Add(NewEmployee());
            dbContext.Add(new SomeData { StringArray = new[] { "x" } });

            var captured = dbContext.ChangeTracker.CapturePendingAuditDiffs(options: new AuditOptions
            {
                ShouldAuditEntity = entry => entry.Entity is SomeData
            });

            Assert.IsFalse(captured.Any(c => c.EntityType == typeof(Employee).FullName));
            Assert.IsTrue(captured.Any(c => c.EntityType == typeof(SomeData).FullName));
        }

        [TestMethod]
        async public Task FinalizeResolvesTheGeneratedPrimaryKey()
        {
            using var scope = server.Services.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<TestDbContext>();

            var employee = NewEmployee();
            dbContext.Add(employee);

            var pending = dbContext.ChangeTracker.CapturePendingAuditDiffs(userId: "user-42");
            await dbContext.SaveChangesAsync();
            var finalized = pending.FinalizeAuditDiffJson()
                .Single(f => f.EntityType == typeof(Employee).FullName);

            var primaryKey = (IDictionary<string, object>)finalized.PrimaryKey;

            Assert.AreEqual(employee.Id, primaryKey[nameof(Employee.Id)]);
            Assert.AreNotEqual(0, employee.Id);
            Assert.AreEqual("user-42", finalized.UserId);
            Assert.AreEqual("Added", finalized.State);
        }

        static readonly AuditOptions ExcludeEmail = new()
        {
            ShouldAuditProperty = (entry, property) => property.Name != nameof(Employee.Email)
        };

        static PendingAuditEntry SingleEmployeeEntry(IReadOnlyCollection<PendingAuditEntry> captured) =>
            captured.Single(c => c.EntityType == typeof(Employee).FullName);
    }
}
