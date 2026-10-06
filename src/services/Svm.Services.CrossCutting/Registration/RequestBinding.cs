using FluentValidation;
using MediatR;
using System.Reflection;

namespace Svm.Services.CrossCutting.Registration;

/// <summary>Explicit composition input. Production discovery is owned by Svm.Application.</summary>
public sealed record RequestBinding
{
    public RequestBinding(Type requestType, Type handlerType, params Type[] validatorTypes)
    {
        RequestType = requestType;
        HandlerType = handlerType;
        ValidatorTypes = Array.AsReadOnly((Type[])validatorTypes.Clone());
    }

    public Type RequestType { get; }
    public Type HandlerType { get; }
    public IReadOnlyList<Type> ValidatorTypes { get; }

    public static IReadOnlyList<RequestBinding> Discover(Assembly handlerAssembly, Assembly contractAssembly)
    {
        var handlers = handlerAssembly.GetTypes().Where(IsConcrete).ToArray();
        var requests = handlerAssembly.GetTypes().Concat(contractAssembly.GetTypes()).Distinct()
            .Where(t => IsConcrete(t) && typeof(IBaseRequest).IsAssignableFrom(t)).ToArray();
        var result = new List<RequestBinding>();
        foreach (var request in requests)
        {
            var matches = handlers.Where(t => t.GetInterfaces().Any(i => i.IsGenericType &&
                i.GetGenericTypeDefinition() == typeof(IRequestHandler<,>) && i.GenericTypeArguments[0] == request)).ToArray();
            if (matches.Length != 1)
                throw new InvalidOperationException($"{request.FullName} must have exactly one Handler in {handlerAssembly.GetName().Name}.");
            var validatorContract = typeof(IValidator<>).MakeGenericType(request);
            result.Add(new RequestBinding(request, matches[0], handlers.Where(validatorContract.IsAssignableFrom).ToArray()));
        }
        // Orphan handlers must not silently disappear from the scan.
        foreach (var handler in handlers.SelectMany(t => t.GetInterfaces()).Where(i => i.IsGenericType &&
                     i.GetGenericTypeDefinition() == typeof(IRequestHandler<,>)))
            if (!requests.Contains(handler.GenericTypeArguments[0]))
                throw new InvalidOperationException("A Handler targets a request outside the approved application/contract assemblies.");
        return result.AsReadOnly();
    }

    private static bool IsConcrete(Type type) => type.IsClass && !type.IsAbstract && !type.ContainsGenericParameters;
}
