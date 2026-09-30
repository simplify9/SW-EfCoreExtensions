using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SW.PrimitiveTypes;

namespace SW.EfCoreExtensions.UnitTests
{
    [TestClass]
    public class DataQueryingTests
    {

        
        static TestServer server;

        [ClassInitialize]
        public static void ClassInitialize(TestContext ctx)
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
        
        [TestMethod]
        public async Task GetWithSearchy()
        {
            var scope = server.Services.CreateScope();
            var facade = scope.ServiceProvider.GetService<TestDbContext>().Database;
            facade.Add("Bags", new Bag
            {
                Entity = "1"
            });
            facade.Add("Bags", new Bag
            {
                Entity = "2"
            });
            facade.Add("Bags", new Bag
            {
                Entity = "3"
            });
            
            var all = await facade.All<Bag>("Bags", new List<SearchyCondition>());
            Assert.AreEqual(all.Count(), 3);
            
            var either1Or2 = await facade.All<Bag>("Bags",new List<SearchyCondition>()
            {
                new SearchyCondition(nameof(Bag.Entity), SearchyRule.EqualsTo, "1"),
                new SearchyCondition(nameof(Bag.Entity), SearchyRule.EqualsTo, "2")
            });
            
            Assert.AreEqual(either1Or2.Count(), 2);

            await facade.Delete<Bag>("Bags", new SearchyCondition[]
            {
                new SearchyCondition(nameof(Bag.Entity), SearchyRule.NotEqualsTo, "4"),
            });

        }
    }
}