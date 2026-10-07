using Microsoft.EntityFrameworkCore;
using Svm.Core.Identity;
using Svm.EntityFrameworkCore.Framework;
using Svm.Services.Contracts.Framework;

namespace Svm.EntityFrameworkCore.Identity;

internal sealed class PersonnelAdministrationRepository(SvmDbContext context, IUnitOfWork unitOfWork) : IPersonnelAdministrationRepository
{
    // Distinct from migration/seed and operation-result locks. Acquire before any subject guard.
    internal const long AdministrationLock = 0x53564d49414dL;
    private void RequireTransaction()
    {
        if (context.Database.CurrentTransaction is null || unitOfWork.CurrentOperationId is null)
            throw new PersistenceException(PersistenceFailure.InvalidTransactionNesting);
    }
    public async Task ProtectAsync(Guid actorId, Guid? targetId, CancellationToken token)
    {
        RequireTransaction();
        await context.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({AdministrationLock})", token);
        foreach (var id in new[] { actorId, targetId ?? actorId }.Distinct().Order())
        {
            var rows = await context.Set<SubjectGuard>().FromSqlInterpolated(
                $"SELECT * FROM iam.subject_guards WHERE \"SubjectId\"={id} FOR UPDATE").AsNoTracking().ToListAsync(token);
            if (id == actorId && rows.Count != 1) throw new RequestRejectedException(RequestFailure.AuthenticationRequired);
            // Target existence is checked after the operator's current authorization, without leaking it here.
        }
    }
    public async Task<long> RevisionAsync(Guid subjectId, CancellationToken token) =>
        await context.Set<SubjectGuard>().Where(x => x.SubjectId == subjectId).Select(x => x.Revision).SingleAsync(token);
    public async Task<bool> HasOtherEnabledAdministratorAsync(Guid subjectId, CancellationToken token)
    {
        RequireTransaction();
        return await context.Database.SqlQuery<bool>($"SELECT EXISTS(SELECT 1 FROM iam.users u JOIN iam.permissions p ON p.\"SubjectId\"=u.\"Id\" WHERE u.\"Id\"<>{subjectId} AND u.\"IsEnabled\" AND p.\"SoftwareId\" IS NULL AND p.\"Operation\"='identity.manage') AS \"Value\"").SingleAsync(token);
    }
    public void AddUser(UserAccount user)
    {
        RequireTransaction(); context.Add(user);
        context.Add(new SubjectGuard { SubjectId = user.Id.Value, Revision = 1 });
    }
    public async Task ReplaceGlobalPermissionsAsync(Guid subjectId, IReadOnlyList<string> operations, CancellationToken token)
    {
        RequireTransaction();
        await context.Set<PermissionGrant>().Where(x => x.SubjectId == subjectId && x.SoftwareId == null).ExecuteDeleteAsync(token);
        foreach (var operation in operations)
            context.Add(new PermissionGrant { Id = Guid.NewGuid(), SubjectId = subjectId, Operation = operation });
    }
}
