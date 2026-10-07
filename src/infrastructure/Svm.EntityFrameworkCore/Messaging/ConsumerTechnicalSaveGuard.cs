using Microsoft.EntityFrameworkCore.Diagnostics;
using Svm.EntityFrameworkCore.Framework;

namespace Svm.EntityFrameworkCore.Messaging;

// Runs after configured save interceptors so they cannot smuggle module changes into a native technical save.
internal sealed class ConsumerTechnicalSaveGuard(SvmDbContext context) : SaveChangesInterceptor
{
    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        if (context.ConsumerTransactions.NativeActive && !context.ConsumerTransactions.BusinessActive)
            context.EnsureNativeTechnicalSave();
        return ValueTask.FromResult(result);
    }
}
