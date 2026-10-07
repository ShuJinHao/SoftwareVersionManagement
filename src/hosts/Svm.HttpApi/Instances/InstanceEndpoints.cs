using System.Text.Json;
using MediatR;
using Microsoft.AspNetCore.Antiforgery;
using Svm.HttpApi.Personnel;
using Svm.Services.Contracts.Framework;
using Svm.Services.Contracts.Instances;
using static Svm.HttpApi.Personnel.UserEndpoints;

namespace Svm.HttpApi.Instances;

internal static class InstanceEndpoints
{
    internal static void MapInstanceAccess(this WebApplication app)
    {
        var manage=app.MapGroup("/api/v1/manage").WithMetadata(new PersonnelEndpointKind(RequestKind.Manage));
        manage.AddEndpointFilter(async (c,next)=> { if(!c.HttpContext.Request.IsHttps) throw new RequestRejectedException(RequestFailure.PermissionDenied); return await next(c); });
        manage.MapGet("/enrollment-grants",async (HttpContext h,ISender s,InstanceCursor c)=>
        {
            var sid=InstanceCursor.QueryId(h,"softwareId") ?? throw Invalid(); var input=await c.ReadAsync(h,"enrollment-grants",sid,["softwareId"],h.RequestAborted);
            return Page(await s.Send(new ListEnrollmentGrantsQuery(sid,input.Size,input.After),h.RequestAborted),input,c);
        });
        manage.MapPost("/enrollment-grants",async (HttpContext h,ISender s,IAntiforgery a)=>
        {
            using var d=await Input(h,a,["softwareId","deviceIds","expiresAt","maxInstances","secretMaterial","reason"]); var b=d.RootElement;
            var ids=Array(b,"deviceIds").Select(x=>x.ValueKind==JsonValueKind.String && x.TryGetGuid(out var id)?id:throw Invalid()).ToArray();
            var v=(await s.Send(new CreateEnrollmentGrantCommand(Key(h),Id(b,"softwareId"),ids,Date(b,"expiresAt"),Capacity(b),Text(b,"secretMaterial"),Text(b,"reason")),h.RequestAborted)).Value;
            return Detail(v,201);
        });
        manage.MapPost("/enrollment-grants/{grantId:guid}/revoke",async (Guid grantId,HttpContext h,ISender s,IAntiforgery a)=>
        { using var d=await Input(h,a,["reason","expectedRevision"]); var b=d.RootElement; return Detail((await s.Send(new RevokeEnrollmentGrantCommand(Key(h),grantId,Revision(b),Text(b,"reason")),h.RequestAborted)).Value); });
        manage.MapGet("/instances/{instanceId:guid}/credentials",async (Guid instanceId,HttpContext h,ISender s,InstanceCursor c)=>
        { var input=await c.ReadAsync(h,"credentials/"+instanceId,instanceId,[],h.RequestAborted); return Page(await s.Send(new ListInstanceCredentialsQuery(instanceId,input.Size,input.After),h.RequestAborted),input,c); });
        manage.MapPost("/credentials/{credentialId:guid}/revoke",async (Guid credentialId,HttpContext h,ISender s,IAntiforgery a)=>
        { using var d=await Input(h,a,["reason","expectedRevision"]); var b=d.RootElement; return Detail((await s.Send(new RevokeInstanceCredentialCommand(Key(h),credentialId,Revision(b),Text(b,"reason")),h.RequestAborted)).Value); });
        manage.MapPost("/instances/{instanceId:guid}/recovery-grants",async (Guid instanceId,HttpContext h,ISender s,IAntiforgery a)=>
        { using var d=await Input(h,a,["expiresAt","secretMaterial","reason"]); var b=d.RootElement; return Detail((await s.Send(new CreateRecoveryGrantCommand(Key(h),instanceId,Date(b,"expiresAt"),Text(b,"secretMaterial"),Text(b,"reason")),h.RequestAborted)).Value,201); });
        manage.MapPost("/recovery-grants/{grantId:guid}/revoke",async (Guid grantId,HttpContext h,ISender s,IAntiforgery a)=>
        { using var d=await Input(h,a,["reason","expectedRevision"]); var b=d.RootElement; return Detail((await s.Send(new RevokeRecoveryGrantCommand(Key(h),grantId,Revision(b),Text(b,"reason")),h.RequestAborted)).Value); });
        manage.MapGet("/instances",async (HttpContext h,ISender s,InstanceCursor c)=>
        {
            var filter=new InstanceFilter(InstanceCursor.QueryId(h,"softwareId") ?? throw Invalid(),InstanceCursor.QueryId(h,"processId"),InstanceCursor.QueryId(h,"deviceId"),InstanceCursor.Query(h,"deviceNo"),InstanceCursor.Query(h,"reportedIp"),InstanceCursor.QueryId(h,"installedReleaseId"),InstanceCursor.Query(h,"freshness"),InstanceCursor.Query(h,"runningState"),InstanceCursor.Query(h,"lifecycle"));
            var input=await c.ReadAsync(h,"instances",filter,["softwareId","processId","deviceId","deviceNo","reportedIp","installedReleaseId","freshness","runningState","lifecycle"],h.RequestAborted);
            return Page(await s.Send(new ListInstancesQuery(new(filter,input.Size,input.After)),h.RequestAborted),input,c);
        });
        manage.MapGet("/instances/{instanceId:guid}",async (Guid instanceId,HttpContext h,ISender s)=> { EmptyQuery(h); return Detail(await s.Send(new GetInstanceQuery(instanceId),h.RequestAborted)); });
        manage.MapGet("/instances/{instanceId:guid}/version-history",async (Guid instanceId,HttpContext h,ISender s,InstanceCursor c)=>
        { var input=await c.ReadAsync(h,"history/"+instanceId,instanceId,[],h.RequestAborted); return Page(await s.Send(new GetInstanceHistoryQuery(instanceId,input.Size,input.After),h.RequestAborted),input,c); });
        manage.MapPatch("/instances/{instanceId:guid}/lifecycle",async (Guid instanceId,HttpContext h,ISender s,IInstanceQueries q,IAntiforgery a)=>
        { using var d=await Input(h,a,["lifecycle","reason","expectedRevision"]); var b=d.RootElement; await s.Send(new UpdateInstanceLifecycleCommand(Key(h),instanceId,Revision(b),Text(b,"lifecycle"),Text(b,"reason")),h.RequestAborted); return Detail(await q.GetAsync(instanceId,h.RequestAborted,"instance.manage") ?? throw new RequestRejectedException(RequestFailure.ResourceNotFound)); });
        var enrollment=app.MapGroup("/api/v1/enrollment");
        enrollment.MapPost("/instances",async (HttpContext h,ISender s)=>
        { using var d=await MachineInput(h,["softwareId","installationKey","deviceId","secretMaterial"]); var b=d.RootElement; return Detail((await s.Send(new RegisterInstanceCommand(Key(h),Id(b,"softwareId"),Id(b,"installationKey"),Id(b,"deviceId"),Text(b,"secretMaterial")),h.RequestAborted)).Value,201); }).WithMetadata(new PersonnelEndpointKind(RequestKind.Enrollment));
        enrollment.MapPost("/recoveries",async (HttpContext h,ISender s)=>
        { using var d=await MachineInput(h,["secretMaterial"]); return Detail((await s.Send(new RecoverInstanceCommand(Key(h),Text(d.RootElement,"secretMaterial")),h.RequestAborted)).Value); }).WithMetadata(new PersonnelEndpointKind(RequestKind.Recovery));
        var client=app.MapGroup("/api/v1/client").WithMetadata(new PersonnelEndpointKind(RequestKind.Client));
        client.MapGet("/context",async (HttpContext h,ISender s)=> { EmptyQuery(h); return Detail(await s.Send(new GetClientContextQuery(),h.RequestAborted)); });
        client.MapPost("/report-streams",async (HttpContext h,ISender s)=>
        { using var d=await MachineInput(h,["expectedEpoch"]); return Detail((await s.Send(new OpenReportStreamCommand(Key(h),Number(d.RootElement,"expectedEpoch")),h.RequestAborted)).Value); });
        client.MapPost("/status-reports",async (HttpContext h,ISender s)=>
        {
            using var d=await MachineInput(h,["streamEpoch","reportSeq","reportedAt","installationState","installedReleaseId","installedVersion","installedAt","runningState","reportedIps","databaseState"]); var b=d.RootElement;
            if(!b.TryGetProperty("databaseState",out var db)) throw Invalid(); Fields(db,["mode","items"]);
            var items=Array(db,"items").Select(x=> { Fields(x,["databaseKey","schemaId"]); return new DatabaseItem(Text(x,"databaseKey"),Text(x,"schemaId")); }).ToArray();
            var ips=Array(b,"reportedIps").Select(x=>x.ValueKind==JsonValueKind.String?x.GetString()!:throw Invalid()).ToArray();
            var report=new StateReport(Number(b,"streamEpoch"),Number(b,"reportSeq"),Date(b,"reportedAt"),Text(b,"installationState"),OptionalId(b,"installedReleaseId"),OptionalText(b,"installedVersion"),OptionalDate(b,"installedAt"),Text(b,"runningState"),ips,new(Text(db,"mode"),items));
            return Detail((await s.Send(new SubmitStatusReportCommand(report),h.RequestAborted)).Value);
        });
    }
    private static IResult Detail<T>(T value,int status=200)
    {
        // Project the DTO and the public envelope without changing its shape or retaining it for replay.
        var json=JsonSerializer.SerializeToElement(value,new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var fields=json.EnumerateObject().ToDictionary(p=>p.Name,p=>(object?)p.Value,StringComparer.Ordinal); fields.Add("serverTime",DateTimeOffset.UtcNow);
        return Results.Json(fields,statusCode:status);
    }
    private static IResult Page<T>(InstancePage<T> p,InstancePageRequest x,InstanceCursor c) => Results.Json(new { items=p.Items,nextCursor=c.Encode(x,p.Next),serverTime=DateTimeOffset.UtcNow });
    private static async Task<JsonDocument> MachineInput(HttpContext h,string[] fields)
    {
        if(!h.Request.IsHttps) throw new RequestRejectedException(RequestFailure.PermissionDenied); EmptyQuery(h);
        if(!h.Request.HasJsonContentType()) throw Invalid();
        JsonDocument? d=null; try { d=await JsonDocument.ParseAsync(h.Request.Body,new JsonDocumentOptions { MaxDepth=8 },h.RequestAborted); Fields(d.RootElement,fields); return d; }
        catch(JsonException) { d?.Dispose(); throw Invalid(); } catch { d?.Dispose(); throw; }
    }
    private static void Fields(JsonElement b,string[] fields)
    {
        if(b.ValueKind!=JsonValueKind.Object) throw Invalid(); var ps=b.EnumerateObject().ToArray();
        if(ps.Any(p=>!fields.Contains(p.Name,StringComparer.Ordinal))) throw new RequestRejectedException(RequestFailure.UnknownField);
        if(ps.Select(p=>p.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count()!=ps.Length) throw Invalid();
    }
    private static Guid Id(JsonElement b,string key) => Guid.TryParseExact(Text(b,key),"D",out var id) && id!=Guid.Empty?id:throw Invalid();
    private static Guid? OptionalId(JsonElement b,string key) => !b.TryGetProperty(key,out var v) || v.ValueKind==JsonValueKind.Null?null:Id(b,key);
    private static int Capacity(JsonElement b) => Number(b,"maxInstances") is > 0 and <= 100000 ? (int)Number(b,"maxInstances") : throw new RequestRejectedException(RequestFailure.ValidationFailed);
    private static long Number(JsonElement b,string key) => b.TryGetProperty(key,out var v) && v.ValueKind==JsonValueKind.Number && v.TryGetInt64(out var n)?n:throw Invalid();
    private static DateTimeOffset Date(JsonElement b,string key) => b.TryGetProperty(key,out var v) && v.ValueKind==JsonValueKind.String && v.TryGetDateTimeOffset(out var t)?t.ToUniversalTime():throw Invalid();
    private static DateTimeOffset? OptionalDate(JsonElement b,string key) => !b.TryGetProperty(key,out var v) || v.ValueKind==JsonValueKind.Null?null:Date(b,key);
    private static IEnumerable<JsonElement> Array(JsonElement b,string key) => b.TryGetProperty(key,out var v) && v.ValueKind==JsonValueKind.Array?v.EnumerateArray():throw Invalid();
    private static void EmptyQuery(HttpContext h) { if(h.Request.Query.Count!=0) throw Invalid(); }
    private static RequestRejectedException Invalid() => new(RequestFailure.InvalidRequest);
}
