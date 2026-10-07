using Microsoft.Extensions.DependencyInjection;
using Svm.Services.Contracts.Framework;

namespace Svm.Services.CrossCutting.DomainEvents;

public static class DomainEventServiceCollectionExtensions
{
    public static IServiceCollection AddSvmDomainEvents(this IServiceCollection services, IReadOnlyList<DomainEventBinding> bindings,
        DomainEventOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(bindings);
        if (services.Any(d => d.ServiceType == typeof(DomainEventCatalog)))
            throw new InvalidOperationException("Domain events are already registered.");
        var catalog = new DomainEventCatalog(bindings);
        services.AddSingleton(catalog);
        services.AddSingleton(options ?? new DomainEventOptions());
        services.AddScoped<IDomainEventDispatcher, DomainEventDispatcher>();
        foreach (var handler in catalog.Bindings.SelectMany(b => b.Handlers).Select(h => h.HandlerType).Distinct())
            services.AddScoped(handler);
        foreach (var binding in catalog.Bindings)
        foreach (var handler in binding.Handlers)
            services.AddScoped(typeof(IDomainEventSubscription), SubscriptionType(binding.EventType, handler.HandlerType));
        return services;
    }

    public static IServiceCollection ValidateSvmDomainEvents(this IServiceCollection services)
    {
        var catalogs = services.Where(d => d.ServiceType == typeof(DomainEventCatalog)).ToArray();
        if (catalogs.Length != 1 || catalogs[0].IsKeyedService || catalogs[0].Lifetime != ServiceLifetime.Singleton ||
            catalogs[0].ImplementationInstance is not DomainEventCatalog catalog)
            throw new InvalidOperationException("Exactly one immutable domain event catalog is required.");
        var options = services.Where(d => d.ServiceType == typeof(DomainEventOptions)).ToArray();
        if (options.Length != 1 || options[0].IsKeyedService || options[0].Lifetime != ServiceLifetime.Singleton ||
            options[0].ImplementationInstance is not DomainEventOptions)
            throw new InvalidOperationException("Exactly one immutable domain event option set is required.");
        RequireScoped(services, typeof(IDomainEventDispatcher), typeof(DomainEventDispatcher));
        var handlers = catalog.Bindings.SelectMany(b => b.Handlers).Select(h => h.HandlerType).ToHashSet();
        foreach (var handler in handlers) RequireScoped(services, handler, handler);
        var expected = catalog.Bindings.SelectMany(b => b.Handlers.Select(h => SubscriptionType(b.EventType, h.HandlerType))).ToHashSet();
        foreach (var descriptor in services)
        {
            if (descriptor.ServiceType.IsGenericType && descriptor.ServiceType.GetGenericTypeDefinition() == typeof(IDomainEventHandler<>))
                throw new InvalidOperationException("Domain event handlers must use the explicit subscriber catalog.");
            if (IsHandler(descriptor.ServiceType) && !handlers.Contains(descriptor.ServiceType) ||
                !descriptor.IsKeyedService && descriptor.ImplementationType is { } implementation && IsHandler(implementation) &&
                (!handlers.Contains(implementation) || descriptor.ServiceType != implementation))
                throw new InvalidOperationException("A domain event handler bypasses the explicit subscriber catalog.");
            if (descriptor.ServiceType != typeof(IDomainEventSubscription)) continue;
            if (descriptor.IsKeyedService || descriptor.Lifetime != ServiceLifetime.Scoped || descriptor.ImplementationType is null ||
                !expected.Remove(descriptor.ImplementationType))
                throw new InvalidOperationException("Invalid, duplicate or unapproved domain event subscription.");
        }
        if (expected.Count != 0) throw new InvalidOperationException("A required domain event subscription was removed.");
        return services;
    }

    private static Type SubscriptionType(Type domainEvent, Type handler) => typeof(DomainEventSubscription<,>).MakeGenericType(domainEvent, handler);

    private static bool IsHandler(Type type) => type.GetInterfaces().Any(i => i.IsGenericType &&
        i.GetGenericTypeDefinition() == typeof(IDomainEventHandler<>));

    private static void RequireScoped(IServiceCollection services, Type contract, Type implementation)
    {
        var matches = services.Where(d => d.ServiceType == contract).ToArray();
        if (matches.Length != 1 || matches[0].IsKeyedService || matches[0].Lifetime != ServiceLifetime.Scoped || matches[0].ImplementationType != implementation)
            throw new InvalidOperationException("Domain event dispatch and handlers require one explicit scoped implementation.");
    }
}
