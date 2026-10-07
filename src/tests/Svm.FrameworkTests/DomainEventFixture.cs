using System.Reflection;
using System.Reflection.Emit;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Svm.EntityFrameworkCore.Framework;
using Svm.Services.Contracts.Framework;
using Svm.Services.CrossCutting.DomainEvents;
using Svm.SharedKernel.Domain;

namespace Svm.FrameworkTests;

/// <summary>Test-owned model and assembly fixtures, never production entities, Commands or activation switches.</summary>
internal sealed class DomainEventFixture(PersistenceDatabase database)
{
    internal IdempotencyFixture Idempotency { get; } = new(database);
    internal DomainEventTestState State { get; } = new();

    internal ServiceProvider Provider(DomainEventOptions? options = null, IReadOnlyList<DomainEventBinding>? bindings = null,
        bool foreignAggregate = false, Action<IServiceCollection>? configure = null, params IInterceptor[] interceptors) =>
        Idempotency.ProviderWithDomainEvents(bindings ?? [Binding()], options ?? new(), services =>
        {
            services.AddSingleton(State);
            var builder = new DbContextOptionsBuilder<SvmDbContext>().UseNpgsql(database.WriterConnection).AddInterceptors(interceptors);
            if (foreignAggregate) builder.ReplaceService<IModelCustomizer, ForeignDomainEventTestModelCustomizer>();
            else builder.ReplaceService<IModelCustomizer, DomainEventTestModelCustomizer>();
            var contextOptions = builder.Options;
            services.Replace(ServiceDescriptor.Scoped(_ => new SvmDbContext(contextOptions)));
            configure?.Invoke(services);
        }, interceptors);

    internal static DomainEventBinding Binding(bool multiple = false, Type? eventType = null) =>
        new(ModuleOwner.Identity, eventType ?? DomainEventTestTypes.Event,
            multiple ? [new(DomainEventTestTypes.SecondHandler, 20), new(DomainEventTestTypes.FirstHandler, 10)] : [new(DomainEventTestTypes.FirstHandler, 10)]);

    internal static DomainEventTestAggregate Aggregate(bool foreign = false, Guid? id = null) =>
        (DomainEventTestAggregate)Activator.CreateInstance(foreign ? DomainEventTestTypes.ForeignAggregate : DomainEventTestTypes.Aggregate,
            new StrongId<DomainEventTestTag>(id ?? Guid.NewGuid()))!;

    internal static IDomainEvent Event(int sequence, Guid? id = null, Type? type = null, DateTimeOffset? occurredAt = null) =>
        (IDomainEvent)Activator.CreateInstance(type ?? DomainEventTestTypes.Event, id ?? Guid.NewGuid(), occurredAt ?? DateTimeOffset.UtcNow, sequence)!;
}

public sealed class DomainEventTestTag;

public class DomainEventTestAggregate(StrongId<DomainEventTestTag> id) : AggregateRoot<StrongId<DomainEventTestTag>>(id)
{
    public int Value { get; set; }
    public void Raise(IDomainEvent domainEvent) => RecordDomainEvent(domainEvent);
}

public sealed class DomainEventTestState
{
    public List<string> Calls { get; } = [];
    public List<Guid> HandlerInstances { get; } = [];
    public Func<string, IDomainEvent, CancellationToken, Task>? OnHandle { get; set; }

    public async Task HandleAsync(string handler, Guid instance, IDomainEvent domainEvent, CancellationToken cancellationToken)
    {
        Calls.Add($"{handler}:{Sequence(domainEvent)}");
        HandlerInstances.Add(instance);
        if (OnHandle is not null) await OnHandle(handler, domainEvent, cancellationToken);
    }
    public static int Sequence(IDomainEvent domainEvent) => (int)domainEvent.GetType().GetProperty("Sequence")!.GetValue(domainEvent)!;
}

public class DomainEventTestHandler<TEvent>(DomainEventTestState state) : IDomainEventHandler<TEvent> where TEvent : class, IDomainEvent
{
    private readonly Guid _instance = Guid.NewGuid();
    public Task HandleAsync(TEvent domainEvent, CancellationToken cancellationToken) =>
        state.HandleAsync(GetType().Name, _instance, domainEvent, cancellationToken);
}

public sealed class DomainEventTestModelCustomizer(ModelCustomizerDependencies dependencies) : ModelCustomizer(dependencies)
{
    public override void Customize(ModelBuilder modelBuilder, DbContext context)
    {
        base.Customize(modelBuilder, context);
        Configure(modelBuilder, DomainEventTestTypes.Aggregate);
    }

    internal static void Configure(ModelBuilder modelBuilder, Type aggregate)
    {
        var entity = modelBuilder.Entity(aggregate);
        entity.HasBaseType((Type?)null);
        entity.ToTable("foundation_probe", "iam");
        entity.HasKey(nameof(DomainEventTestAggregate.Id));
        entity.Property(nameof(DomainEventTestAggregate.Id)).HasColumnName("id").HasConversion(
            new ValueConverter<StrongId<DomainEventTestTag>, Guid>(id => id.Value, value => new(value)));
        entity.Property(nameof(DomainEventTestAggregate.Value)).HasColumnName("value");
        entity.Ignore(nameof(DomainEventTestAggregate.DomainEvents));
    }
}

public sealed class ForeignDomainEventTestModelCustomizer(ModelCustomizerDependencies dependencies) : ModelCustomizer(dependencies)
{
    public override void Customize(ModelBuilder modelBuilder, DbContext context)
    {
        base.Customize(modelBuilder, context);
        DomainEventTestModelCustomizer.Configure(modelBuilder, DomainEventTestTypes.ForeignAggregate);
    }
}

/// <summary>Named assemblies exercise the production owner checks without permitting test assemblies in the catalog.</summary>
internal static class DomainEventTestTypes
{
    private static readonly ModuleBuilder Core = AssemblyBuilder.DefineDynamicAssembly(new("Svm.Core.Identity"), AssemblyBuilderAccess.Run).DefineDynamicModule("Fixtures");
    private static readonly ModuleBuilder Services = AssemblyBuilder.DefineDynamicAssembly(new("Svm.IdentityService"), AssemblyBuilderAccess.Run).DefineDynamicModule("Fixtures");
    internal static readonly Type Event = DefineEvent("Recorded");
    internal static readonly Type OtherEvent = DefineEvent("FollowedUp");
    internal static readonly Type MutableEvent = DefineEvent("Mutable", immutable: false);
    internal static readonly Type Aggregate = DefineAggregate(Core, "TrackedAggregate");
    internal static readonly Type ForeignAggregate = DefineAggregate(AssemblyBuilder.DefineDynamicAssembly(new("Svm.Core.Releases"), AssemblyBuilderAccess.Run)
        .DefineDynamicModule("Fixtures"), "ForeignAggregate");
    internal static readonly Type FirstHandler = DefineHandler("First", Event);
    internal static readonly Type SecondHandler = DefineHandler("Second", Event);
    internal static readonly Type FollowUpHandler = DefineHandler("FollowUp", OtherEvent);
    internal static readonly Type MutableHandler = DefineHandler("MutableHandler", MutableEvent);

    private static Type DefineEvent(string name, bool immutable = true)
    {
        var type = Core.DefineType("Fixture." + name, TypeAttributes.Public | TypeAttributes.Sealed);
        type.AddInterfaceImplementation(typeof(IDomainEvent));
        var fields = new[] { DefineProperty(type, "EventId", typeof(Guid), immutable), DefineProperty(type, "OccurredAt", typeof(DateTimeOffset), immutable),
            DefineProperty(type, "Sequence", typeof(int), immutable) };
        var constructor = type.DefineConstructor(MethodAttributes.Public, CallingConventions.Standard, [typeof(Guid), typeof(DateTimeOffset), typeof(int)]);
        var il = constructor.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Call, typeof(object).GetConstructor(Type.EmptyTypes)!);
        for (var i = 0; i < fields.Length; i++)
        { il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldarg, i + 1); il.Emit(OpCodes.Stfld, fields[i]); }
        il.Emit(OpCodes.Ret);
        return type.CreateType()!;
    }

    private static FieldBuilder DefineProperty(TypeBuilder type, string name, Type valueType, bool immutable)
    {
        var field = type.DefineField("_" + name, valueType, FieldAttributes.Private | (immutable ? FieldAttributes.InitOnly : 0));
        var property = type.DefineProperty(name, PropertyAttributes.None, valueType, null);
        var getter = type.DefineMethod("get_" + name, MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.Final |
            MethodAttributes.NewSlot | MethodAttributes.SpecialName | MethodAttributes.HideBySig, valueType, Type.EmptyTypes);
        var il = getter.GetILGenerator(); il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldfld, field); il.Emit(OpCodes.Ret);
        property.SetGetMethod(getter);
        if (!immutable)
        {
            var setter = type.DefineMethod("set_" + name, MethodAttributes.Public | MethodAttributes.SpecialName | MethodAttributes.HideBySig,
                typeof(void), [valueType]);
            var setterIl = setter.GetILGenerator(); setterIl.Emit(OpCodes.Ldarg_0); setterIl.Emit(OpCodes.Ldarg_1);
            setterIl.Emit(OpCodes.Stfld, field); setterIl.Emit(OpCodes.Ret); property.SetSetMethod(setter);
        }
        if (typeof(IDomainEvent).GetProperty(name) is { } contract) type.DefineMethodOverride(getter, contract.GetMethod!);
        return field;
    }

    private static Type DefineAggregate(ModuleBuilder module, string name)
    {
        var type = module.DefineType("Fixture." + name, TypeAttributes.Public | TypeAttributes.Sealed, typeof(DomainEventTestAggregate));
        DefineConstructor(type, typeof(DomainEventTestAggregate), typeof(StrongId<DomainEventTestTag>), "id");
        return type.CreateType()!;
    }

    private static Type DefineHandler(string name, Type domainEvent)
    {
        var parent = typeof(DomainEventTestHandler<>).MakeGenericType(domainEvent);
        var type = Services.DefineType("Fixture." + name, TypeAttributes.Public | TypeAttributes.Sealed, parent);
        DefineConstructor(type, parent, typeof(DomainEventTestState), "state");
        return type.CreateType()!;
    }

    private static void DefineConstructor(TypeBuilder type, Type parent, Type argument, string parameterName)
    {
        var constructor = type.DefineConstructor(MethodAttributes.Public, CallingConventions.Standard, [argument]);
        constructor.DefineParameter(1, ParameterAttributes.None, parameterName);
        var il = constructor.GetILGenerator(); il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Call, parent.GetConstructor([argument])!); il.Emit(OpCodes.Ret);
    }
}
