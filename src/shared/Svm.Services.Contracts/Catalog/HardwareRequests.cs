using Svm.Services.Contracts.Framework;
using Svm.Services.Contracts.Instances;

namespace Svm.Services.Contracts.Catalog;

[RequestPolicy("ins.hardware.list", ModuleOwner.Instances, RequestKind.Manage, RequestScope.Global, TransactionMode.ReadOnly, IdempotencyMode.None, ValidationMode.Required, ActorKind.Human, Permission = "asset.read")]
public sealed record ListHardwareQuery(Guid DeviceId, HardwareListInput Input) : IQuery<CatalogPage<HardwareView>>;
[RequestPolicy("ins.hardware.get", ModuleOwner.Instances, RequestKind.Manage, RequestScope.Global, TransactionMode.ReadOnly, IdempotencyMode.None, ValidationMode.Required, ActorKind.Human, Permission = "asset.read")]
public sealed record GetHardwareQuery(Guid HardwareId) : IQuery<HardwareView>;
[RequestPolicy("ins.hardware-bindings.list", ModuleOwner.Instances, RequestKind.Manage, RequestScope.Global, TransactionMode.ReadOnly, IdempotencyMode.None, ValidationMode.Required, ActorKind.Human, Permission = "asset.read")]
public sealed record ListHardwareBindingsQuery(Guid HardwareId, HardwareListInput Input) : IQuery<CatalogPage<HardwareBindingView>>;
[RequestPolicy("ins.workspace.devices", ModuleOwner.Instances, RequestKind.Manage, RequestScope.Global, TransactionMode.ReadOnly, IdempotencyMode.None, ValidationMode.Required, ActorKind.Human, Permission = "asset.read")]
public sealed record GetWorkspaceDevicesQuery(HardwareListInput Input) : IQuery<WorkspacePage<WorkspaceDeviceView>>;
[RequestPolicy("ins.workspace.installations", ModuleOwner.Instances, RequestKind.Manage, RequestScope.Global, TransactionMode.ReadOnly, IdempotencyMode.None, ValidationMode.Required, ActorKind.Human, Permission = "asset.read")]
public sealed record GetWorkspaceInstallationsQuery(HardwareListInput Input) : IQuery<WorkspacePage<WorkspaceInstallationView>>;
[RequestPolicy("ins.hardware.create", ModuleOwner.Instances, RequestKind.Manage, RequestScope.Global, TransactionMode.DatabaseAtomic, IdempotencyMode.OperationResult, ValidationMode.Required, ActorKind.Human, Permission = "asset.manage")]
public sealed record CreateHardwareCommand(Guid Key, Guid DeviceId, string Type, string Name, string? AssetNumber, string? ManagementIp) : ICommand<HardwareView>;
[RequestPolicy("ins.hardware.update", ModuleOwner.Instances, RequestKind.Manage, RequestScope.Global, TransactionMode.DatabaseAtomic, IdempotencyMode.OperationResult, ValidationMode.Required, ActorKind.Human, Permission = "asset.manage")]
public sealed record UpdateHardwareCommand(Guid Key, Guid HardwareId, long ExpectedRevision, string Name, string? AssetNumber, string? ManagementIp, string Reason) : ICommand<HardwareView>;
[RequestPolicy("ins.hardware-bindings.create", ModuleOwner.Instances, RequestKind.Manage, RequestScope.Global, TransactionMode.DatabaseAtomic, IdempotencyMode.OperationResult, ValidationMode.Required, ActorKind.Human, Permission = "asset.manage")]
public sealed record CreateHardwareBindingCommand(Guid Key, Guid HardwareId, Guid? SoftwareId, NewHardwareSoftware? NewSoftware, string Reason) : ICommand<HardwareBindingView>;
[RequestPolicy("ins.hardware-bindings.revoke", ModuleOwner.Instances, RequestKind.Manage, RequestScope.Global, TransactionMode.DatabaseAtomic, IdempotencyMode.OperationResult, ValidationMode.Required, ActorKind.Human, Permission = "asset.manage")]
public sealed record RevokeHardwareBindingCommand(Guid Key, Guid HardwareId, Guid SoftwareId, long ExpectedRevision, string Reason) : ICommand<HardwareBindingView>;
[RequestPolicy("ins.instances.bind-hardware", ModuleOwner.Instances, RequestKind.Manage, RequestScope.Instance, TransactionMode.DatabaseAtomic, IdempotencyMode.OperationResult, ValidationMode.Required, ActorKind.Human, Permission = "instance.manage")]
public sealed record BindInstanceHardwareCommand(Guid Key, Guid InstanceId, Guid HardwareBindingId, long ExpectedRevision, long ExpectedBindingRevision, string Reason) : ICommand<InstanceIdentity>;

public static class HardwareCapabilities
{
    public static bool IsWrite(Type t) => t == typeof(CreateHardwareCommand) || t == typeof(UpdateHardwareCommand) ||
        t == typeof(CreateHardwareBindingCommand) || t == typeof(RevokeHardwareBindingCommand);
    public static bool IsQuery(Type t) => t == typeof(ListHardwareQuery) || t == typeof(GetHardwareQuery) ||
        t == typeof(ListHardwareBindingsQuery) || t == typeof(GetWorkspaceDevicesQuery) || t == typeof(GetWorkspaceInstallationsQuery);
}
