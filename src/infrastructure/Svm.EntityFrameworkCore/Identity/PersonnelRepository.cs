using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Svm.Core.Identity;
using Svm.EntityFrameworkCore.Framework;
using Svm.EntityFrameworkCore.Migrations;
using Svm.Services.Contracts.Framework;
using Svm.SharedKernel.Domain;

namespace Svm.EntityFrameworkCore.Identity;

internal sealed class PersonnelRepository(SvmDbContext context) : IPersonnelRepository
{
    private void RequireTransaction()
    {
        if (context.Database.CurrentTransaction is null) throw new PersistenceException(PersistenceFailure.InvalidTransactionNesting);
    }
    private static async Task<T> Safe<T>(Func<Task<T>> action)
    {
        try { return await action(); }
        catch (Exception e) when (e is DbException or DbUpdateException or IOException)
        { throw new PersistenceException(PersistenceFailure.DependencyUnavailable, sqlState: (e as PostgresException)?.SqlState); }
    }
    public Task<DateTimeOffset> NowAsync(CancellationToken token) => Safe(async () =>
        await context.Database.SqlQueryRaw<DateTimeOffset>("SELECT clock_timestamp() AS \"Value\"").SingleAsync(token));
    public Task<UserAccount?> FindAsync(string employeeNo, CancellationToken token) => Safe(() =>
        context.Set<UserAccount>().AsNoTracking().SingleOrDefaultAsync(x => x.EmployeeNo == employeeNo, token));
    public Task<UserAccount?> GetAsync(Guid subjectId, CancellationToken token) => Safe(async () =>
    {
        var user = await context.Set<UserAccount>().FindAsync([new StrongId<UserAccount>(subjectId)], token);
        if (user is not null) await context.Entry(user).ReloadAsync(token);
        return user;
    });
    public Task<WebSession?> SessionAsync(Guid sessionId, CancellationToken token) => Safe(async () =>
    {
        var session = await context.Set<WebSession>().FindAsync([sessionId], token);
        if (session is not null) await context.Entry(session).ReloadAsync(token);
        return session;
    });
    public async Task LockSubjectAsync(Guid subjectId, bool exclusive, CancellationToken token)
    {
        RequireTransaction();
        var sql = "SELECT * FROM iam.subject_guards WHERE \"SubjectId\" = {0} FOR " + (exclusive ? "UPDATE" : "SHARE");
        var rows = await Safe(() => context.Set<SubjectGuard>().FromSqlRaw(sql, subjectId).AsNoTracking().ToListAsync(token));
        if (rows.Count != 1) throw new RequestRejectedException(RequestFailure.AuthenticationRequired);
    }
    public async Task AdvanceRevisionAsync(Guid subjectId, CancellationToken token)
    {
        RequireTransaction();
        await context.Database.ExecuteSqlInterpolatedAsync($"UPDATE iam.subject_guards SET \"Revision\" = \"Revision\" + 1 WHERE \"SubjectId\" = {subjectId}", token);
    }
    public Task<IReadOnlyList<PersonnelPermission>> PermissionsAsync(Guid subjectId, CancellationToken token) => Safe<IReadOnlyList<PersonnelPermission>>(async () =>
        await context.Set<PermissionGrant>().AsNoTracking().Where(x => x.SubjectId == subjectId).OrderBy(x => x.Operation).ThenBy(x => x.SoftwareId)
            .Select(x => new PersonnelPermission(x.SoftwareId, x.Operation)).ToListAsync(token));
    public void AddSession(WebSession session) { RequireTransaction(); context.Add(session); }
    public async Task RevokeSessionsAsync(Guid subjectId, DateTimeOffset now, CancellationToken token)
    {
        RequireTransaction();
        await context.Set<WebSession>().Where(x => x.SubjectId == subjectId && x.RevokedAt == null)
            .ExecuteUpdateAsync(set => set.SetProperty(x => x.RevokedAt, now), token);
    }
    public async Task<int> LockRateLimitAsync(string accountKey, string addressKey, int accountLimit, int accountWindowSeconds,
        int addressLimit, int addressWindowSeconds, CancellationToken token)
    {
        RequireTransaction();
        var now = await NowAsync(token);
        var retry = 0;
        foreach (var (key, limit, seconds) in new[] { (accountKey, accountLimit, accountWindowSeconds), (addressKey, addressLimit, addressWindowSeconds) })
        {
            await context.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO iam.login_limits (\"Key\", \"Failures\", \"WindowEnd\") VALUES ({key}, 0, {now.AddSeconds(seconds)}) ON CONFLICT (\"Key\") DO NOTHING", token);
            var row = (await context.Set<LoginLimit>().FromSqlInterpolated($"SELECT * FROM iam.login_limits WHERE \"Key\" = {key} FOR UPDATE").AsNoTracking().ToListAsync(token)).Single();
            // Re-evaluate database time after waiting for the row lock.
            now = await NowAsync(token);
            if (row.WindowEnd <= now || row.Failures == 0)
                await context.Database.ExecuteSqlInterpolatedAsync($"UPDATE iam.login_limits SET \"Failures\" = 0, \"WindowEnd\" = {now.AddSeconds(seconds)} WHERE \"Key\" = {key}", token);
            else if (row.Failures >= limit) retry = Math.Max(retry, (int)Math.Ceiling((row.WindowEnd - now).TotalSeconds));
        }
        return retry;
    }
    public async Task RecordFailureAsync(string accountKey, string addressKey, CancellationToken token)
    {
        RequireTransaction();
        await context.Database.ExecuteSqlInterpolatedAsync($"UPDATE iam.login_limits SET \"Failures\" = \"Failures\" + 1 WHERE \"Key\" IN ({accountKey}, {addressKey})", token);
    }
    public async Task<bool> BeginSeedAsync(CancellationToken token)
    {
        RequireTransaction();
        await context.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({MigrationRunner.LockKey})", token);
        if (await context.Set<SeedMarker>().AnyAsync(x => x.Name == "personnel-v1", token)) return false;
        context.Add(new SeedMarker { Name = "personnel-v1", CompletedAt = await NowAsync(token) });
        return true;
    }
    public void AddInitialAccount(UserAccount user, IReadOnlyList<string> globalPermissions)
    {
        RequireTransaction(); context.Add(user); context.Add(new SubjectGuard { SubjectId = user.Id.Value, Revision = 1 });
        foreach (var permission in globalPermissions)
            context.Add(new PermissionGrant { Id = Guid.NewGuid(), SubjectId = user.Id.Value, Operation = permission });
    }
    public async Task EnsurePermissionCatalogAsync(IReadOnlyList<(string Operation, bool Global)> permissions, CancellationToken token)
    {
        RequireTransaction();
        foreach (var (operation, global) in permissions)
            await context.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO iam.permission_catalog (\"Operation\", \"Global\") VALUES ({operation}, {global}) ON CONFLICT (\"Operation\") DO NOTHING", token);
    }
}
