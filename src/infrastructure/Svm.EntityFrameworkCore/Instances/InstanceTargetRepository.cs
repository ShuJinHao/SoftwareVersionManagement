using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Svm.Core.Instances;
using Svm.EntityFrameworkCore.Framework;
using Svm.Services.Contracts.Framework;
using Svm.Services.Contracts.Tasks;

namespace Svm.EntityFrameworkCore.Instances;

internal sealed class InstanceTargetRepository(SvmDbContext db, IUnitOfWork unit, ITargetSnapshotContext snapshot) : IInstanceTargetRepository
{
    public async Task<IReadOnlyList<Guid>> SnapshotPageAsync(InstanceTargetFilter f, Guid? after, int take, CancellationToken ct)
    {
        if (!snapshot.IsMaterializing || unit.CurrentOperationId is null || db.Database.CurrentTransaction?.GetDbTransaction().IsolationLevel != System.Data.IsolationLevel.RepeatableRead || take is < 1 or > 1000)
            throw new PersistenceException(PersistenceFailure.InvalidTransactionNesting);
        return await db.Database.SqlQueryRaw<Guid>("""
            SELECT i."Id" AS "Value" FROM ins.instances i JOIN ins.devices d ON d."Id"=i."DeviceId"
            JOIN ins.instance_snapshots s ON s."InstanceId"=i."Id"
            WHERE i."SoftwareId"={0} AND ({1}::uuid IS NULL OR i."Id">{1})
              AND ({2}::uuid IS NULL OR d."ProcessId"={2}) AND ({3}::uuid IS NULL OR i."DeviceId"={3})
              AND ({4}::text IS NULL OR d."DeviceNo" LIKE {4} ESCAPE '\')
              AND ({5}::text IS NULL OR s."SnapshotJson"->'ReportedIps' ? {5}::text)
              AND ({6}::uuid IS NULL OR s."SnapshotJson"->>'InstalledReleaseId'={6}::text)
              AND ({7}::text IS NULL OR CASE WHEN s."LastAcceptedAt" IS NULL THEN 'NeverReported' WHEN s."LastAcceptedAt"<transaction_timestamp()-interval '5 minutes' THEN 'Unknown' ELSE 'Fresh' END={7})
              AND ({8}::text IS NULL OR s."SnapshotJson"->>'RunningState'={8}) AND ({9}::text IS NULL OR i."Lifecycle"={9})
            ORDER BY i."Id" LIMIT {10}
            """, f.SoftwareId, (object?)after ?? DBNull.Value, (object?)f.ProcessId ?? DBNull.Value, (object?)f.DeviceId ?? DBNull.Value,
            f.DeviceNo is null ? DBNull.Value : f.DeviceNo.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%",
            (object?)f.ReportedIp ?? DBNull.Value, (object?)f.InstalledReleaseId ?? DBNull.Value, (object?)f.Freshness ?? DBNull.Value,
            (object?)f.RunningState ?? DBNull.Value, (object?)f.Lifecycle ?? DBNull.Value, take).ToListAsync(ct);
    }
}
