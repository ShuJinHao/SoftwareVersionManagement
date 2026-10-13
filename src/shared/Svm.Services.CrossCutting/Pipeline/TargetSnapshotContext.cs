using Svm.Services.Contracts.Tasks;
using Svm.Services.Contracts.Framework;

namespace Svm.Services.CrossCutting.Pipeline;

internal sealed class TargetSnapshotContext : ITargetSnapshotContext
{
    public bool IsMaterializing { get; private set; }
    public int TimeoutSeconds { get; private set; }
    internal IDisposable Enter(MaterializeTaskTargetsCommand request, TaskOptions options)
    { ArgumentNullException.ThrowIfNull(request); options.Validate(); if (IsMaterializing) throw new PersistenceException(PersistenceFailure.InvalidTransactionNesting);
      IsMaterializing = true; TimeoutSeconds = options.SnapshotTimeoutSeconds!.Value; return new Exit(this); }
    private sealed class Exit(TargetSnapshotContext context) : IDisposable
    { public void Dispose() { context.IsMaterializing = false; context.TimeoutSeconds = 0; } }
}
