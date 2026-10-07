using FluentValidation;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Svm.Services.Contracts.Framework;
using Svm.Services.Contracts.Identity;
using Svm.Services.Contracts.Audit;
using Svm.Services.CrossCutting.Pipeline;
using Svm.Services.CrossCutting.Idempotency;
using Svm.Services.CrossCutting.DomainEvents;
using Svm.Services.CrossCutting.Consumption;

namespace Svm.Services.CrossCutting.Registration;

public static class FoundationServiceCollectionExtensions
{
    private static readonly Type[] PipelineTypes =
        [typeof(RequestKindBehavior<,>), typeof(ValidationBehavior<,>), typeof(AuthorizationBehavior<,>), typeof(IdempotencyBehavior<,>), typeof(PersonnelTransactionBehavior<,>)];

    public static IServiceCollection AddSvmRequestPipeline(this IServiceCollection services, IReadOnlyList<RequestBinding> bindings,
        IReadOnlyList<DomainEventBinding>? domainEvents = null, DomainEventOptions? domainEventOptions = null)
    {
        if (services.Any(d => d.ServiceType == typeof(RequestCatalog)))
            throw new InvalidOperationException("SVM foundation is already registered.");
        var catalog = new RequestCatalog(bindings);
        services.AddSvmDomainEvents(domainEvents ?? [], domainEventOptions);
        services.AddSingleton(catalog);
        services.AddScoped<ICallContext, ScopedCallContext>();
        services.AddScoped<IOperationContext, ScopedOperationContext>();
        services.AddScoped<IdempotencyCoordinator>();
        services.AddScoped<ISender, Mediator>();
        services.AddSingleton<ScopedRequestExecutor>();
        services.AddSingleton<IOperationResultRecovery, ScopedRequestExecutor>();
        foreach (var pipeline in PipelineTypes)
            services.AddScoped(typeof(IPipelineBehavior<,>), pipeline);
        foreach (var binding in bindings)
        {
            var response = binding.RequestType.GetInterfaces().Single(i => i.IsGenericType &&
                i.GetGenericTypeDefinition() == typeof(IRequest<>)).GenericTypeArguments[0];
            services.AddScoped(typeof(IRequestHandler<,>).MakeGenericType(binding.RequestType, response), binding.HandlerType);
            foreach (var validator in binding.ValidatorTypes)
                services.AddScoped(typeof(IValidator<>).MakeGenericType(binding.RequestType), validator);
        }
        return services;
    }

    /// <summary>Call after all host registrations, before building the service provider.</summary>
    public static IServiceCollection ValidateSvmFoundation(this IServiceCollection services)
    {
        services.ValidateSvmDomainEvents();
        services.ValidateSvmConsumption();
        var catalogs = services.Where(d => d.ServiceType == typeof(RequestCatalog)).ToArray();
        if (catalogs.Length != 1 || catalogs[0].ImplementationInstance is not RequestCatalog catalog)
            throw new InvalidOperationException("Exactly one immutable request catalog is required.");
        if (services.Any(d => d.IsKeyedService && (IsSinglePort(d.ServiceType) ||
            d.ServiceType.IsGenericType && (d.ServiceType.GetGenericTypeDefinition() == typeof(IRequestHandler<,>) ||
                d.ServiceType.GetGenericTypeDefinition() == typeof(IPipelineBehavior<,>) ||
                d.ServiceType.GetGenericTypeDefinition() == typeof(IValidator<>) || d.ServiceType.GetGenericTypeDefinition() == typeof(IIdempotencyRequestAdapter<,>)))))
            throw new InvalidOperationException("Keyed registrations cannot substitute framework ports, handlers or pipeline components.");
        foreach (var group in services.Where(d => IsSinglePort(d.ServiceType)).GroupBy(d => d.ServiceType))
            if (group.Count() != 1) throw new InvalidOperationException($"Conflicting default implementations for {group.Key.FullName}.");
        RequireImplementation(services, typeof(ICallContext), typeof(ScopedCallContext), ServiceLifetime.Scoped);
        RequireImplementation(services, typeof(IOperationContext), typeof(ScopedOperationContext), ServiceLifetime.Scoped);
        RequireImplementation(services, typeof(IdempotencyCoordinator), typeof(IdempotencyCoordinator), ServiceLifetime.Scoped);
        RequireImplementation(services, typeof(IOperationResultRecovery), typeof(ScopedRequestExecutor), ServiceLifetime.Singleton);
        RequireImplementation(services, typeof(ISender), typeof(Mediator), ServiceLifetime.Scoped);
        RequireImplementation(services, typeof(ScopedRequestExecutor), typeof(ScopedRequestExecutor), ServiceLifetime.Singleton);
        if (services.Any(d => d.ServiceType == typeof(IIntegrationEventOutbox))) RequireScopedPort(services, typeof(IIntegrationEventOutbox));

        var behaviors = services.Where(d => d.ServiceType.IsGenericType &&
            d.ServiceType.GetGenericTypeDefinition() == typeof(IPipelineBehavior<,>)).ToArray();
        if (behaviors.Length != PipelineTypes.Length || behaviors.Where((d, i) =>
                d.ServiceType != typeof(IPipelineBehavior<,>) || d.ImplementationType != PipelineTypes[i] || d.Lifetime != ServiceLifetime.Scoped).Any())
            throw new InvalidOperationException("The required request pipeline order/implementations cannot be replaced or bypassed.");

        if (catalog.Bindings.Count > 0)
        {
            RequireScopedPort(services, typeof(ITrustedCallContextSource));
            RequireScopedPort(services, typeof(IRequestAuthorizer));
        }
        if (catalog.Bindings.Any(b => PersonnelWriteCapabilities.Contains(b.RequestType) || PersonnelManagementCapabilities.Contains(b.RequestType)))
        {
            RequireScopedPort(services, typeof(IUnitOfWork));
            RequireScopedPort(services, typeof(IPersonnelService));
            RequireScopedPort(services, typeof(IAuditWriter));
            RequireScopedPort(services, typeof(ISessionProofSource));
        }
        if (catalog.Bindings.Any(b => PersonnelManagementCapabilities.Contains(b.RequestType)))
        {
            RequireScopedPort(services, typeof(IPersonnelAdministration));
            RequireScopedPort(services, typeof(IUserQueries));
            RequireScopedPort(services, typeof(IOperationResultStore));
        }
        if (catalog.Bindings.Any(b => b.RequestType == typeof(GetUserQuery) || b.RequestType == typeof(ListUsersQuery)))
            RequireScopedPort(services, typeof(IUserQueries));
        var expectedHandlers = new HashSet<Type>();
        var expectedValidators = new HashSet<(Type Contract, Type Implementation)>();
        var expectedAdapters = new Dictionary<Type, System.Reflection.Assembly>();
        foreach (var binding in catalog.Bindings)
        {
            var response = binding.RequestType.GetInterfaces().Single(i => i.IsGenericType &&
                i.GetGenericTypeDefinition() == typeof(IRequest<>)).GenericTypeArguments[0];
            var contract = typeof(IRequestHandler<,>).MakeGenericType(binding.RequestType, response);
            expectedHandlers.Add(contract);
            RequireImplementation(services, contract, binding.HandlerType, ServiceLifetime.Scoped);
            if (catalog.GetPolicy(binding.RequestType).Idempotency == IdempotencyMode.OperationResult)
                expectedAdapters.Add(typeof(IIdempotencyRequestAdapter<,>).MakeGenericType(binding.RequestType, response), binding.HandlerType.Assembly);
            foreach (var validator in binding.ValidatorTypes)
                expectedValidators.Add((typeof(IValidator<>).MakeGenericType(binding.RequestType), validator));
        }
        foreach (var descriptor in services.Where(d => d.ServiceType.IsGenericType))
        {
            var definition = descriptor.ServiceType.GetGenericTypeDefinition();
            if ((definition == typeof(IRequestHandler<,>) && !expectedHandlers.Contains(descriptor.ServiceType)) ||
                definition == typeof(IRequestHandler<>) || definition == typeof(IStreamRequestHandler<,>) ||
                definition == typeof(IStreamPipelineBehavior<,>))
                throw new InvalidOperationException("A Handler bypasses the approved request catalog.");
            if (definition == typeof(IValidator<>) && (descriptor.ImplementationType is null ||
                descriptor.Lifetime != ServiceLifetime.Scoped || !expectedValidators.Remove((descriptor.ServiceType, descriptor.ImplementationType))))
                throw new InvalidOperationException("A Validator bypasses or duplicates the approved registration.");
            if (definition == typeof(IIdempotencyRequestAdapter<,>))
            {
                if (!expectedAdapters.Remove(descriptor.ServiceType, out var assembly) || descriptor.IsKeyedService ||
                    descriptor.Lifetime != ServiceLifetime.Scoped || descriptor.ImplementationType is not { IsAbstract: false, ContainsGenericParameters: false } implementation ||
                    implementation.Assembly != assembly || !descriptor.ServiceType.IsAssignableFrom(implementation))
                    throw new InvalidOperationException("An idempotency adapter bypasses the closed administration catalog or its scoped registration.");
            }
        }
        if (expectedAdapters.Count != 0) throw new InvalidOperationException("An active idempotency adapter is missing.");
        if (expectedValidators.Count != 0) throw new InvalidOperationException("A required Validator registration was removed.");
        return services;
    }

    private static bool IsSinglePort(Type type) => type == typeof(ICallContext) || type == typeof(ISender) ||
        type == typeof(ScopedRequestExecutor) || type == typeof(IOperationResultRecovery) || type.IsInterface && !type.IsGenericType &&
        type.Assembly == typeof(ICallContext).Assembly;

    private static void RequireScopedPort(IServiceCollection services, Type type)
    {
        var matches = services.Where(d => d.ServiceType == type).ToArray();
        if (matches.Length != 1 || matches[0].Lifetime != ServiceLifetime.Scoped)
            throw new InvalidOperationException($"Active requests require exactly one scoped {type.Name} implementation.");
    }

    private static void RequireImplementation(IServiceCollection services, Type contract, Type implementation, ServiceLifetime lifetime)
    {
        var matches = services.Where(d => d.ServiceType == contract).ToArray();
        if (matches.Length != 1 || matches[0].ImplementationType != implementation || matches[0].Lifetime != lifetime)
            throw new InvalidOperationException($"Invalid registration/lifetime for {contract.FullName}.");
    }
}
