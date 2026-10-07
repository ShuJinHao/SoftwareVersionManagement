using Svm.Services.Contracts.Framework;
using Svm.SharedKernel.Domain;

namespace Svm.Services.CrossCutting.DomainEvents;

public sealed record DomainEventHandlerBinding(Type HandlerType, int Order);

/// <summary>One explicit subscriber list per event. Its order is independent of DI enumeration order.</summary>
public sealed class DomainEventBinding
{
    public DomainEventBinding(ModuleOwner owner, Type eventType, params DomainEventHandlerBinding[] handlers)
    {
        ArgumentNullException.ThrowIfNull(eventType);
        ArgumentNullException.ThrowIfNull(handlers);
        Owner = owner;
        EventType = eventType;
        Handlers = Array.AsReadOnly((DomainEventHandlerBinding[])handlers.Clone());
    }

    public ModuleOwner Owner { get; }
    public Type EventType { get; }
    public IReadOnlyList<DomainEventHandlerBinding> Handlers { get; }

    public static DomainEventBinding Single<TEvent, THandler>(ModuleOwner owner, int order = 0)
        where TEvent : class, IDomainEvent where THandler : class, IDomainEventHandler<TEvent> =>
        new(owner, typeof(TEvent), new DomainEventHandlerBinding(typeof(THandler), order));
}
