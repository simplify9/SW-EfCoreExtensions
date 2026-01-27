using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Newtonsoft.Json;
using SW.PrimitiveTypes;
using System;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;

namespace SW.EfCoreExtensions
{
    /// <summary>
    /// Provides extension methods for Entity Framework Core's ChangeTracker to apply common patterns
    /// such as soft deletion, audit timestamps, multi-tenancy, and domain event dispatching.
    /// </summary>
    public static class ChangeTrackerExtensions
    {
        /// <summary>
        /// Applies soft deletion logic to entities marked for deletion that implement <see cref="ISoftDelete"/>.
        /// Instead of physically deleting the entity, sets the Deleted flag to true and optionally records deletion metadata.
        /// </summary>
        /// <param name="changeTracker">The Entity Framework change tracker to process.</param>
        /// <param name="userId">The identifier of the user performing the deletion, used for audit purposes.</param>
        /// <remarks>
        /// This method:
        /// <list type="bullet">
        /// <item><description>Changes entity state from Deleted to Modified</description></item>
        /// <item><description>Sets the Deleted flag to true for entities implementing <see cref="ISoftDelete"/></description></item>
        /// <item><description>Records deletion timestamp for entities implementing <see cref="IHasDeletionTime"/></description></item>
        /// <item><description>Records deleting user ID for entities implementing <see cref="IDeletionAudited"/></description></item>
        /// </list>
        /// Call this method before SaveChanges() to apply soft deletion semantics.
        /// </remarks>
        /// <example>
        /// <code>
        /// dbContext.ChangeTracker.ApplySoftDeletion(userId: "user123");
        /// await dbContext.SaveChangesAsync();
        /// </code>
        /// </example>
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

        /// <summary>
        /// Automatically applies audit timestamps and user information to entities implementing audit interfaces.
        /// Updates creation and modification metadata based on entity state.
        /// </summary>
        /// <param name="changeTracker">The Entity Framework change tracker to process.</param>
        /// <param name="userId">The identifier of the user performing the operation, used for audit purposes.</param>
        /// <remarks>
        /// This method automatically sets:
        /// <list type="bullet">
        /// <item><description><see cref="IHasCreationTime.CreatedOn"/> - Set when entity is Added</description></item>
        /// <item><description><see cref="ICreationAudited.CreatedBy"/> - Set when entity is Added</description></item>
        /// <item><description><see cref="IHasModificationTime.ModifiedOn"/> - Set when entity is Added or Modified</description></item>
        /// <item><description><see cref="IModificationAudited.ModifiedBy"/> - Set when entity is Added or Modified</description></item>
        /// </list>
        /// All timestamps use a single UTC timestamp for consistency across all entities in the batch.
        /// Call this method before SaveChanges() to apply audit values.
        /// </remarks>
        /// <example>
        /// <code>
        /// dbContext.ChangeTracker.ApplyAuditValues(userId: "user123");
        /// await dbContext.SaveChangesAsync();
        /// </code>
        /// </example>
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

                if (entry.Entity is IHasModificationTime && (entry.State == EntityState.Added || entry.State == EntityState.Modified))
                    TrySetProperty(entry.Entity, nameof(IHasModificationTime.ModifiedOn), timestamp);

                if (entry.Entity is IModificationAudited && (entry.State == EntityState.Added || entry.State == EntityState.Modified))
                    TrySetProperty(entry.Entity, nameof(IModificationAudited.ModifiedBy), userId);
            }
        }

        /// <summary>
        /// Automatically applies tenant identification to newly added entities in multi-tenant applications.
        /// Sets the tenant ID for entities implementing tenant interfaces.
        /// </summary>
        /// <param name="changeTracker">The Entity Framework change tracker to process.</param>
        /// <param name="tenantId">The tenant identifier to apply. If null, no tenant values are set.</param>
        /// <remarks>
        /// This method supports:
        /// <list type="bullet">
        /// <item><description><see cref="IHasTenant"/> - For entities that require a tenant ID</description></item>
        /// <item><description><see cref="IHasOptionalTenant"/> - For entities with optional tenant isolation</description></item>
        /// </list>
        /// Only affects entities in the Added state. If tenantId is null, no changes are made.
        /// Call this method before SaveChanges() to apply tenant isolation.
        /// </remarks>
        /// <example>
        /// <code>
        /// dbContext.ChangeTracker.ApplyTenantValues(tenantId: 123);
        /// await dbContext.SaveChangesAsync();
        /// </code>
        /// </example>
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

        /// <summary>
        /// Attempts to set a property value on an entity using reflection, handling both public and private setters.
        /// Silently fails if the property doesn't exist or cannot be set.
        /// </summary>
        /// <param name="entity">The entity instance to modify.</param>
        /// <param name="propertyName">The name of the property to set.</param>
        /// <param name="value">The value to assign to the property.</param>
        private static void TrySetProperty(object entity, string propertyName, object value)
        {
            try
            {
                var prop = entity.GetType().GetProperty(propertyName);
                if (prop == null) return;
                
                var setMethod = prop.GetSetMethod() ?? prop.GetSetMethod(nonPublic: true);
                setMethod?.Invoke(entity, new[] { value });
            }
            catch
            {
                // Silently ignore reflection errors to maintain backward compatibility
                // and avoid breaking saves when property doesn't exist or is readonly
            }
        }

        /// <summary>
        /// Dispatches domain events from entities to a domain event dispatcher.
        /// Clears events from entities after dispatching to prevent duplicate processing.
        /// </summary>
        /// <param name="changeTracker">The Entity Framework change tracker to process.</param>
        /// <param name="domainEventDispatcher">The domain event dispatcher to use for publishing events.</param>
        /// <returns>A task representing the asynchronous operation.</returns>
        /// <remarks>
        /// This method:
        /// <list type="bullet">
        /// <item><description>Finds all entities implementing <see cref="IGeneratesDomainEvents"/> with pending events</description></item>
        /// <item><description>Dispatches each event through the provided dispatcher</description></item>
        /// <item><description>Clears the events collection to prevent duplicate processing</description></item>
        /// </list>
        /// Events are dispatched sequentially in the order they were added.
        /// Call this method after SaveChanges() to ensure entity IDs are available for new entities.
        /// </remarks>
        /// <example>
        /// <code>
        /// await dbContext.SaveChangesAsync();
        /// await dbContext.ChangeTracker.DispatchDomainEvents(domainEventDispatcher);
        /// </code>
        /// </example>
        public static async Task DispatchDomainEvents(this ChangeTracker changeTracker, IDomainEventDispatcher domainEventDispatcher)
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

        /// <summary>
        /// Publishes domain events from entities to a message bus or event publishing system.
        /// Serializes events to JSON and clears them from entities after publishing.
        /// </summary>
        /// <param name="changeTracker">The Entity Framework change tracker to process.</param>
        /// <param name="publish">The publish interface to use for publishing serialized events.</param>
        /// <returns>A task representing the asynchronous operation.</returns>
        /// <remarks>
        /// This method:
        /// <list type="bullet">
        /// <item><description>Finds all entities implementing <see cref="IGeneratesDomainEvents"/> with pending events</description></item>
        /// <item><description>Serializes each event to JSON using Newtonsoft.Json</description></item>
        /// <item><description>Publishes events using the event type name as the topic/routing key</description></item>
        /// <item><description>Clears the events collection to prevent duplicate processing</description></item>
        /// </list>
        /// Events are published sequentially in the order they were added.
        /// Call this method after SaveChanges() to ensure entity IDs are available for new entities.
        /// </remarks>
        /// <example>
        /// <code>
        /// await dbContext.SaveChangesAsync();
        /// await dbContext.ChangeTracker.PublishDomainEvents(messagePublisher);
        /// </code>
        /// </example>
        public static async Task PublishDomainEvents(this ChangeTracker changeTracker, IPublish publish)
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
