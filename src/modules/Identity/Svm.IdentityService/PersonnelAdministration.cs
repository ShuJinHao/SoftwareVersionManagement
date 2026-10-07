using Microsoft.Extensions.DependencyInjection;
using Svm.Core.Identity;
using Svm.Services.Contracts.Framework;
using Svm.Services.Contracts.Identity;
using Svm.SharedKernel.Domain;
using Svm.Services.Contracts.Catalog;

namespace Svm.IdentityService;

public static class PersonnelAdministrationRegistration
{
    public static IServiceCollection AddSvmPersonnelAdministration(this IServiceCollection services) =>
        services.AddScoped<IPersonnelAdministration, PersonnelAdministration>();
    public static IServiceCollection AddSvmPersonnelSoftwareAdministration(this IServiceCollection services) =>
        services.AddScoped<IPersonnelSoftwareAdministration, PersonnelAdministration>();
}

internal sealed class PersonnelAdministration(IPersonnelRepository personnel, IPersonnelAdministrationRepository administration,
    IPersonnelCrypto crypto, IUnitOfWork unitOfWork, IOperationContext operations) : IPersonnelAdministration, IPersonnelSoftwareAdministration
{
    public Task ProtectAsync(Guid actorId, Guid? targetId, CancellationToken token) => administration.ProtectAsync(actorId, targetId, token);
    public async Task<UserView> CreateAsync(string employeeNo, string displayName, string temporaryPassword, CancellationToken token)
    {
        RequireOperation();
        if (await personnel.FindAsync(employeeNo, token) is not null) throw new RequestRejectedException(RequestFailure.InvalidState);
        var user = new UserAccount(new StrongId<UserAccount>(Guid.NewGuid()), employeeNo, displayName, crypto.HashPassword(temporaryPassword));
        administration.AddUser(user);
        return new(user.Id.Value, user.EmployeeNo, user.DisplayName, user.IsEnabled, user.MustChangePassword, [], 1);
    }
    public async Task<UserView> UpdateAsync(Guid userId, long expectedRevision, string? displayName, bool? isEnabled, CancellationToken token)
    {
        var user = await ExistingAsync(userId, expectedRevision, token);
        if (user.IsEnabled && isEnabled == false && (await personnel.PermissionsAsync(userId, token)).Any(IsAdministrator))
            await RequireAnotherAdministrator(userId, token);
        user.Update(displayName, isEnabled);
        if (isEnabled == false) await personnel.RevokeSessionsAsync(userId, await personnel.NowAsync(token), token);
        await personnel.AdvanceRevisionAsync(userId, token);
        return await ViewAsync(user, token);
    }
    public async Task<UserView> ResetPasswordAsync(Guid userId, long expectedRevision, string temporaryPassword, CancellationToken token)
    {
        var user = await ExistingAsync(userId, expectedRevision, token);
        user.ResetPassword(crypto.HashPassword(temporaryPassword));
        await personnel.RevokeSessionsAsync(userId, await personnel.NowAsync(token), token);
        await personnel.AdvanceRevisionAsync(userId, token);
        return await ViewAsync(user, token);
    }
    public async Task<UserView> ReplacePermissionsAsync(Guid userId, long expectedRevision, IReadOnlyList<PermissionView> permissions, CancellationToken token)
    {
        var user = await ExistingAsync(userId, expectedRevision, token);
        var current = await personnel.PermissionsAsync(userId, token);
        var retained = current.Where(p => p.SoftwareId is not null).Select(p => new PermissionView(p.SoftwareId, p.Operation)).ToHashSet();
        if (!retained.SetEquals(permissions.Where(p => p.SoftwareId is not null)) ||
            permissions.Where(p => p.SoftwareId is null).Any(p => !PersonnelManagementCapabilities.GlobalPermissions.Contains(p.Operation)))
            throw new RequestRejectedException(RequestFailure.ValidationFailed);
        if (user.IsEnabled && current.Any(IsAdministrator) && !permissions.Any(p => p.SoftwareId is null && p.Operation == "identity.manage"))
            await RequireAnotherAdministrator(userId, token);
        await administration.ReplaceGlobalPermissionsAsync(userId, permissions.Where(p => p.SoftwareId is null).Select(p => p.Operation).ToArray(), token);
        await personnel.AdvanceRevisionAsync(userId, token);
        return new(user.Id.Value, user.EmployeeNo, user.DisplayName, user.IsEnabled, user.MustChangePassword,
            permissions.OrderBy(p => p.Operation, StringComparer.Ordinal).ThenBy(p => p.SoftwareId).ToArray(), await administration.RevisionAsync(userId, token));
    }
    public async Task<UserView> ReplaceAsync(Guid userId, long revision, IReadOnlyList<PermissionView> permissions, CancellationToken token)
    {
        RequireOperation();
        if (operations.Current is not { Owner: ModuleOwner.Identity, Operation: "identity.users.permissions" })
            throw new RequestRejectedException(RequestFailure.PermissionDenied);
        var user = await ExistingAsync(userId, revision, token);
        if (permissions.Any(p => !PermissionCatalog.Entries.Any(d => d.Operation == p.Operation && d.Global == (p.SoftwareId is null))))
            throw new RequestRejectedException(RequestFailure.ValidationFailed);
        var current = await personnel.PermissionsAsync(userId, token);
        if (user.IsEnabled && current.Any(IsAdministrator) && !permissions.Any(p => p.SoftwareId is null && p.Operation == "identity.manage"))
            await RequireAnotherAdministrator(userId, token);
        await administration.ReplaceAllPermissionsAsync(userId, permissions.Select(p => new PersonnelPermission(p.SoftwareId, p.Operation)).ToArray(), token);
        await personnel.AdvanceRevisionAsync(userId, token);
        return new(user.Id.Value, user.EmployeeNo, user.DisplayName, user.IsEnabled, user.MustChangePassword,
            permissions.OrderBy(p => p.Operation, StringComparer.Ordinal).ThenBy(p => p.SoftwareId).ToArray(), await administration.RevisionAsync(userId, token));
    }
    public async Task GrantCreatorAsync(Guid softwareId, CancellationToken token)
    {
        RequireOperation();
        if (operations.Current is not { Owner: ModuleOwner.Releases, Operation: "rel.software.create" } operation || softwareId == Guid.Empty)
            throw new RequestRejectedException(RequestFailure.PermissionDenied);
        await administration.AddSoftwarePermissionsAsync(operation.SubjectId, softwareId, ["software.read", "instance.read", "instance.manage"], token);
        await personnel.AdvanceRevisionAsync(operation.SubjectId, token);
    }
    private async Task<UserAccount> ExistingAsync(Guid id, long expectedRevision, CancellationToken token)
    {
        RequireOperation();
        var user = await personnel.GetAsync(id, token) ?? throw new RequestRejectedException(RequestFailure.ResourceNotFound);
        if (await administration.RevisionAsync(id, token) != expectedRevision) throw new RequestRejectedException(RequestFailure.RevisionConflict);
        return user;
    }
    private async Task<UserView> ViewAsync(UserAccount user, CancellationToken token) =>
        new(user.Id.Value, user.EmployeeNo, user.DisplayName, user.IsEnabled, user.MustChangePassword,
            (await personnel.PermissionsAsync(user.Id.Value, token)).Select(p => new PermissionView(p.SoftwareId, p.Operation)).ToArray(),
            await administration.RevisionAsync(user.Id.Value, token));
    private async Task RequireAnotherAdministrator(Guid id, CancellationToken token)
    {
        if (!await administration.HasOtherEnabledAdministratorAsync(id, token)) throw new RequestRejectedException(RequestFailure.InvalidState);
    }
    private static bool IsAdministrator(PersonnelPermission p) => p.SoftwareId is null && p.Operation == "identity.manage";
    private void RequireOperation()
    {
        if (unitOfWork.CurrentOperationId is null) throw new PersistenceException(PersistenceFailure.InvalidTransactionNesting);
    }
}
