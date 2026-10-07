using Microsoft.Extensions.DependencyInjection;
using Svm.Services.Contracts.Framework;

namespace Svm.Services.CrossCutting.Consumption;

public static class ConsumptionRegistration
{
    public static IServiceCollection AddSvmConsumption(this IServiceCollection services, IReadOnlyList<IntegrationConsumerBinding> bindings)
    {
        if (services.Any(s => s.ServiceType == typeof(ConsumptionCatalog))) throw new IntegrationConsumptionException(ConsumptionFailure.ConfigurationInvalid);
        var catalog = new ConsumptionCatalog(bindings);
        services.AddSingleton(catalog);
        if (catalog.Bindings.Count == 0) return services;
        services.AddScoped<IIntegrationEventPreflight, IntegrationConsumptionPreflight>();
        services.AddSingleton<IIntegrationConsumptionRecovery, Svm.Services.CrossCutting.Pipeline.ScopedRequestExecutor>();
        foreach (var b in catalog.Bindings)
        {
            services.AddScoped(b.HandlerType);
            services.AddScoped(typeof(IIntegrationEventDispatcher<>).MakeGenericType(b.EventType),
                typeof(IntegrationConsumptionDispatcher<,>).MakeGenericType(b.EventType, b.HandlerType));
        }
        return services;
    }
    public static IServiceCollection ValidateSvmConsumption(this IServiceCollection services)
    {
        var entries = services.Where(s => s.ServiceType == typeof(ConsumptionCatalog)).ToArray();
        if (entries.Length == 0)
        {
            if (services.Any(d => IsConsumptionPort(d.ServiceType))) throw new IntegrationConsumptionException(ConsumptionFailure.ConfigurationInvalid);
            return services;
        }
        if (entries.Length != 1 || entries[0].IsKeyedService || entries[0].ImplementationInstance is not ConsumptionCatalog catalog || entries[0].Lifetime != ServiceLifetime.Singleton)
            throw new IntegrationConsumptionException(ConsumptionFailure.ConfigurationInvalid);
        var expected = new Dictionary<Type, Type>();
        if (catalog.Bindings.Count == 0 && services.Any(d => IsConsumptionPort(d.ServiceType)))
            throw new IntegrationConsumptionException(ConsumptionFailure.ConfigurationInvalid);
        if (catalog.Bindings.Count > 0)
        {
            expected.Add(typeof(IIntegrationEventPreflight), typeof(IntegrationConsumptionPreflight));
            var recovery = services.Where(s => s.ServiceType == typeof(IIntegrationConsumptionRecovery)).ToArray();
            if (recovery.Length != 1 || recovery[0].IsKeyedService || recovery[0].Lifetime != ServiceLifetime.Singleton ||
                recovery[0].ImplementationType != typeof(Svm.Services.CrossCutting.Pipeline.ScopedRequestExecutor))
                throw new IntegrationConsumptionException(ConsumptionFailure.ConfigurationInvalid);
            foreach (var port in new[] { typeof(IIntegrationWorkAuthorizer), typeof(IIntegrationConsumptionTransaction), typeof(IUnitOfWork), typeof(IOperationResultStore) })
            {
                var descriptors = services.Where(s => s.ServiceType == port).ToArray();
                if (descriptors.Length != 1 || descriptors[0].IsKeyedService || descriptors[0].Lifetime != ServiceLifetime.Scoped)
                    throw new IntegrationConsumptionException(ConsumptionFailure.ConfigurationInvalid);
            }
        }
        foreach (var b in catalog.Bindings)
        {
            expected.Add(b.HandlerType, b.HandlerType);
            expected.Add(typeof(IIntegrationEventDispatcher<>).MakeGenericType(b.EventType), typeof(IntegrationConsumptionDispatcher<,>).MakeGenericType(b.EventType, b.HandlerType));
        }
        foreach (var (port, implementation) in expected)
        {
            var ds = services.Where(s => s.ServiceType == port).ToArray();
            if (ds.Length != 1 || ds[0].IsKeyedService || ds[0].Lifetime != ServiceLifetime.Scoped || ds[0].ImplementationType != implementation)
                throw new IntegrationConsumptionException(ConsumptionFailure.ConfigurationInvalid);
        }
        foreach (var d in services)
            if (d.ServiceType.IsGenericType && (d.ServiceType.GetGenericTypeDefinition() == typeof(IIntegrationEventHandler<>) ||
                d.ServiceType.GetGenericTypeDefinition() == typeof(IIntegrationEventDispatcher<>)) && !expected.ContainsKey(d.ServiceType) ||
                IsHandler(d.ServiceType) && !expected.ContainsKey(d.ServiceType) ||
                (d.IsKeyedService ? d.KeyedImplementationType : d.ImplementationType) is { } type && IsHandler(type) && !expected.ContainsKey(d.ServiceType))
                throw new IntegrationConsumptionException(ConsumptionFailure.ConfigurationInvalid);
        return services;
    }
    private static bool IsHandler(Type type) => type.GetInterfaces().Any(t => t.IsGenericType && t.GetGenericTypeDefinition() == typeof(IIntegrationEventHandler<>));
    private static bool IsConsumptionPort(Type type) => type == typeof(IIntegrationEventPreflight) || type == typeof(IIntegrationConsumptionRecovery) ||
        type.IsGenericType && (type.GetGenericTypeDefinition() == typeof(IIntegrationEventHandler<>) || type.GetGenericTypeDefinition() == typeof(IIntegrationEventDispatcher<>));
}

internal sealed class ConsumptionCatalog
{
    internal ConsumptionCatalog(IReadOnlyList<IntegrationConsumerBinding> bindings)
    {
        ArgumentNullException.ThrowIfNull(bindings);
        var seen = new HashSet<Type>();
        var handlers = new HashSet<Type>();
        foreach (var b in bindings)
        {
            if (b is null) throw new IntegrationConsumptionException(ConsumptionFailure.ConfigurationInvalid);
            _ = IntegrationEventDescriptor.For(b.EventType);
            if (!seen.Add(b.EventType) || !handlers.Add(b.HandlerType) || !b.HandlerType.IsClass || !b.HandlerType.IsSealed || b.HandlerType.ContainsGenericParameters ||
                b.HandlerType.Assembly.GetName().Name != "Svm.Application" ||
                b.HandlerType.GetInterfaces().Count(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IIntegrationEventHandler<>)) != 1 ||
                !b.HandlerType.GetInterfaces().Contains(typeof(IIntegrationEventHandler<>).MakeGenericType(b.EventType)))
                throw new IntegrationConsumptionException(ConsumptionFailure.ConfigurationInvalid);
        }
        Bindings = Array.AsReadOnly(bindings.ToArray());
    }
    internal IReadOnlyList<IntegrationConsumerBinding> Bindings { get; }
}
