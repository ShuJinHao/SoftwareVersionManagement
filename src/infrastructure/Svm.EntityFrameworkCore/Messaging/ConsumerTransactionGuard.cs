using Svm.EntityFrameworkCore.Framework;
using Svm.Services.Contracts.Framework;

namespace Svm.EntityFrameworkCore.Messaging;

internal sealed class ConsumerTransactionGuard(SvmDbContext context) : IIntegrationConsumptionTransaction
{
    public void EnsureActive()
    {
        if (!context.ConsumerTransactions.Owns(context.Database.CurrentTransaction))
            throw new IntegrationConsumptionException(ConsumptionFailure.TransactionRequired);
    }
}
