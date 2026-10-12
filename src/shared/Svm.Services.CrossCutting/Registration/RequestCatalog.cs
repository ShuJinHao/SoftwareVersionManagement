using System.Reflection;
using FluentValidation;
using MediatR;
using Svm.Services.Contracts.Framework;
using Svm.Services.Contracts.Identity;
using Svm.Services.Contracts.Catalog;
using Svm.Services.Contracts.Instances;
using Svm.Services.Contracts.Packages;

namespace Svm.Services.CrossCutting.Registration;

public sealed class RequestCatalog
{
    private readonly IReadOnlyDictionary<Type, RequestPolicy> _policies;
    internal IReadOnlyList<RequestBinding> Bindings { get; }

    internal RequestCatalog(IEnumerable<RequestBinding> bindings)
    {
        Bindings = Array.AsReadOnly(bindings.ToArray());
        var policies = new Dictionary<Type, RequestPolicy>();
        var operations = new HashSet<(ModuleOwner, string)>();
        foreach (var binding in Bindings)
        {
            var policy = Validate(binding);
            if (!policies.TryAdd(binding.RequestType, policy))
                throw new InvalidOperationException($"Duplicate request registration: {binding.RequestType.FullName}.");
            if (!operations.Add((policy.Owner, policy.Operation)))
                throw new InvalidOperationException($"Duplicate owner/operation key: {policy.Owner}/{policy.Operation}.");
        }
        _policies = policies;
    }

    public RequestPolicy GetPolicy(Type requestType) => _policies.TryGetValue(requestType, out var policy)
        ? policy : throw new RequestRejectedException(RequestFailure.ConfigurationInvalid);

    private static RequestPolicy Validate(RequestBinding binding)
    {
        var type = binding.RequestType;
        if (!type.IsClass || type.IsAbstract || type.ContainsGenericParameters)
            throw new InvalidOperationException("Only closed concrete request types may be registered.");
        var metadata = type.GetCustomAttribute<RequestPolicyAttribute>(inherit: false) ??
            throw new InvalidOperationException($"Missing request policy: {type.FullName}.");
        var policy = new RequestPolicy(metadata);
        if (string.IsNullOrWhiteSpace(policy.Operation) || !Enum.IsDefined(policy.Owner) ||
            !Enum.IsDefined(policy.Kind) || !Enum.IsDefined(policy.Scope) || !Enum.IsDefined(policy.Validation) ||
            !Enum.IsDefined(policy.Transaction) || !Enum.IsDefined(policy.Idempotency) ||
            policy.Actors.Count == 0 || policy.Actors.Distinct().Count() != policy.Actors.Count ||
            policy.Actors.Any(a => !Enum.IsDefined(a) || !IsAllowedActor(policy.Kind, a)))
            throw new InvalidOperationException($"Invalid request classification: {type.FullName}.");
        if (policy.Actors.Contains(ActorKind.Anonymous))
        {
            if (policy.Kind != RequestKind.Session || policy.Scope != RequestScope.Global ||
                policy.Actors.Count != 1 || !string.IsNullOrEmpty(policy.Permission))
                throw new InvalidOperationException("Anonymous requests must be explicitly isolated session operations.");
        }
        else if (string.IsNullOrWhiteSpace(policy.Permission))
            throw new InvalidOperationException($"Missing permission declaration: {type.FullName}.");
        if (policy.Kind is RequestKind.Client or RequestKind.Enrollment or RequestKind.Recovery && policy.Scope == RequestScope.Global)
            throw new InvalidOperationException("Instance and grant requests cannot have a global scope.");
        if (policy.Kind == RequestKind.Enrollment && policy.Scope != RequestScope.Software ||
            policy.Kind == RequestKind.Recovery && policy.Scope != RequestScope.Instance ||
            policy.Scope == RequestScope.InternalWork && policy.Kind != RequestKind.Internal)
            throw new InvalidOperationException("Request scope does not match the entry classification.");

        var shapes = type.GetInterfaces().Where(i => i.IsGenericType &&
            (i.GetGenericTypeDefinition() == typeof(IQuery<>) || i.GetGenericTypeDefinition() == typeof(ICommand<>))).ToArray();
        if (shapes.Length != 1 || type.GetInterfaces().Count(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IRequest<>)) != 1 ||
            typeof(IRequest).IsAssignableFrom(type) || type.GetInterfaces().Any(i => i.IsGenericType &&
                i.GetGenericTypeDefinition() == typeof(IStreamRequest<>)))
            throw new InvalidOperationException("A request must be exactly one typed SVM Command or Query.");
        var isCommand = shapes[0].GetGenericTypeDefinition() == typeof(ICommand<>);
        var management = PersonnelManagementCapabilities.Contains(type);
        var catalogWrite = CatalogCapabilities.IsWrite(type);
        var instanceWrite = InstanceCapabilities.IsWrite(type);
        var mode = type == typeof(SubmitStatusReportCommand) ? IdempotencyMode.ReportSequence :
            type == typeof(RegisterInstanceCommand) || type == typeof(RecoverInstanceCommand) ? IdempotencyMode.EnrollmentProtocol :
            management || catalogWrite || instanceWrite || PackageCapabilities.IsIdempotent(type) ? IdempotencyMode.OperationResult : IdempotencyMode.None;
        if (isCommand ? (!PersonnelWriteCapabilities.Contains(type) && !management && !catalogWrite && !instanceWrite && !PackageCapabilities.IsWrite(type)) || policy.Transaction != (type == typeof(UploadContentCommand) ? TransactionMode.PhasedFile : TransactionMode.DatabaseAtomic) ||
                policy.Idempotency != mode
            : policy.Transaction != TransactionMode.ReadOnly || policy.Idempotency != IdempotencyMode.None)
            throw new InvalidOperationException("Only the closed personnel, catalog, instance-access and package writes are activated.");

        var response = type.GetInterfaces().Single(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IRequest<>)).GenericTypeArguments[0];
        var handlerContract = typeof(IRequestHandler<,>).MakeGenericType(type, response);
        if (!binding.HandlerType.IsClass || binding.HandlerType.IsAbstract || binding.HandlerType.ContainsGenericParameters ||
            !handlerContract.IsAssignableFrom(binding.HandlerType))
            throw new InvalidOperationException("Handler type does not implement the registered request/response contract.");
        var validatorContract = typeof(IValidator<>).MakeGenericType(type);
        if (binding.ValidatorTypes.Distinct().Count() != binding.ValidatorTypes.Count ||
            binding.ValidatorTypes.Any(t => !t.IsClass || t.IsAbstract || t.ContainsGenericParameters || !validatorContract.IsAssignableFrom(t)))
            throw new InvalidOperationException("Invalid or duplicate Validator registration.");
        if (policy.Validation == ValidationMode.Required && binding.ValidatorTypes.Count == 0 ||
            policy.Validation == ValidationMode.ExplicitlyNone &&
            (binding.ValidatorTypes.Count != 0 || string.IsNullOrWhiteSpace(policy.ValidationReason)))
            throw new InvalidOperationException("Validator strategy must have validators or an explicit no-validation justification.");
        return policy;
    }

    private static bool IsAllowedActor(RequestKind kind, ActorKind actor) => kind switch
    {
        RequestKind.Session => actor is ActorKind.Anonymous or ActorKind.Human,
        RequestKind.Manage => actor is ActorKind.Human or ActorKind.ManagementSystem,
        RequestKind.Client => actor == ActorKind.Instance,
        RequestKind.Enrollment => actor == ActorKind.EnrollmentGrant,
        RequestKind.Recovery => actor == ActorKind.RecoveryGrant,
        RequestKind.Internal => actor == ActorKind.Service,
        _ => false
    };
}
