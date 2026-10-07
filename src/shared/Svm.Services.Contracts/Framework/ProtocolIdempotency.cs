namespace Svm.Services.Contracts.Framework;

/// <summary>Natural-key protocols own their committed facts; they never cache a full request or response.</summary>
public sealed record ProtocolResult<T>(T Value);
public interface IProtocolRequestAdapter<in TRequest, TResponse> where TRequest : notnull
{
    Task<ProtocolResult<TResponse>?> FindCommittedAsync(TRequest request, CancellationToken cancellationToken);
}
