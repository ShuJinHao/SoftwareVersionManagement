using Svm.Services.Contracts.Framework;
using Svm.SharedKernel.Domain;

namespace Svm.Services.CrossCutting.DomainEvents;

internal sealed class DomainEventCatalog
{
    private readonly IReadOnlyDictionary<Type, DomainEventBinding> _events;

    internal DomainEventCatalog(IEnumerable<DomainEventBinding> bindings)
    {
        var events = new Dictionary<Type, DomainEventBinding>();
        foreach (var binding in bindings)
        {
            if (binding is null || !Enum.IsDefined(binding.Owner) || !IsConcrete(binding.EventType) ||
                !typeof(IDomainEvent).IsAssignableFrom(binding.EventType) ||
                binding.EventType.Assembly.GetName().Name != CoreAssembly(binding.Owner) || binding.Handlers.Count == 0)
                throw new InvalidOperationException("Domain events require a concrete owning-module event and an explicit subscriber list.");
            if (!events.TryAdd(binding.EventType, binding))
                throw new InvalidOperationException("Each domain event requires exactly one explicit subscriber list.");
            var contract = typeof(IDomainEventHandler<>).MakeGenericType(binding.EventType);
            if (binding.Handlers.Any(h => h is null || !IsConcrete(h.HandlerType) ||
                    !contract.IsAssignableFrom(h.HandlerType) || h.HandlerType.Assembly.GetName().Name != ServiceAssembly(binding.Owner) ||
                    h.HandlerType.GetInterfaces().Any(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IDomainEventHandler<>) &&
                        i.GenericTypeArguments[0].Assembly.GetName().Name != CoreAssembly(binding.Owner))) ||
                binding.Handlers.Select(h => h.HandlerType).Distinct().Count() != binding.Handlers.Count ||
                binding.Handlers.Select(h => h.Order).Distinct().Count() != binding.Handlers.Count)
                throw new InvalidOperationException("Domain event handlers must belong to the owning module with unique types and explicit unique orders.");
        }
        Bindings = Array.AsReadOnly(events.Values.ToArray());
        _events = events;
    }

    internal IReadOnlyList<DomainEventBinding> Bindings { get; }

    internal DomainEventBinding Get(IAggregateRoot aggregate, IDomainEvent domainEvent)
    {
        if (!_events.TryGetValue(domainEvent.GetType(), out var binding))
            throw new DomainEventDispatchException(DomainEventFailure.MissingHandler);
        if (aggregate.GetType().Assembly.GetName().Name != CoreAssembly(binding.Owner))
            throw new DomainEventDispatchException(DomainEventFailure.InvalidOwnership);
        return binding;
    }

    private static bool IsConcrete(Type type) => type.IsClass && !type.IsAbstract && !type.ContainsGenericParameters;
    private static string CoreAssembly(ModuleOwner owner) => "Svm.Core." + owner;
    private static string ServiceAssembly(ModuleOwner owner) => owner switch
    {
        ModuleOwner.Identity => "Svm.IdentityService", ModuleOwner.Releases => "Svm.ReleaseService",
        ModuleOwner.Packages => "Svm.PackageService", ModuleOwner.Instances => "Svm.InstanceService",
        ModuleOwner.Tasks => "Svm.TaskService", ModuleOwner.Audit => "Svm.AuditService",
        _ => throw new InvalidOperationException("Unknown domain event module.")
    };
}
