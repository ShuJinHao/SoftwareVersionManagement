using MediatR;
using Svm.Services.Contracts.Framework;
using Svm.Services.Contracts.Tasks;
using Svm.Services.Contracts.Catalog;

namespace Svm.Application.Tasks;

internal sealed class GetDeploymentCapabilitiesQueryHandler(TaskOptions options, SiteCatalogOptions site) : IRequestHandler<GetDeploymentCapabilitiesQuery, DeploymentCapabilities>
{ public Task<DeploymentCapabilities> Handle(GetDeploymentCapabilitiesQuery x, CancellationToken ct) { ct.ThrowIfCancellationRequested(); options.Validate(); return Task.FromResult(new DeploymentCapabilities(options.DefaultStartLocalTime!, options.DefaultLatestStartLocalTime!, site.Require().SiteTimeZone, options.BatchSize!.Value, options.FailureLimit!.Value, options.ResultWaitSeconds!.Value, options.SelectionChunkSize!.Value, options.MaxSelectionMembers!.Value, options.PollRetrySeconds!.Value)); } }
internal sealed class GetTargetSelectionQueryHandler(ITaskQueries queries) : IRequestHandler<GetTargetSelectionQuery, SelectionView>
{ public Task<SelectionView> Handle(GetTargetSelectionQuery x, CancellationToken ct) => queries.SelectionAsync(x.SelectionId, ct); }
internal sealed class GetDeploymentQueryHandler(ITaskQueries queries) : IRequestHandler<GetDeploymentQuery, DeploymentView>
{ public Task<DeploymentView> Handle(GetDeploymentQuery x, CancellationToken ct) => queries.DeploymentAsync(x.DeploymentId, ct); }
internal sealed class ListDeploymentsQueryHandler(ITaskQueries queries) : IRequestHandler<ListDeploymentsQuery, TaskPage<DeploymentView>>
{ public Task<TaskPage<DeploymentView>> Handle(ListDeploymentsQuery x, CancellationToken ct) => queries.DeploymentsAsync(x.Input, ct); }
internal sealed class ListDeploymentTargetsQueryHandler(ITaskQueries queries) : IRequestHandler<ListDeploymentTargetsQuery, TaskPage<AdmissionView>>
{ public Task<TaskPage<AdmissionView>> Handle(ListDeploymentTargetsQuery x, CancellationToken ct) => queries.AdmissionsAsync(x.DeploymentId, x.PageSize, x.After, x.Decision,x.ReasonCode, ct); }
internal sealed class GetDeploymentBatchesQueryHandler(ITaskQueries queries) : IRequestHandler<GetDeploymentBatchesQuery, TaskPage<BatchView>>
{ public Task<TaskPage<BatchView>> Handle(GetDeploymentBatchesQuery x, CancellationToken ct) => queries.BatchesAsync(x.DeploymentId,x.PageSize,x.After,ct); }
internal sealed class ListInstanceTasksQueryHandler(ITaskQueries queries) : IRequestHandler<ListInstanceTasksQuery, TaskPage<TaskView>>
{ public Task<TaskPage<TaskView>> Handle(ListInstanceTasksQuery x, CancellationToken ct) => queries.TasksAsync(x.Input, null, ct); }
internal sealed class GetInstanceTaskQueryHandler(ITaskQueries queries) : IRequestHandler<GetInstanceTaskQuery, TaskView>
{ public Task<TaskView> Handle(GetInstanceTaskQuery x, CancellationToken ct) => queries.TaskAsync(x.TaskId, ct); }
internal sealed class ListTaskReceiptsQueryHandler(ITaskQueries queries) : IRequestHandler<ListTaskReceiptsQuery, TaskPage<ReceiptView>>
{ public Task<TaskPage<ReceiptView>> Handle(ListTaskReceiptsQuery x, CancellationToken ct) => queries.ReceiptsAsync(x.TaskId, x.PageSize, x.After, ct); }
internal sealed class GetTaskWorkQueryHandler(ITaskQueries queries) : IRequestHandler<GetTaskWorkQuery, TaskWorkView>
{ public Task<TaskWorkView> Handle(GetTaskWorkQuery x, CancellationToken ct) => queries.WorkAsync(x.WorkId, ct); }
internal sealed class ListTaskControlItemsQueryHandler(ITaskQueries queries) : IRequestHandler<ListTaskControlItemsQuery, TaskPage<ControlItemView>>
{ public Task<TaskPage<ControlItemView>> Handle(ListTaskControlItemsQuery x, CancellationToken ct) => queries.ControlItemsAsync(x.WorkId, x.PageSize, x.After, ct); }
internal sealed class ListClientTasksQueryHandler(ITaskQueries queries, ICallContext calls) : IRequestHandler<ListClientTasksQuery, TaskPage<TaskView>>
{ public Task<TaskPage<TaskView>> Handle(ListClientTasksQuery x, CancellationToken ct) => queries.TasksAsync(x.Input, calls.Current!.Actor.InstanceId, ct); }
internal sealed class GetClientTaskQueryHandler(ITaskQueries queries) : IRequestHandler<GetClientTaskQuery, TaskView>
{ public Task<TaskView> Handle(GetClientTaskQuery x, CancellationToken ct) => queries.TaskAsync(x.TaskId, ct); }
internal sealed class GetTaskAuthorityQueryHandler(ITaskWorkflow tasks) : IRequestHandler<GetTaskAuthorityQuery, TaskWorkAuthority>
{ public Task<TaskWorkAuthority> Handle(GetTaskAuthorityQuery x, CancellationToken ct) => tasks.AuthorityAsync(x.WorkId, false, ct); }
internal sealed class GetIntegrationMaterialsQueryHandler(ITaskQueries queries) : IRequestHandler<GetIntegrationMaterialsQuery, IReadOnlyList<IntegrationMaterialView>>
{ public Task<IReadOnlyList<IntegrationMaterialView>> Handle(GetIntegrationMaterialsQuery x, CancellationToken ct) => queries.MaterialsAsync(x.ReleaseId, x.PageSize, x.After, ct); }
