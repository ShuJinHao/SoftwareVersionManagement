using MediatR;
using Svm.Services.Contracts.Framework;
using Svm.Services.CrossCutting.Registration;

namespace Svm.Services.CrossCutting.Pipeline;

public sealed class AuthorizationBehavior<TRequest, TResponse>(
    RequestCatalog catalog, ICallContext context, IRequestAuthorizer? authorizer = null)
    : IPipelineBehavior<TRequest, TResponse> where TRequest : notnull
{
    public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var call = context.Current ?? throw new RequestRejectedException(RequestFailure.AuthenticationRequired);
        var policy = catalog.GetPolicy(request.GetType());
        if (authorizer is null) throw new RequestRejectedException(RequestFailure.ConfigurationInvalid);
        await RequestAuthorization.CheckAsync(authorizer, request, policy, call, cancellationToken);
        return await next();
    }
}
