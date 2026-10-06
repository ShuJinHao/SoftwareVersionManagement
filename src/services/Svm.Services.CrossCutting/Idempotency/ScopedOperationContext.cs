using Svm.Services.Contracts.Framework;

namespace Svm.Services.CrossCutting.Idempotency;

internal sealed class ScopedOperationContext : IOperationContext
{
    private OperationIdentity? _current;
    public OperationIdentity? Current => _current;
    internal void Bind(OperationIdentity identity)
    {
        if (Interlocked.CompareExchange(ref _current, identity, null) is not null)
            throw new RequestRejectedException(RequestFailure.ConfigurationInvalid);
    }
}
