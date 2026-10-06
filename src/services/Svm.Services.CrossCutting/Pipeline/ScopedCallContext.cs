using Svm.Services.Contracts.Framework;

namespace Svm.Services.CrossCutting.Pipeline;

internal sealed class ScopedCallContext : ICallContext
{
    // An empty scaffold can start without an identity adapter. Activating any request requires one.
    public ScopedCallContext(ITrustedCallContextSource? source = null) => Current = source?.GetCurrent();
    public CallContextSnapshot? Current { get; }
}
