using Svm.Services.Contracts.Framework;

namespace Svm.Services.Contracts.Catalog;

[RequestPolicy("ins.site.get", ModuleOwner.Instances, RequestKind.Manage, RequestScope.Global,
    TransactionMode.ReadOnly, IdempotencyMode.None, ValidationMode.Required, ActorKind.Human, Permission = "asset.read")]
public sealed record GetSiteQuery : IQuery<SiteView>;

[RequestPolicy("ins.processes.list", ModuleOwner.Instances, RequestKind.Manage, RequestScope.Global,
    TransactionMode.ReadOnly, IdempotencyMode.None, ValidationMode.Required, ActorKind.Human, Permission = "asset.read")]
public sealed record ListProcessesQuery(CatalogListInput Input) : IQuery<CatalogPage<ProcessView>>;

[RequestPolicy("ins.processes.get", ModuleOwner.Instances, RequestKind.Manage, RequestScope.Global,
    TransactionMode.ReadOnly, IdempotencyMode.None, ValidationMode.Required, ActorKind.Human, Permission = "asset.read")]
public sealed record GetProcessQuery(Guid ProcessId) : IQuery<ProcessView>;

[RequestPolicy("ins.devices.list", ModuleOwner.Instances, RequestKind.Manage, RequestScope.Global,
    TransactionMode.ReadOnly, IdempotencyMode.None, ValidationMode.Required, ActorKind.Human, Permission = "asset.read")]
public sealed record ListDevicesQuery(CatalogListInput Input) : IQuery<CatalogPage<DeviceView>>;

[RequestPolicy("ins.devices.get", ModuleOwner.Instances, RequestKind.Manage, RequestScope.Global,
    TransactionMode.ReadOnly, IdempotencyMode.None, ValidationMode.Required, ActorKind.Human, Permission = "asset.read")]
public sealed record GetDeviceQuery(Guid DeviceId) : IQuery<DeviceView>;

[RequestPolicy("ins.bindings.list", ModuleOwner.Instances, RequestKind.Manage, RequestScope.Global,
    TransactionMode.ReadOnly, IdempotencyMode.None, ValidationMode.Required, ActorKind.Human, Permission = "asset.read")]
public sealed record ListBindingsQuery(Guid DeviceId, CatalogListInput Input) : IQuery<CatalogPage<BindingView>>;

[RequestPolicy("ins.inventory.list", ModuleOwner.Instances, RequestKind.Manage, RequestScope.Global,
    TransactionMode.ReadOnly, IdempotencyMode.None, ValidationMode.Required, ActorKind.Human, Permission = "asset.read")]
public sealed record GetDeviceInventoryQuery(Guid DeviceId, CatalogListInput Input) : IQuery<CatalogPage<DeviceSoftwareInventoryItem>>;

[RequestPolicy("rel.software.list", ModuleOwner.Releases, RequestKind.Manage, RequestScope.Global,
    TransactionMode.ReadOnly, IdempotencyMode.None, ValidationMode.Required, ActorKind.Human, Permission = "software.read")]
public sealed record ListSoftwareQuery(CatalogListInput Input) : IQuery<CatalogPage<SoftwareView>>;

[RequestPolicy("rel.software.get", ModuleOwner.Releases, RequestKind.Manage, RequestScope.Software,
    TransactionMode.ReadOnly, IdempotencyMode.None, ValidationMode.Required, ActorKind.Human, Permission = "software.read")]
public sealed record GetSoftwareQuery(Guid SoftwareId) : IQuery<SoftwareView>;

[RequestPolicy("identity.permission-options.list", ModuleOwner.Identity, RequestKind.Manage, RequestScope.Global,
    TransactionMode.ReadOnly, IdempotencyMode.None, ValidationMode.Required, ActorKind.Human, Permission = "identity.manage")]
public sealed record GetPermissionOptionsQuery(CatalogListInput Input) : IQuery<PermissionOptionsPage>;

[RequestPolicy("rel.software.create", ModuleOwner.Releases, RequestKind.Manage, RequestScope.Global,
    TransactionMode.DatabaseAtomic, IdempotencyMode.OperationResult, ValidationMode.Required, ActorKind.Human, Permission = "software.create")]
public sealed record CreateSoftwareCommand(Guid Key, string Code, string Name, string Category, string? Description) : ICommand<SoftwareView>;

[RequestPolicy("rel.software.update", ModuleOwner.Releases, RequestKind.Manage, RequestScope.Software,
    TransactionMode.DatabaseAtomic, IdempotencyMode.OperationResult, ValidationMode.Required, ActorKind.Human, Permission = "release.upload")]
public sealed record UpdateSoftwareCommand(Guid Key, Guid SoftwareId, long ExpectedRevision, string Name, string? Description, string Reason) : ICommand<SoftwareView>;

[RequestPolicy("ins.processes.create", ModuleOwner.Instances, RequestKind.Manage, RequestScope.Global,
    TransactionMode.DatabaseAtomic, IdempotencyMode.OperationResult, ValidationMode.Required, ActorKind.Human, Permission = "asset.manage")]
public sealed record CreateProcessCommand(Guid Key, string Code, string Name) : ICommand<ProcessView>;

[RequestPolicy("ins.processes.update", ModuleOwner.Instances, RequestKind.Manage, RequestScope.Global,
    TransactionMode.DatabaseAtomic, IdempotencyMode.OperationResult, ValidationMode.Required, ActorKind.Human, Permission = "asset.manage")]
public sealed record UpdateProcessCommand(Guid Key, Guid ProcessId, long ExpectedRevision, string Name, string Reason) : ICommand<ProcessView>;

[RequestPolicy("ins.devices.create", ModuleOwner.Instances, RequestKind.Manage, RequestScope.Global,
    TransactionMode.DatabaseAtomic, IdempotencyMode.OperationResult, ValidationMode.Required, ActorKind.Human, Permission = "asset.manage")]
public sealed record CreateDeviceCommand(Guid Key, Guid ProcessId, string DeviceNo, string Name) : ICommand<DeviceView>;

[RequestPolicy("ins.devices.update", ModuleOwner.Instances, RequestKind.Manage, RequestScope.Global,
    TransactionMode.DatabaseAtomic, IdempotencyMode.OperationResult, ValidationMode.Required, ActorKind.Human, Permission = "asset.manage")]
public sealed record UpdateDeviceCommand(Guid Key, Guid DeviceId, long ExpectedRevision, Guid? ProcessId, string? Name, string Reason) : ICommand<DeviceView>;

[RequestPolicy("ins.bindings.create", ModuleOwner.Instances, RequestKind.Manage, RequestScope.Software,
    TransactionMode.DatabaseAtomic, IdempotencyMode.OperationResult, ValidationMode.Required, ActorKind.Human, Permission = "instance.manage")]
public sealed record CreateBindingCommand(Guid Key, Guid DeviceId, Guid SoftwareId, string Reason) : ICommand<BindingView>;

[RequestPolicy("ins.bindings.revoke", ModuleOwner.Instances, RequestKind.Manage, RequestScope.Software,
    TransactionMode.DatabaseAtomic, IdempotencyMode.OperationResult, ValidationMode.Required, ActorKind.Human, Permission = "instance.manage")]
public sealed record RevokeBindingCommand(Guid Key, Guid DeviceId, Guid SoftwareId, long ExpectedRevision, string Reason) : ICommand<BindingView>;
