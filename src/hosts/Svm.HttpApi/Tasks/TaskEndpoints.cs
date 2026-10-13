using System.Text.Json;
using System.Text.Json.Serialization;
using MediatR;
using Microsoft.AspNetCore.Antiforgery;
using Svm.HttpApi.Personnel;
using Svm.Services.Contracts.Framework;
using Svm.Services.Contracts.Tasks;
using Svm.Services.Contracts.Instances;
using Svm.Services.Contracts.Packages;
using static Svm.HttpApi.Personnel.UserEndpoints;

namespace Svm.HttpApi.Tasks;

internal static class TaskEndpoints
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = false, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    private sealed record SealInput(long ExpectedRevision, int ChunkCount, int ExpectedDistinctCount);
    private sealed record ControlInput(long ExpectedRevision, string Reason, TaskWindow? Window = null, IReadOnlyList<FailureReview>? ReviewedFailures = null, string? OnsiteEvidence = null, bool NoActiveInstallationConfirmed = false);
    private static RouteGroupBuilder Group(WebApplication app, string prefix, RequestKind kind, bool enabled)
    { var g = app.MapGroup(prefix).WithMetadata(new PersonnelEndpointKind(kind)); g.AddEndpointFilter(async (c, next) => {
        if (!c.HttpContext.Request.IsHttps) throw new RequestRejectedException(RequestFailure.PermissionDenied);
        if (!enabled) throw new RequestRejectedException(RequestFailure.ConfigurationInvalid); return await next(c); }); return g; }
    internal static void MapTasks(this WebApplication app, bool enabled)
    {
        var g = Group(app, "/api/v1/manage", RequestKind.Manage, enabled);
        g.MapGet("/software/{softwareId:guid}/deployment-capabilities", async (Guid softwareId, HttpContext h, ISender s) => { NoQuery(h); return Detail(await s.Send(new GetDeploymentCapabilitiesQuery(softwareId), h.RequestAborted)); });
        g.MapPost("/target-selections", async (HttpContext h, ISender s, IAntiforgery a) => {
            using var d = await Input(h, a, ["softwareId","mode","filter"]); return Result(h, await s.Send(new CreateTargetSelectionCommand(Key(h), Read<SelectionInput>(d)), h.RequestAborted), 201); });
        g.MapGet("/target-selections/{selectionId:guid}", async (Guid selectionId, HttpContext h, ISender s) => { NoQuery(h); return Detail(await s.Send(new GetTargetSelectionQuery(selectionId), h.RequestAborted)); });
        g.MapPut("/target-selections/{selectionId:guid}/chunks/{chunkNo:int}", async (Guid selectionId, int chunkNo, HttpContext h, ISender s, IAntiforgery a) => {
            using var d = await Input(h,a,["instanceIds"]); if(!d.RootElement.TryGetProperty("instanceIds",out var array) || array.ValueKind != JsonValueKind.Array) throw Invalid();
            Guid[] ids; try { ids = array.Deserialize<Guid[]>(Json) ?? throw Invalid(); } catch(JsonException) { throw Invalid(); } return Result(h, await s.Send(new PutTargetChunkCommand(selectionId,chunkNo,ids), h.RequestAborted)); });
        g.MapPost("/target-selections/{selectionId:guid}/seal", async (Guid selectionId,HttpContext h,ISender s,IAntiforgery a) => {
            using var d = await Input(h,a,["expectedRevision","chunkCount","expectedDistinctCount"]); Revision(d.RootElement); var x = Read<SealInput>(d);
            return Result(h,await s.Send(new SealTargetSelectionCommand(Key(h),selectionId,x.ExpectedRevision,x.ChunkCount,x.ExpectedDistinctCount),h.RequestAborted)); });
        g.MapPost("/deployments", async (HttpContext h,ISender s,IAntiforgery a) => { using var d = await Input(h,a,["softwareId","selectionId","kind","targetReleaseId","window","reason","retryOfDeploymentId"]); return Result(h,await s.Send(new CreateDeploymentCommand(Key(h),Read<DeploymentInput>(d)),h.RequestAborted),202); });
        g.MapGet("/deployments",async (HttpContext h,ISender s,TaskCursor c) => {
            var software = TaskCursor.Id(h,"softwareId") ?? throw Invalid(); var state = TaskCursor.Query(h,"state"); var p = await c.Read(h,"deployments",new {software,state},["softwareId","state"]);
            return Page(await s.Send(new ListDeploymentsQuery(new(software,State:state,PageSize:p.Size,After:p.After)),h.RequestAborted),p,c); });
        g.MapGet("/deployments/{deploymentId:guid}",async (Guid deploymentId,HttpContext h,ISender s) => { NoQuery(h); return Detail(await s.Send(new GetDeploymentQuery(deploymentId),h.RequestAborted)); });
        g.MapGet("/deployments/{deploymentId:guid}/targets",async (Guid deploymentId,HttpContext h,ISender s,TaskCursor c) => { var decision=TaskCursor.Query(h,"decision"); var reasonCode=TaskCursor.Query(h,"reasonCode"); var p = await c.Read(h,"targets",new { deploymentId,decision,reasonCode },["decision","reasonCode"]); return Page(await s.Send(new ListDeploymentTargetsQuery(deploymentId,p.Size,p.After,decision,reasonCode),h.RequestAborted),p,c); });
        g.MapGet("/deployments/{deploymentId:guid}/batches",async (Guid deploymentId,HttpContext h,ISender s,TaskCursor c) => { var p = await c.Read(h,"batches",deploymentId,[]); return Page(await s.Send(new GetDeploymentBatchesQuery(deploymentId,p.Size,p.After),h.RequestAborted),p,c); });
        g.MapGet("/deployments/{deploymentId:guid}/tasks",async (Guid deploymentId,HttpContext h,ISender s,TaskCursor c) => {
            var state = TaskCursor.Query(h,"state"); var batchId=TaskCursor.Id(h,"batchId"); var waitReason=TaskCursor.Query(h,"waitReason"); var rawDeferred=TaskCursor.Query(h,"isDeferred"); bool? deferred=rawDeferred is null ? null : bool.TryParse(rawDeferred,out var value) ? value : throw Invalid();
            var p = await c.Read(h,"deployment-tasks",new {deploymentId,state,batchId,waitReason,deferred},["state","batchId","waitReason","isDeferred"]); var d = await s.Send(new GetDeploymentQuery(deploymentId),h.RequestAborted);
            return Page(await s.Send(new ListInstanceTasksQuery(new(d.SoftwareId,deploymentId,state,p.Size,p.After,batchId,waitReason,deferred)),h.RequestAborted),p,c); });
        g.MapGet("/tasks/{taskId:guid}",async (Guid taskId,HttpContext h,ISender s) => { NoQuery(h); return Detail(await s.Send(new GetInstanceTaskQuery(taskId),h.RequestAborted)); });
        g.MapGet("/tasks/{taskId:guid}/receipts",async (Guid taskId,HttpContext h,ISender s,TaskCursor c) => { var p = await c.Read(h,"receipts",taskId,[]); return Page(await s.Send(new ListTaskReceiptsQuery(taskId,p.Size,p.After),h.RequestAborted),p,c); });
        foreach (var action in new[] { "Pause", "Resume", "Reschedule", "Cancel" })
        {
            g.MapPost("/deployments/{deploymentId:guid}/"+action.ToLowerInvariant(),async (Guid deploymentId,HttpContext h,ISender s,IAntiforgery a) => {
                var fields = action == "Resume" ? new[] {"expectedRevision","reason","reviewedFailures"} : action == "Reschedule" ? ["expectedRevision","reason","window"] : ["expectedRevision","reason"];
                using var d = await Input(h,a,fields); Revision(d.RootElement); var x = Read<ControlInput>(d);
                return action is "Reschedule" or "Cancel"
                    ? Result(h,await s.Send(new CreateDeploymentControlWorkCommand(Key(h),deploymentId,x.ExpectedRevision,action,x.Reason,x.Window),h.RequestAborted))
                    : Result(h,await s.Send(new ControlDeploymentCommand(Key(h),deploymentId,x.ExpectedRevision,action,x.Reason,x.ReviewedFailures),h.RequestAborted)); });
        }
        foreach (var action in new[] { "Defer", "Restore", "Cancel", "CloseUnknown" })
        {
            g.MapPost("/tasks/{taskId:guid}/"+(action == "CloseUnknown" ? "close-unknown" : action.ToLowerInvariant()),async (Guid taskId,HttpContext h,ISender s,IAntiforgery a) => {
                using var d = await Input(h,a,action == "CloseUnknown" ? ["expectedRevision","reason","onsiteEvidence","noActiveInstallationConfirmed"] : ["expectedRevision","reason"]); Revision(d.RootElement); var x = Read<ControlInput>(d);
                return Result(h,await s.Send(new ControlInstanceTaskCommand(Key(h),taskId,x.ExpectedRevision,action,x.Reason,x.OnsiteEvidence,x.NoActiveInstallationConfirmed),h.RequestAborted)); });
        }
        g.MapGet("/deployment-work/{workId:guid}",async (Guid workId,HttpContext h,ISender s) => { NoQuery(h); return Detail(await s.Send(new GetTaskWorkQuery(workId),h.RequestAborted)); });
        g.MapGet("/deployment-work/{workId:guid}/items",async (Guid workId,HttpContext h,ISender s,TaskCursor c) => { var p = await c.Read(h,"control-items",workId,[]); return Page(await s.Send(new ListTaskControlItemsQuery(workId,p.Size,p.After),h.RequestAborted),p,c); });
        g.MapPost("/releases/{releaseId:guid}/integration-materials",async (Guid releaseId,HttpContext h,ISender s,IAntiforgery a) => { using var d = await Input(h,a,["dataLocations","updateBehavior","rollbackBehavior","recoveryPlan","verificationConclusion","evidenceReferences","reason"]); return Result(h,await s.Send(new RecordIntegrationMaterialCommand(Key(h),releaseId,Read<IntegrationMaterialInput>(d)),h.RequestAborted),201); });
        g.MapGet("/releases/{releaseId:guid}/integration-materials",async (Guid releaseId,HttpContext h,ISender s,TaskCursor c) => { var p = await c.Read(h,"materials",releaseId,[]); var items = await s.Send(new GetIntegrationMaterialsQuery(releaseId,p.Size+0,p.After),h.RequestAborted); return Results.Json(new {items,nextCursor = items.Count == p.Size ? c.Encode(p,items[^1].Id) : null,serverTime = DateTimeOffset.UtcNow}); });
        var client = Group(app,"/api/v1/client",RequestKind.Client,enabled);
        client.MapGet("/tasks",async (HttpContext h,ISender s,TaskCursor c,IInstanceAccess access,IAccessProofSource proof) => {
            var identity = await access.AuthenticateAsync(proof.Proof ?? throw Invalid(),false,h.RequestAborted) ?? throw new RequestRejectedException(RequestFailure.CredentialInvalid);
            var state = TaskCursor.Query(h,"state"); var p = await c.Read(h,"client-tasks",new {identity.SoftwareId,state},["state"]);
            return Page(await s.Send(new ListClientTasksQuery(new(identity.SoftwareId,State:state,PageSize:p.Size,After:p.After)),h.RequestAborted),p,c); });
        client.MapGet("/tasks/{taskId:guid}",async (Guid taskId,HttpContext h,ISender s) => { NoQuery(h); return Detail(await s.Send(new GetClientTaskQuery(taskId),h.RequestAborted)); });
        client.MapPost("/tasks/{taskId:guid}/claim",async (Guid taskId,HttpContext h,ISender s) => { using var d = await MachineInput(h,[]); var result = await s.Send(new ClaimInstanceTaskCommand(Key(h),taskId),h.RequestAborted); var task = await s.Send(new GetClientTaskQuery(taskId),h.RequestAborted); var release = await s.Send(new GetClientReleaseQuery(task.TargetReleaseId),h.RequestAborted);
            return Detail(new {task,attemptId = result.Value,package = await s.Send(new GetClientPackageQuery(release.PackageId),h.RequestAborted),operationId = result.OperationId}); });
        client.MapPost("/attempts/{attemptId:guid}/start",async (Guid attemptId,HttpContext h,ISender s) => { using var d = await MachineInput(h,["stateReport","preflight"]); return Result(h,await s.Send(new StartInstanceTaskCommand(Key(h),attemptId,Read<StartInput>(d)),h.RequestAborted)); });
        client.MapPost("/attempts/{attemptId:guid}/receipts",async (Guid attemptId,HttpContext h,ISender s) => { using var d = await MachineInput(h,["eventId","sequence","kind","progress","result","failureCode","detail","stateReport"]); return Result(h,await s.Send(new SubmitTaskReceiptCommand(attemptId,Read<ReceiptInput>(d)),h.RequestAborted)); });
    }
    private static T Read<T>(JsonDocument d)
    { try { Duplicates(d.RootElement); NestedFields(d.RootElement); return d.RootElement.Deserialize<T>(Json) ?? throw Invalid(); }
      catch (JsonException) { throw Invalid(); } }
    private static void NestedFields(JsonElement e)
    {
        if(e.ValueKind != JsonValueKind.Object) return;
        foreach(var p in e.EnumerateObject())
        {
            if(p.Value.ValueKind == JsonValueKind.Null) continue;
            string[]? fields = p.Name switch {
                "filter" => ["softwareId","processId","deviceId","deviceNo","reportedIp","installedReleaseId","freshness","runningState","lifecycle"],
                "window" => ["notBefore","latestStart"], "preflight" => ["downloadedSha256","dataProtectionConfirmed","compatibilityDeclarationRevision","note"],
                "stateReport" => ["streamEpoch","reportSeq","reportedAt","installationState","installedReleaseId","installedVersion","installedAt","runningState","reportedIps","databaseState"],
                "databaseState" => ["mode","items"], _ => null };
            if(fields is not null) { Fields(p.Value,fields); NestedFields(p.Value); }
            if(p.Name is "reviewedFailures" or "items")
            { if(p.Value.ValueKind != JsonValueKind.Array) throw Invalid(); foreach(var item in p.Value.EnumerateArray()) Fields(item,p.Name == "items" ? ["databaseKey","schemaId"] : ["batchId","failureRevision"]); }
        }
    }
    private static void Duplicates(JsonElement e)
    { if (e.ValueKind == JsonValueKind.Object) { var names = new HashSet<string>(StringComparer.Ordinal); foreach (var p in e.EnumerateObject()) { if (!names.Add(p.Name)) throw Invalid(); Duplicates(p.Value); } }
      else if (e.ValueKind == JsonValueKind.Array) foreach (var child in e.EnumerateArray()) Duplicates(child); }
    private static async Task<JsonDocument> MachineInput(HttpContext h,string[] fields)
    { NoQuery(h); if (!h.Request.HasJsonContentType()) throw Invalid(); try { var d = await JsonDocument.ParseAsync(h.Request.Body,new JsonDocumentOptions { MaxDepth = 8 },h.RequestAborted); Fields(d.RootElement,fields); return d; } catch (JsonException) { throw Invalid(); } }
    private static void NoQuery(HttpContext h) { if (h.Request.Query.Count != 0) throw Invalid(); }
    private static RequestRejectedException Invalid() => new(RequestFailure.InvalidRequest);
    private static IResult Detail<T>(T value,int status = 200)
    { var e = JsonSerializer.SerializeToElement(value,Json); var result = e.EnumerateObject().ToDictionary(x => x.Name,x => (object?)x.Value); result["serverTime"] = DateTimeOffset.UtcNow; return Results.Json(result,statusCode:status); }
    private static IResult Result<T>(HttpContext h,OperationResult<T> r,int status = 200)
    { if (r.Status == OperationStatus.Accepted) { status = 202; h.Response.Headers.Location = $"/api/v1/manage/deployment-work/{r.WorkId:D}"; h.Response.Headers.RetryAfter = "5"; }
      var e = JsonSerializer.SerializeToElement(r.Value,Json); var result = e.EnumerateObject().ToDictionary(x => x.Name,x => (object?)x.Value); result["operationId"] = r.OperationId; result["workId"] = r.WorkId; result["serverTime"] = DateTimeOffset.UtcNow; return Results.Json(result,statusCode:status); }
    private static IResult Page<T>(TaskPage<T> p,TaskPageRequest x,TaskCursor c) => Results.Json(new {items = p.Items,nextCursor = c.Encode(x,p.Next),serverTime = DateTimeOffset.UtcNow});
}
