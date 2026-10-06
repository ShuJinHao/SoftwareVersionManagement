using MediatR;
using Microsoft.Extensions.DependencyInjection;

namespace Svm.Services.CrossCutting.Pipeline;

/// <summary>
/// Host-only dispatcher for one background invocation. The new scope obtains its own trusted
/// context source; no ambient HTTP actor or caller-supplied role is copied into it.
/// </summary>
public sealed class ScopedRequestExecutor(IServiceScopeFactory scopeFactory)
{
    public async Task<TResponse> SendAsync<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await using var scope = scopeFactory.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ISender>().Send(request, cancellationToken);
    }
}
