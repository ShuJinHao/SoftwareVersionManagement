using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Svm.Services.Contracts.Framework;
using Svm.Services.Contracts.Packages;

namespace Svm.Services.CrossCutting.Pipeline;

/// <summary>Only the closed file phases can cross scopes. Authorization obtains a fresh host proof.</summary>
internal sealed class CommandScopeExecutor(IServiceScopeFactory scopes) : ICommandScopeExecutor
{
    public async Task<OperationResult<T>> ExecuteAsync<T>(ICommand<T> command, CancellationToken token)
    {
        if (!PackageCapabilities.IsPhase(command.GetType())) throw new RequestRejectedException(RequestFailure.PermissionDenied);
        await using var scope = scopes.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ISender>().Send(command, token);
    }
    public async Task<T> QueryAsync<T>(IQuery<T> query, CancellationToken token)
    {
        if (query is not CheckUploadQuery) throw new RequestRejectedException(RequestFailure.PermissionDenied);
        await using var scope = scopes.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ISender>().Send(query, token);
    }
}
