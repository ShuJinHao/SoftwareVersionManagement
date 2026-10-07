using Microsoft.Extensions.DependencyInjection;
using Svm.Core.Identity;
using Svm.Services.Contracts.Framework;
using Svm.Services.Contracts.Identity;
using Svm.SharedKernel.Domain;

namespace Svm.IdentityService;

public static class PersonnelAdministrationRegistration
{
    public static IServiceCollection AddSvmPersonnelAdministration(this IServiceCollection services) =>
        services.AddScoped<IPersonnelAdministration, PersonnelAdministration>();
}

internal sealed class PersonnelAdministration(IPersonnelRepository personnel, IPersonnelAdministrationRepository administration,
    IPersonnelCrypto crypto, IUnitOfWork unitOfWork) : IPersonnelAdministration
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
