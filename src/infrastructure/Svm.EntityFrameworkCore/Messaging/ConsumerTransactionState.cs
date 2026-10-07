using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
using Svm.EntityFrameworkCore.Framework;
using Svm.Services.Contracts.Framework;

namespace Svm.EntityFrameworkCore.Messaging;

// Private capability established only while the registered native factory owns this scope.
internal sealed class ConsumerTransactionState : DbTransactionInterceptor
{
    private Guid? _transaction;
    private TransactionDomainEvents? _events;
    internal bool NativeActive { get; private set; }
    internal bool BusinessActive { get; set; }
    internal bool CommitAttempted { get; private set; }
    internal bool CommitConfirmed { get; private set; }
    internal Guid? OperationId { get; private set; }

    internal void Enter()
    {
        if (NativeActive) throw new IntegrationConsumptionException(ConsumptionFailure.ConfigurationInvalid);
        NativeActive = true;
    }
    internal void Exit() { NativeActive = false; BusinessActive = false; _transaction = null; _events = null; }
    internal bool Owns(IDbContextTransaction? transaction) => NativeActive && transaction is not null && transaction.TransactionId == _transaction;
    internal void Prepare(IDbContextTransaction transaction, Guid operationId, TransactionDomainEvents events)
    {
        if (!Owns(transaction) || _events is not null) throw new PersistenceException(PersistenceFailure.InvalidTransactionNesting, operationId);
        OperationId = operationId; _events = events;
    }
    internal void Validate() => _events?.ValidateAfterSave();

    public override ValueTask<DbTransaction> TransactionStartedAsync(DbConnection connection, TransactionEndEventData eventData,
        DbTransaction result, CancellationToken cancellationToken = default)
    {
        if (NativeActive)
        {
            _transaction = eventData.TransactionId; _events = null; OperationId = null;
            CommitAttempted = false; CommitConfirmed = false;
        }
        return ValueTask.FromResult(result);
    }
    public override ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction, TransactionEventData eventData,
        InterceptionResult result, CancellationToken cancellationToken = default)
    {
        if (NativeActive && _transaction == eventData.TransactionId)
        {
            Validate(); cancellationToken.ThrowIfCancellationRequested(); CommitAttempted = true;
        }
        return ValueTask.FromResult(result);
    }
    public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData,
        CancellationToken cancellationToken = default)
    {
        if (NativeActive && _transaction == eventData.TransactionId)
        {
            CommitConfirmed = true; _events?.AcknowledgeCommitted(); _events = null;
        }
        return Task.CompletedTask;
    }
}
