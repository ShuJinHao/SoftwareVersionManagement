using Svm.Services.Contracts.Framework;
using Svm.Services.Contracts.Tasks;
using Svm.Services.Contracts.Messaging.V1;

namespace Svm.Application.Tasks;

internal sealed class TaskPreparationAvailableHandler(ITaskWorkflow tasks) : IIntegrationEventHandler<TaskPreparationAvailableV1>
{
    public async Task<OperationResultReference> HandleAsync(TaskPreparationAvailableV1 x, IntegrationConsumptionContext context, CancellationToken ct)
    { await tasks.AcceptAsync(x.WorkId, x.DispatchSequence, x.EventId, ct); return new(x.EventId, OperationStatus.Completed, x.WorkId); }
}
internal sealed class TaskControlAvailableHandler(ITaskWorkflow tasks) : IIntegrationEventHandler<TaskControlAvailableV1>
{
    public async Task<OperationResultReference> HandleAsync(TaskControlAvailableV1 x, IntegrationConsumptionContext context, CancellationToken ct)
    { await tasks.AcceptAsync(x.WorkId, x.DispatchSequence, x.EventId, ct); return new(x.EventId, OperationStatus.Completed, x.WorkId); }
}
