using Svm.Services.Contracts.Framework;

namespace Svm.Services.Contracts.Tasks;

[RequestPolicy("tasks.CreateTargetSelectionCommand", ModuleOwner.Tasks, RequestKind.Manage, RequestScope.Software,
    TransactionMode.DatabaseAtomic, IdempotencyMode.OperationResult, ValidationMode.Required, ActorKind.Human, Permission = "deployment.create")]
public sealed record CreateTargetSelectionCommand(Guid Key, SelectionInput Input) : ICommand<SelectionView>;

[RequestPolicy("tasks.PutTargetChunkCommand", ModuleOwner.Tasks, RequestKind.Manage, RequestScope.Software,
    TransactionMode.DatabaseAtomic, IdempotencyMode.SelectionChunk, ValidationMode.Required, ActorKind.Human, Permission = "deployment.create")]
public sealed record PutTargetChunkCommand(Guid SelectionId, int ChunkNo, IReadOnlyList<Guid> InstanceIds) : ICommand<SelectionView>;

[RequestPolicy("tasks.SealTargetSelectionCommand", ModuleOwner.Tasks, RequestKind.Manage, RequestScope.Software,
    TransactionMode.DatabaseAtomic, IdempotencyMode.OperationResult, ValidationMode.Required, ActorKind.Human, Permission = "deployment.create")]
public sealed record SealTargetSelectionCommand(Guid Key, Guid SelectionId, long ExpectedRevision, int ExpectedChunkCount, int ExpectedMemberCount) : ICommand<SelectionView>;

[RequestPolicy("tasks.CreateDeploymentCommand", ModuleOwner.Tasks, RequestKind.Manage, RequestScope.Software,
    TransactionMode.DatabaseAtomic, IdempotencyMode.OperationResult, ValidationMode.Required, ActorKind.Human, Permission = "deployment.create")]
public sealed record CreateDeploymentCommand(Guid Key, DeploymentInput Input) : ICommand<DeploymentView>;

[RequestPolicy("tasks.ControlDeploymentCommand", ModuleOwner.Tasks, RequestKind.Manage, RequestScope.Software,
    TransactionMode.DatabaseAtomic, IdempotencyMode.OperationResult, ValidationMode.Required, ActorKind.Human, Permission = "deployment.control")]
public sealed record ControlDeploymentCommand(Guid Key, Guid DeploymentId, long ExpectedRevision, string Action, string Reason, IReadOnlyList<FailureReview>? ReviewedFailures = null) : ICommand<DeploymentView>;

[RequestPolicy("tasks.CreateDeploymentControlWorkCommand", ModuleOwner.Tasks, RequestKind.Manage, RequestScope.Software,
    TransactionMode.DatabaseAtomic, IdempotencyMode.OperationResult, ValidationMode.Required, ActorKind.Human, Permission = "deployment.control")]
public sealed record CreateDeploymentControlWorkCommand(Guid Key, Guid DeploymentId, long ExpectedRevision, string Action, string Reason, TaskWindow? Window = null) : ICommand<TaskWorkView>;

[RequestPolicy("tasks.ControlInstanceTaskCommand", ModuleOwner.Tasks, RequestKind.Manage, RequestScope.Software,
    TransactionMode.DatabaseAtomic, IdempotencyMode.OperationResult, ValidationMode.Required, ActorKind.Human, Permission = "deployment.control")]
public sealed record ControlInstanceTaskCommand(Guid Key, Guid TaskId, long ExpectedRevision, string Action, string Reason, string? OnsiteEvidence = null, bool NoActiveInstallationConfirmed = false) : ICommand<TaskView>;

[RequestPolicy("tasks.ClaimInstanceTaskCommand", ModuleOwner.Tasks, RequestKind.Client, RequestScope.Instance,
    TransactionMode.DatabaseAtomic, IdempotencyMode.OperationResult, ValidationMode.Required, ActorKind.Instance, Permission = "task.execute")]
public sealed record ClaimInstanceTaskCommand(Guid Key, Guid TaskId) : ICommand<Guid>;

[RequestPolicy("tasks.StartInstanceTaskCommand", ModuleOwner.Tasks, RequestKind.Client, RequestScope.Instance,
    TransactionMode.DatabaseAtomic, IdempotencyMode.OperationResult, ValidationMode.Required, ActorKind.Instance, Permission = "task.execute")]
public sealed record StartInstanceTaskCommand(Guid Key, Guid AttemptId, StartInput Input) : ICommand<StartGrant>;

[RequestPolicy("tasks.SubmitTaskReceiptCommand", ModuleOwner.Tasks, RequestKind.Client, RequestScope.Instance,
    TransactionMode.DatabaseAtomic, IdempotencyMode.ReceiptSequence, ValidationMode.Required, ActorKind.Instance, Permission = "task.execute")]
public sealed record SubmitTaskReceiptCommand(Guid AttemptId, ReceiptInput Input) : ICommand<ReceiptResult>;

[RequestPolicy("tasks.RecordIntegrationMaterialCommand", ModuleOwner.Releases, RequestKind.Manage, RequestScope.Software,
    TransactionMode.DatabaseAtomic, IdempotencyMode.OperationResult, ValidationMode.Required, ActorKind.Human, Permission = "release.upload")]
public sealed record RecordIntegrationMaterialCommand(Guid Key, Guid ReleaseId, IntegrationMaterialInput Input) : ICommand<IntegrationMaterialView>;

[RequestPolicy("tasks.ClaimTaskWorkCommand", ModuleOwner.Tasks, RequestKind.Internal, RequestScope.InternalWork,
    TransactionMode.DatabaseAtomic, IdempotencyMode.None, ValidationMode.Required, ActorKind.Service, Permission = "task.execute")]
public sealed record ClaimTaskWorkCommand(Guid WorkId, Guid LeaseToken) : ICommand<TaskLease?>;

[RequestPolicy("tasks.AdvanceTaskWorkCommand", ModuleOwner.Tasks, RequestKind.Internal, RequestScope.InternalWork,
    TransactionMode.DatabaseAtomic, IdempotencyMode.None, ValidationMode.Required, ActorKind.Service, Permission = "task.execute")]
public sealed record AdvanceTaskWorkCommand(TaskLease Lease) : ICommand<TaskWorkView>;

[RequestPolicy("tasks.MaterializeTaskTargetsCommand", ModuleOwner.Tasks, RequestKind.Internal, RequestScope.InternalWork,
    TransactionMode.DatabaseAtomic, IdempotencyMode.None, ValidationMode.Required, ActorKind.Service, Permission = "task.execute")]
public sealed record MaterializeTaskTargetsCommand(TaskLease Lease) : ICommand<SelectionView>;

[RequestPolicy("tasks.FailTaskWorkCommand", ModuleOwner.Tasks, RequestKind.Internal, RequestScope.InternalWork,
    TransactionMode.DatabaseAtomic, IdempotencyMode.None, ValidationMode.Required, ActorKind.Service, Permission = "task.execute")]
public sealed record FailTaskWorkCommand(TaskLease Lease, string Code) : ICommand<bool>;

[RequestPolicy("tasks.GetDeploymentCapabilitiesQuery", ModuleOwner.Tasks, RequestKind.Manage, RequestScope.Software,
    TransactionMode.ReadOnly, IdempotencyMode.None, ValidationMode.Required, ActorKind.Human, Permission = "deployment.create")]
public sealed record GetDeploymentCapabilitiesQuery(Guid SoftwareId) : IQuery<DeploymentCapabilities>;

[RequestPolicy("tasks.GetTargetSelectionQuery", ModuleOwner.Tasks, RequestKind.Manage, RequestScope.Software,
    TransactionMode.ReadOnly, IdempotencyMode.None, ValidationMode.Required, ActorKind.Human, Permission = "deployment.create")]
public sealed record GetTargetSelectionQuery(Guid SelectionId) : IQuery<SelectionView>;

[RequestPolicy("tasks.GetDeploymentQuery", ModuleOwner.Tasks, RequestKind.Manage, RequestScope.Software,
    TransactionMode.ReadOnly, IdempotencyMode.None, ValidationMode.Required, ActorKind.Human, Permission = "instance.read")]
public sealed record GetDeploymentQuery(Guid DeploymentId) : IQuery<DeploymentView>;

[RequestPolicy("tasks.ListDeploymentsQuery", ModuleOwner.Tasks, RequestKind.Manage, RequestScope.Software,
    TransactionMode.ReadOnly, IdempotencyMode.None, ValidationMode.Required, ActorKind.Human, Permission = "instance.read")]
public sealed record ListDeploymentsQuery(TaskListInput Input) : IQuery<TaskPage<DeploymentView>>;

[RequestPolicy("tasks.ListDeploymentTargetsQuery", ModuleOwner.Tasks, RequestKind.Manage, RequestScope.Software,
    TransactionMode.ReadOnly, IdempotencyMode.None, ValidationMode.Required, ActorKind.Human, Permission = "instance.read")]
public sealed record ListDeploymentTargetsQuery(Guid DeploymentId, int PageSize = 50, Guid? After = null, string? Decision = null, string? ReasonCode = null) : IQuery<TaskPage<AdmissionView>>;

[RequestPolicy("tasks.GetDeploymentBatchesQuery", ModuleOwner.Tasks, RequestKind.Manage, RequestScope.Software,
    TransactionMode.ReadOnly, IdempotencyMode.None, ValidationMode.Required, ActorKind.Human, Permission = "instance.read")]
public sealed record GetDeploymentBatchesQuery(Guid DeploymentId, int PageSize = 50, Guid? After = null) : IQuery<TaskPage<BatchView>>;

[RequestPolicy("tasks.ListInstanceTasksQuery", ModuleOwner.Tasks, RequestKind.Manage, RequestScope.Software,
    TransactionMode.ReadOnly, IdempotencyMode.None, ValidationMode.Required, ActorKind.Human, Permission = "instance.read")]
public sealed record ListInstanceTasksQuery(TaskListInput Input) : IQuery<TaskPage<TaskView>>;

[RequestPolicy("tasks.GetInstanceTaskQuery", ModuleOwner.Tasks, RequestKind.Manage, RequestScope.Software,
    TransactionMode.ReadOnly, IdempotencyMode.None, ValidationMode.Required, ActorKind.Human, Permission = "instance.read")]
public sealed record GetInstanceTaskQuery(Guid TaskId) : IQuery<TaskView>;

[RequestPolicy("tasks.ListTaskReceiptsQuery", ModuleOwner.Tasks, RequestKind.Manage, RequestScope.Software,
    TransactionMode.ReadOnly, IdempotencyMode.None, ValidationMode.Required, ActorKind.Human, Permission = "instance.read")]
public sealed record ListTaskReceiptsQuery(Guid TaskId, int PageSize = 50, Guid? After = null) : IQuery<TaskPage<ReceiptView>>;

[RequestPolicy("tasks.GetTaskWorkQuery", ModuleOwner.Tasks, RequestKind.Manage, RequestScope.Software,
    TransactionMode.ReadOnly, IdempotencyMode.None, ValidationMode.Required, ActorKind.Human, Permission = "instance.read")]
public sealed record GetTaskWorkQuery(Guid WorkId) : IQuery<TaskWorkView>;

[RequestPolicy("tasks.ListTaskControlItemsQuery", ModuleOwner.Tasks, RequestKind.Manage, RequestScope.Software,
    TransactionMode.ReadOnly, IdempotencyMode.None, ValidationMode.Required, ActorKind.Human, Permission = "instance.read")]
public sealed record ListTaskControlItemsQuery(Guid WorkId, int PageSize = 50, Guid? After = null) : IQuery<TaskPage<ControlItemView>>;

[RequestPolicy("tasks.ListClientTasksQuery", ModuleOwner.Tasks, RequestKind.Client, RequestScope.Instance,
    TransactionMode.ReadOnly, IdempotencyMode.None, ValidationMode.Required, ActorKind.Instance, Permission = "task.execute")]
public sealed record ListClientTasksQuery(TaskListInput Input) : IQuery<TaskPage<TaskView>>;

[RequestPolicy("tasks.GetClientTaskQuery", ModuleOwner.Tasks, RequestKind.Client, RequestScope.Instance,
    TransactionMode.ReadOnly, IdempotencyMode.None, ValidationMode.Required, ActorKind.Instance, Permission = "task.execute")]
public sealed record GetClientTaskQuery(Guid TaskId) : IQuery<TaskView>;

[RequestPolicy("tasks.GetTaskAuthorityQuery", ModuleOwner.Tasks, RequestKind.Internal, RequestScope.InternalWork,
    TransactionMode.ReadOnly, IdempotencyMode.None, ValidationMode.Required, ActorKind.Service, Permission = "task.execute")]
public sealed record GetTaskAuthorityQuery(Guid WorkId) : IQuery<TaskWorkAuthority>;

[RequestPolicy("tasks.GetIntegrationMaterialsQuery", ModuleOwner.Releases, RequestKind.Manage, RequestScope.Software,
    TransactionMode.ReadOnly, IdempotencyMode.None, ValidationMode.Required, ActorKind.Human, Permission = "software.read")]
public sealed record GetIntegrationMaterialsQuery(Guid ReleaseId, int PageSize = 50, Guid? After = null) : IQuery<IReadOnlyList<IntegrationMaterialView>>;

public static class TaskCapabilities
{
    public static bool IsProtocol(Type t) => t == typeof(PutTargetChunkCommand) || t == typeof(SubmitTaskReceiptCommand);
    public static bool IsInternal(Type t) => t == typeof(ClaimTaskWorkCommand) || t == typeof(AdvanceTaskWorkCommand) || t == typeof(MaterializeTaskTargetsCommand) || t == typeof(FailTaskWorkCommand) || t == typeof(GetTaskAuthorityQuery);
    public static bool IsWrite(Type t) => t == typeof(CreateTargetSelectionCommand) || t == typeof(PutTargetChunkCommand) || t == typeof(SealTargetSelectionCommand) || t == typeof(CreateDeploymentCommand) || t == typeof(ControlDeploymentCommand) || t == typeof(CreateDeploymentControlWorkCommand) || t == typeof(ControlInstanceTaskCommand) || t == typeof(ClaimInstanceTaskCommand) || t == typeof(StartInstanceTaskCommand) || t == typeof(SubmitTaskReceiptCommand) || t == typeof(RecordIntegrationMaterialCommand) || t == typeof(ClaimTaskWorkCommand) || t == typeof(AdvanceTaskWorkCommand) || t == typeof(MaterializeTaskTargetsCommand) || t == typeof(FailTaskWorkCommand);
    public static bool IsQuery(Type t) => t == typeof(GetDeploymentCapabilitiesQuery) || t == typeof(GetTargetSelectionQuery) || t == typeof(GetDeploymentQuery) || t == typeof(ListDeploymentsQuery) || t == typeof(ListDeploymentTargetsQuery) || t == typeof(GetDeploymentBatchesQuery) || t == typeof(ListInstanceTasksQuery) || t == typeof(GetInstanceTaskQuery) || t == typeof(ListTaskReceiptsQuery) || t == typeof(GetTaskWorkQuery) || t == typeof(ListTaskControlItemsQuery) || t == typeof(ListClientTasksQuery) || t == typeof(GetClientTaskQuery) || t == typeof(GetTaskAuthorityQuery) || t == typeof(GetIntegrationMaterialsQuery);
    public static bool Contains(Type t) => IsWrite(t) || IsQuery(t);
    public static bool IsIdempotent(Type t) => IsWrite(t) && !IsInternal(t) && !IsProtocol(t);
}
