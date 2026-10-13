using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Svm.Services.Contracts.Framework;
using Svm.Services.Contracts.Tasks;
using Svm.Services.Contracts.Instances;
using Svm.Services.Contracts.Packages;

namespace Svm.Dapper;

public static class TaskQueryRegistration
{ public static IServiceCollection AddSvmTaskQueries(this IServiceCollection services) => services.AddScoped<ITaskQueries, TaskQueries>(); }
internal sealed class TaskQueries(ReadQuerySession session, ICallContext calls, PackageExecutionOptions options, IAccessProofSource? proof = null) : ITaskQueries
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private Guid Subject => calls.Current?.Actor.ActorId ?? throw new RequestRejectedException(RequestFailure.AuthenticationRequired);
    private Guid? Instance => calls.Current?.Actor.Kind == ActorKind.Instance ? calls.Current.Actor.InstanceId : null;
    private Guid? Credential => Instance is not null ? proof?.Proof?.Id : null;
    private const string Visible = """
        ((@instance::uuid IS NULL AND EXISTS(SELECT 1 FROM iam.permissions p JOIN iam.users u ON u."Id"=p."SubjectId"
            WHERE p."SubjectId"=@subject AND p."SoftwareId"=d."SoftwareId" AND p."Operation"='instance.read' AND u."IsEnabled" AND NOT u."MustChangePassword"))
         OR (@instance::uuid IS NOT NULL AND EXISTS(SELECT 1 FROM ins.instances i JOIN iam.instance_credentials c ON c."SubjectId"=i."Id"
            WHERE i."Id"=@instance AND i."SoftwareId"=d."SoftwareId" AND i."Lifecycle"='Active' AND c."Id"=@credential AND c."RevokedAt" IS NULL AND (c."ExpiresAt" IS NULL OR c."ExpiresAt">clock_timestamp()))))
        """;
    private const string DeploymentProjection = """
        SELECT d."Id" AS "Cursor", (to_jsonb(d) || jsonb_build_object('Kind','Update',
          'Window',jsonb_build_object('notBefore',d."NotBefore",'latestStart',d."LatestStart"),
          'Parameters',jsonb_build_object('batchSize',d."BatchSize",'failureLimit',d."FailureLimit",'resultWaitSeconds',d."ResultWaitSeconds"),
          'ResultCounts',(SELECT jsonb_build_object('succeeded',count(*) FILTER(WHERE t."State"='Succeeded'),'failed',count(*) FILTER(WHERE t."State"='Failed'),
            'canceled',count(*) FILTER(WHERE t."State"='Canceled'),'closedUnknown',count(*) FILTER(WHERE t."State"='ClosedUnknown'),
            'unfinished',count(*) FILTER(WHERE t."State" NOT IN ('Succeeded','Failed','Canceled','ClosedUnknown'))) FROM tsk.tasks t WHERE t."DeploymentId"=d."Id"),
          'PauseReasons',COALESCE((SELECT jsonb_agg(jsonb_build_object('code',code,'scope',CASE WHEN code IN ('FailureLimit','ReceiptTimeout') THEN 'FutureBatches' ELSE 'NotStarted' END))
            FROM unnest(string_to_array(d."PauseCodes",',')) code WHERE code<>''),'[]'::jsonb)))::text AS "Value"
        FROM tsk.deployments d
        """;
    private const string WaitReason = """
        CASE WHEN t."State" IN ('Succeeded','Failed','Canceled','ClosedUnknown') THEN NULL
          WHEN t."IsDeferred" OR d."ControlPending" THEN 'Paused'
          WHEN b."OpenedAt" IS NOT NULL AND COALESCE(t."ResponseDeadlineAt",greatest(b."OpenedAt",t."NotBefore")+d."ResultWaitSeconds"*interval '1 second') < clock_timestamp() THEN 'ReceiptTimeout'
          WHEN a."StartAuthorizedAt" IS NULL AND clock_timestamp() >= t."LatestStart" THEN 'WindowMissed'
          WHEN NOT EXISTS(SELECT 1 FROM rel.releases r JOIN pkg.packages p ON p."Id"=r."PackageId"
            WHERE r."Id"=t."TargetReleaseId" AND r."State"='Formal' AND p."State"='Ready' AND NOT p."Disabled"
              AND EXISTS(SELECT 1 FROM pkg.replicas cp WHERE cp."PackageId"=p."Id" AND cp."State"='Healthy' AND cp."CheckedAt">clock_timestamp()-make_interval(secs=>@freshSeconds))) THEN 'NoHealthyPackage'
          WHEN d."State" IN ('Canceled','Completed','Rejected') OR EXISTS(SELECT 1 FROM unnest(string_to_array(d."PauseCodes",',')) p WHERE p NOT IN ('FailureLimit','ReceiptTimeout')) THEN 'Paused'
        END
        """;
    private const string TaskProjection = $$"""
        SELECT t."Id" AS "Cursor", (to_jsonb(t) || jsonb_build_object(
          'State',CASE WHEN t."State"='Queued' AND b."State"='Open' THEN 'Available' ELSE t."State" END,
          'Window',jsonb_build_object('notBefore',t."NotBefore",'latestStart',t."LatestStart"),
          'ResponseDeadlineAt',COALESCE(t."ResponseDeadlineAt",CASE WHEN b."OpenedAt" IS NOT NULL THEN greatest(b."OpenedAt",t."NotBefore")+d."ResultWaitSeconds"*interval '1 second' END),
          'WaitReason',{{WaitReason}},
          'StartAuthorizedAt',a."StartAuthorizedAt",'MustBeginBefore',a."MustBeginBefore",
          'ManualClosure',CASE WHEN t."ClosedAt" IS NOT NULL THEN jsonb_build_object('subjectId',t."ClosedBy",'reason',t."ClosureReason",'onsiteEvidence',t."OnsiteEvidence",'closedAt',t."ClosedAt") END))::text AS "Value"
        FROM tsk.tasks t JOIN tsk.deployments d ON d."Id"=t."DeploymentId" LEFT JOIN tsk.batches b ON b."Id"=t."BatchId" LEFT JOIN tsk.attempts a ON a."Id"=t."AttemptId"
        """;
    private async Task<T> Single<T>(string sql, object parameters, CancellationToken ct)
    { var rows = await session.QueryAsync<Row>(sql,parameters,ct); return rows.Count == 1 ? JsonSerializer.Deserialize<T>(rows[0].Value,Json)! : throw new RequestRejectedException(RequestFailure.ResourceNotFound); }
    public Task<SelectionView> SelectionAsync(Guid id, CancellationToken ct) => Single<SelectionView>("""
        SELECT s."Id" AS "Cursor",to_jsonb(s)::text AS "Value" FROM tsk.target_selections s
        WHERE s."Id"=@id AND s."OwnerId"=@subject AND EXISTS(SELECT 1 FROM iam.permissions p JOIN iam.users u ON u."Id"=p."SubjectId"
          WHERE p."SubjectId"=@subject AND p."SoftwareId"=s."SoftwareId" AND p."Operation"='deployment.create' AND u."IsEnabled" AND NOT u."MustChangePassword")
        """,new { id,subject=Subject },ct);
    public Task<DeploymentView> DeploymentAsync(Guid id, CancellationToken ct) => Single<DeploymentView>(DeploymentProjection+" WHERE d.\"Id\"=@id AND "+Visible,new {id,subject=Subject,instance=Instance,credential=Credential},ct);
    public Task<TaskView> TaskAsync(Guid id, CancellationToken ct) => Single<TaskView>(TaskProjection+" WHERE t.\"Id\"=@id AND "+Visible+" AND (@instance::uuid IS NULL OR t.\"InstanceId\"=@instance)",new {id,subject=Subject,instance=Instance,credential=Credential,freshSeconds=options.ReplicaCheckSeconds*2},ct);
    public Task<TaskWorkView> WorkAsync(Guid id, CancellationToken ct) => Single<TaskWorkView>("""
        SELECT w."Id" AS "Cursor",to_jsonb(w)::text AS "Value" FROM tsk.works w JOIN iam.permissions p ON p."SoftwareId"=w."SoftwareId"
        JOIN iam.users u ON u."Id"=p."SubjectId" WHERE w."Id"=@id AND p."SubjectId"=@subject AND p."Operation"='instance.read' AND u."IsEnabled" AND NOT u."MustChangePassword"
        """,new {id,subject=Subject},ct);
    public async Task<TaskPage<BatchView>> BatchesAsync(Guid deployment, int take, Guid? after, CancellationToken ct) => Page<BatchView>(await session.QueryAsync<Row>("""
        SELECT b."Id" AS "Cursor",(to_jsonb(b)||jsonb_build_object('deferredCount',c."Deferred",'failedCount',c."Failed",'timedOutCount',c."TimedOut",'earliestResponseDeadlineAt',c."Deadline"))::text AS "Value"
        FROM tsk.batches b JOIN tsk.deployments d ON d."Id"=b."DeploymentId"
        CROSS JOIN LATERAL (SELECT count(*) FILTER(WHERE t."IsDeferred" AND t."State" NOT IN ('Succeeded','Failed','Canceled','ClosedUnknown')) AS "Deferred",
          count(*) FILTER(WHERE t."State"='Failed') AS "Failed",
          count(*) FILTER(WHERE t."State" NOT IN ('Succeeded','Failed','Canceled','ClosedUnknown') AND NOT t."IsDeferred" AND b."OpenedAt" IS NOT NULL AND
            COALESCE(t."ResponseDeadlineAt",greatest(b."OpenedAt",t."NotBefore")+d."ResultWaitSeconds"*interval '1 second') < clock_timestamp()) AS "TimedOut",
          min(COALESCE(t."ResponseDeadlineAt",greatest(b."OpenedAt",t."NotBefore")+d."ResultWaitSeconds"*interval '1 second')) FILTER
            (WHERE t."State" NOT IN ('Succeeded','Failed','Canceled','ClosedUnknown') AND NOT t."IsDeferred" AND b."OpenedAt" IS NOT NULL) AS "Deadline"
          FROM tsk.tasks t WHERE t."BatchId"=b."Id") c
        WHERE d."Id"=@deployment AND
        """+Visible+" AND (@after::uuid IS NULL OR b.\"Ordinal\">(SELECT \"Ordinal\" FROM tsk.batches WHERE \"Id\"=@after AND \"DeploymentId\"=@deployment)) ORDER BY b.\"Ordinal\" LIMIT @take",new {deployment,take=take+1,after,subject=Subject,instance=Instance,credential=Credential},ct),take);
    public async Task<IReadOnlyList<IntegrationMaterialView>> MaterialsAsync(Guid release,int take,Guid? after,CancellationToken ct) =>
        (await session.QueryAsync<Row>("""
        SELECT m."Id" AS "Cursor",to_jsonb(m)::text AS "Value" FROM rel.integration_materials m JOIN rel.releases r ON r."Id"=m."ReleaseId"
        WHERE r."Id"=@release AND EXISTS(SELECT 1 FROM iam.permissions p JOIN iam.users u ON u."Id"=p."SubjectId" WHERE p."SubjectId"=@subject
          AND p."SoftwareId"=r."SoftwareId" AND p."Operation"='software.read' AND u."IsEnabled" AND NOT u."MustChangePassword")
          AND (@after::uuid IS NULL OR m."Revision"<(SELECT "Revision" FROM rel.integration_materials WHERE "Id"=@after AND "ReleaseId"=@release))
        ORDER BY m."Revision" DESC LIMIT @take
        """,new {release,take,after,subject=Subject},ct)).Select(x=>JsonSerializer.Deserialize<IntegrationMaterialView>(x.Value,Json)!).ToArray();
    public async Task<TaskPage<DeploymentView>> DeploymentsAsync(TaskListInput x, CancellationToken ct) => Page<DeploymentView>(await session.QueryAsync<Row>(DeploymentProjection + " WHERE d.\"SoftwareId\"=@software AND " + Visible + " AND (@after::uuid IS NULL OR d.\"Id\">@after) AND (@state::text IS NULL OR d.\"State\"=@state) ORDER BY d.\"Id\" LIMIT @take", new { software = x.SoftwareId, subject = Subject, instance = Instance, credential = Credential, after = x.After, state = x.State, take = x.PageSize + 1 }, ct), x.PageSize);
    public async Task<TaskPage<TaskView>> TasksAsync(TaskListInput x, Guid? instanceId, CancellationToken ct)
    {
        if (instanceId != Instance) throw new RequestRejectedException(RequestFailure.ResourceNotFound);
        return Page<TaskView>(await session.QueryAsync<Row>(TaskProjection + " WHERE d.\"SoftwareId\"=@software AND " + Visible + " AND (@deployment::uuid IS NULL OR d.\"Id\"=@deployment) AND (@batch::uuid IS NULL OR t.\"BatchId\"=@batch) AND (@deferred::boolean IS NULL OR t.\"IsDeferred\"=@deferred) AND (@waitReason::text IS NULL OR " + WaitReason + "=@waitReason) AND (@instance::uuid IS NULL OR t.\"InstanceId\"=@instance AND t.\"State\" NOT IN ('Succeeded','Failed','Canceled','ClosedUnknown')) AND (@after::uuid IS NULL OR t.\"Id\">@after) AND (@state::text IS NULL OR t.\"State\"=@state OR @state='Available' AND t.\"State\"='Queued' AND b.\"State\"='Open') ORDER BY t.\"Id\" LIMIT @take", new { software = x.SoftwareId, subject = Subject, instance = Instance, credential = Credential, deployment = x.DeploymentId, batch=x.BatchId, deferred=x.IsDeferred, waitReason=x.WaitReason, freshSeconds=options.ReplicaCheckSeconds*2, after = x.After, state = x.State, take = x.PageSize + 1 }, ct), x.PageSize);
    }
    public async Task<TaskPage<AdmissionView>> AdmissionsAsync(Guid deploymentId, int take, Guid? after, string? decision, string? reasonCode, CancellationToken ct) => Page<AdmissionView>(await session.QueryAsync<Row>("SELECT a.\"InstanceId\" AS \"Cursor\",to_jsonb(a)::text AS \"Value\" FROM tsk.admission_items a JOIN tsk.deployments d ON d.\"Id\"=a.\"DeploymentId\" WHERE d.\"Id\"=@deployment AND " + Visible + " AND (@decision::text IS NULL OR a.\"Decision\"=@decision) AND (@reasonCode::text IS NULL OR a.\"ReasonCode\"=@reasonCode) AND (@after::uuid IS NULL OR a.\"InstanceId\">@after) ORDER BY a.\"InstanceId\" LIMIT @take", new { deployment = deploymentId, subject = Subject, instance = Instance, credential = Credential, decision, reasonCode, after, take = take + 1 }, ct), take);
    public async Task<TaskPage<ReceiptView>> ReceiptsAsync(Guid taskId, int take, Guid? after, CancellationToken ct) => Page<ReceiptView>(await session.QueryAsync<Row>("SELECT r.\"Id\" AS \"Cursor\", (to_jsonb(r)-'Digest'-'TaskState'-'StateReportApplied')::text AS \"Value\" FROM tsk.receipts r JOIN tsk.attempts a ON a.\"Id\"=r.\"AttemptId\" JOIN tsk.tasks t ON t.\"Id\"=a.\"TaskId\" JOIN tsk.deployments d ON d.\"Id\"=t.\"DeploymentId\" WHERE t.\"Id\"=@task AND " + Visible + " AND (@after::uuid IS NULL OR r.\"Id\">@after) ORDER BY r.\"Id\" LIMIT @take", new { task = taskId, subject = Subject, instance = Instance, credential = Credential, after, take = take + 1 }, ct), take);
    public async Task<TaskPage<ControlItemView>> ControlItemsAsync(Guid workId, int take, Guid? after, CancellationToken ct) => Page<ControlItemView>(await session.QueryAsync<Row>("SELECT c.\"TaskId\" AS \"Cursor\",to_jsonb(c)::text AS \"Value\" FROM tsk.control_items c JOIN tsk.works w ON w.\"Id\"=c.\"WorkId\" JOIN tsk.deployments d ON d.\"Id\"=w.\"ResourceId\" WHERE w.\"Id\"=@work AND " + Visible + " AND (@after::uuid IS NULL OR c.\"TaskId\">@after) ORDER BY c.\"TaskId\" LIMIT @take", new { work = workId, subject = Subject, instance = Instance, credential = Credential, after, take = take + 1 }, ct), take);
    public async Task<IReadOnlyDictionary<Guid, TaskSummary>> LatestAsync(IReadOnlyList<Guid> ids, CancellationToken ct)
    {
        if (ids.Count > 200 || ids.Any(x => x == Guid.Empty)) throw new RequestRejectedException(RequestFailure.ValidationFailed);
        if (ids.Count == 0) return new Dictionary<Guid, TaskSummary>();
        var rows = await session.QueryAsync<SummaryRow>("SELECT DISTINCT ON (t.\"InstanceId\") t.\"InstanceId\",t.\"Id\" AS \"TaskId\",t.\"TerminalResult\" AS \"Result\" FROM tsk.tasks t JOIN tsk.deployments d ON d.\"Id\"=t.\"DeploymentId\" WHERE t.\"InstanceId\"=ANY(@ids) AND " + Visible + " AND (@instance::uuid IS NULL OR t.\"InstanceId\"=@instance) ORDER BY t.\"InstanceId\",t.\"CreatedAt\" DESC,t.\"Id\" DESC", new { ids = ids.ToArray(), subject = Subject, instance = Instance, credential = Credential }, ct);
        return rows.ToDictionary(x => x.InstanceId, x => new TaskSummary(x.TaskId, x.Result));
    }
    private static TaskPage<T> Page<T>(IReadOnlyList<Row> rows, int take) => new(rows.Take(take).Select(x => JsonSerializer.Deserialize<T>(x.Value, Json) ?? throw new RequestRejectedException(RequestFailure.DependencyUnavailable)).ToArray(), rows.Count > take ? rows[take - 1].Cursor : null);
    private sealed class Row { public Guid Cursor { get; set; } public string Value { get; set; } = ""; }
    private sealed class SummaryRow { public Guid InstanceId { get; set; } public Guid TaskId { get; set; } public string? Result { get; set; } }
}
