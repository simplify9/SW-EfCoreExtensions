using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Newtonsoft.Json;
using SW.PrimitiveTypes;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;

namespace SW.EfCoreExtensions
{
    public static class ChangeTrackerExtensions
    {
        public static void ApplySoftDeletion(this ChangeTracker changeTracker, string userId)
        {
            changeTracker.DetectChanges();

            var timestamp = DateTime.UtcNow;

            foreach (var entry in changeTracker.Entries())
            {
                if (entry.Entity is ISoftDelete && entry.State == EntityState.Deleted)
                {
                    entry.State = EntityState.Modified;
                    TrySetProperty(entry.Entity, nameof(ISoftDelete.Deleted), true);

                    if (entry.Entity is IHasDeletionTime)
                        TrySetProperty(entry.Entity, nameof(IHasDeletionTime.DeletedOn), timestamp);

                    if (entry.Entity is IDeletionAudited)
                        TrySetProperty(entry.Entity, nameof(IDeletionAudited.DeletedBy), userId);
                }

            }
        }

        public static void ApplyAuditValues(this ChangeTracker changeTracker, string userId)
        {
            changeTracker.DetectChanges();

            var timestamp = DateTime.UtcNow;

            foreach (var entry in changeTracker.Entries())
            {

                if (entry.Entity is IHasCreationTime && entry.State == EntityState.Added)
                    TrySetProperty(entry.Entity, nameof(IHasCreationTime.CreatedOn), timestamp);

                if (entry.Entity is ICreationAudited && entry.State == EntityState.Added)
                    TrySetProperty(entry.Entity, nameof(ICreationAudited.CreatedBy), userId);

                if (entry.Entity is IHasModificationTime && entry.State == EntityState.Modified)
                    TrySetProperty(entry.Entity, nameof(IHasModificationTime.ModifiedOn), timestamp);

                if (entry.Entity is IModificationAudited && entry.State == EntityState.Modified)
                    TrySetProperty(entry.Entity, nameof(IModificationAudited.ModifiedBy), userId);
            }
        }

        public static void ApplyTenantValues(this ChangeTracker changeTracker, int? tenantId)
        {
            changeTracker.DetectChanges();

            foreach (var entry in changeTracker.Entries())
            {
                if (entry.Entity is IHasTenant && entry.State == EntityState.Added && tenantId.HasValue)
                    TrySetProperty(entry.Entity, nameof(IHasTenant.TenantId), tenantId.Value);

                if (entry.Entity is IHasOptionalTenant && entry.State == EntityState.Added && tenantId.HasValue)
                    TrySetProperty(entry.Entity, nameof(IHasOptionalTenant.TenantId), tenantId.Value);
            }
        }

        private static void TrySetProperty(object entity, string propertyName, object value)
        {
            var prop = entity.GetType().GetProperty(propertyName);
            var setMethod = prop.GetSetMethod() ?? prop.GetSetMethod(true);
            if (setMethod != null) setMethod.Invoke(entity, new object[] { value });
        }

        async public static Task DispatchDomainEvents(this ChangeTracker changeTracker, IDomainEventDispatcher domainEventDispatcher)
        {
            var entitiesWithEvents = changeTracker.Entries<IGeneratesDomainEvents>()
                .Select(e => e.Entity)
                .Where(e => e.Events.Any())
                .ToArray();

            foreach (var entity in entitiesWithEvents)
            {
                var events = entity.Events.ToArray();
                entity.Events.Clear();
                foreach (var domainEvent in events)
                {
                    await domainEventDispatcher.Dispatch(domainEvent);
                }
            }
        }

        async public static Task PublishDomainEvents(this ChangeTracker changeTracker, IPublish publish)
        {
            var entitiesWithEvents = changeTracker.Entries<IGeneratesDomainEvents>()
                .Select(e => e.Entity)
                .Where(e => e.Events.Any())
                .ToArray();

            foreach (var entity in entitiesWithEvents)
            {
                var events = entity.Events.ToArray();
                entity.Events.Clear();
                foreach (var domainEvent in events)
                {
                    await publish.Publish(domainEvent.GetType().Name, JsonConvert.SerializeObject(domainEvent));
                }
            }
        }
    }
}
