using Svm.Services.Contracts.Framework;
using Svm.Services.Contracts.Packages;

namespace Svm.Dapper;

internal sealed class FormalReleaseAvailability(ReadQuerySession session, ICallContext calls, PackageExecutionOptions options)
    : IFormalReleaseAvailability
{
    public async Task<IReadOnlyDictionary<Guid, Guid>> LatestAsync(IReadOnlyList<Guid> softwareIds, CancellationToken token)
    {
        if (softwareIds.Count > 200 || softwareIds.Any(id => id == Guid.Empty))
            throw new RequestRejectedException(RequestFailure.ValidationFailed);
        if (softwareIds.Count == 0) return new Dictionary<Guid, Guid>();
        var actor = calls.Current?.Actor ?? throw new RequestRejectedException(RequestFailure.AuthenticationRequired);
        var rows = await session.QueryAsync<Row>("""
            SELECT DISTINCT ON (r."SoftwareId") r."SoftwareId",r."Id"
            FROM rel.releases r JOIN pkg.packages p ON p."Id"=r."PackageId"
            WHERE r."SoftwareId"=ANY(@softwareIds) AND r."State"='Formal' AND p."State"='Ready' AND NOT p."Disabled"
              AND EXISTS(SELECT 1 FROM pkg.replicas cp WHERE cp."PackageId"=p."Id" AND cp."State"='Healthy'
                  AND cp."CheckedAt">clock_timestamp()-make_interval(secs=>@freshSeconds))
              AND ((@kind='Human' AND EXISTS(SELECT 1 FROM iam.users u JOIN iam.permissions perm ON perm."SubjectId"=u."Id"
                    WHERE u."Id"=@subject AND u."IsEnabled" AND NOT u."MustChangePassword"
                      AND perm."SoftwareId"=r."SoftwareId" AND perm."Operation"='software.read'))
                   OR (@kind='Instance' AND r."SoftwareId"=@actorSoftware))
            ORDER BY r."SoftwareId",r."Major" DESC,r."Minor" DESC,r."Patch" DESC,r."Id" DESC
            """, new { softwareIds = softwareIds.Distinct().ToArray(), subject = actor.ActorId, kind = actor.Kind.ToString(),
                actorSoftware = actor.SoftwareId, freshSeconds = options.ReplicaCheckSeconds * 2 }, token);
        return rows.ToDictionary(row => row.SoftwareId, row => row.Id);
    }
    private sealed class Row
    {
        public Guid SoftwareId { get; set; }
        public Guid Id { get; set; }
    }
}
