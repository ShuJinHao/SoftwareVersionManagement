using Svm.SharedKernel.Domain;

namespace Svm.Core.Identity;

public sealed class AccessRole : AggregateRoot<StrongId<AccessRole>>
{
    public AccessRole(StrongId<AccessRole> id, string name) : base(id) { Rename(name); }
    public string Name { get; private set; } = "";
    public bool IsEnabled { get; private set; } = true;
    public long Revision { get; private set; } = 1;
    public void Update(string name, bool enabled) { Rename(name); IsEnabled = enabled; Revision++; }
    private void Rename(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (name.Length > 128) throw new ArgumentOutOfRangeException(nameof(name));
        Name = name;
    }
}

public sealed record RoleScope(string Kind, Guid? ResourceId);
public sealed record AssignedRole(Guid RoleId, string Name, bool IsEnabled, long Revision,
    IReadOnlyList<string> Operations, IReadOnlyList<RoleScope> Scopes);
public sealed record RoleDefinition(AccessRole Role, IReadOnlyList<string> Operations, int AssignedPeople);

/// <summary>IAM owns roles and assignments; references to site resources are validated through their owning module.</summary>
public interface IRoleRepository
{
    Task<IReadOnlyList<AssignedRole>> AssignmentsAsync(Guid subjectId, CancellationToken token);
    Task<RoleDefinition?> GetAsync(Guid roleId, CancellationToken token);
    Task<bool> NameExistsAsync(string name, Guid? excludedRoleId, CancellationToken token);
    Task<IReadOnlyList<RoleDefinition>> ListAsync(string? name, Guid? afterId, int take, CancellationToken token);
    Task ProtectSiteChangeAsync(CancellationToken token);
    Task AdvanceSiteRevisionAsync(CancellationToken token);
    Task ProtectRoleAsync(Guid actorId, Guid roleId, CancellationToken token);
    Task AddAsync(AccessRole role, IReadOnlyList<string> operations, CancellationToken token);
    Task ReplaceOperationsAsync(Guid roleId, IReadOnlyList<string> operations, CancellationToken token);
    Task ReplaceAssignmentsAsync(Guid subjectId, IReadOnlyList<(Guid RoleId, RoleScope Scope)> assignments, CancellationToken token);
    Task<bool> HasEnabledAdministratorAsync(Guid? excludedSubjectId, Guid? excludedRoleId, CancellationToken token);
    Task<bool> HasSoftwarePermissionAsync(Guid subjectId, Guid softwareId, string operation, CancellationToken token);
    Task<bool> HasInstancePermissionAsync(Guid subjectId, Guid softwareId, string operation, Guid instanceId, CancellationToken token);
    Task<bool> CoversSelectionAsync(Guid subjectId, Guid softwareId, string operation, Guid selectionId, CancellationToken token);
    Task<long> RevisionAsync(Guid subjectId, CancellationToken token);
    Task AdvanceAssignedRevisionsAsync(Guid roleId, CancellationToken token);
}
