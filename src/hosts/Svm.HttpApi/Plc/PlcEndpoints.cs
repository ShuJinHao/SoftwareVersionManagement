using System.Text.Json;
using System.Net.Http.Headers;
using MediatR;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http.Features;
using Svm.FileStorage;
using Svm.HttpApi.Personnel;
using Svm.HttpApi.Instances;
using Svm.HttpApi.Packages;
using Svm.Services.Contracts.Framework;
using Svm.Services.Contracts.Plc;
using Svm.Services.Contracts.Packages;
using static Svm.HttpApi.Personnel.UserEndpoints;
namespace Svm.HttpApi.Plc;

internal static class PlcEndpoints
{
    internal static void MapPlc(this WebApplication app,bool enabled)
    {
        var g=app.MapGroup("/api/v1/manage").WithMetadata(new PersonnelEndpointKind(RequestKind.Manage));
        g.AddEndpointFilter(async(c,next)=>{PackageEndpoints.Require(c.HttpContext,enabled);return await next(c);});
        g.MapPost("/software/{softwareId:guid}/plc-upload-credentials",async(Guid softwareId,HttpContext h,ISender s,IAntiforgery a)=>
        {
            using var d=await Input(h,a,["name","scope","expiresAt","secretMaterial"]);var b=d.RootElement;
            if(!b.TryGetProperty("scope",out var scopes)||scopes.ValueKind!=JsonValueKind.Array)throw Invalid();
            var entries=scopes.EnumerateArray().Select(v=>{Fields(v,["bindingId","bindingRevision"]);return new PlcScope(Id(v,"bindingId"),Number(v,"bindingRevision"));}).ToArray();
            return Results.Json((await s.Send(new CreatePlcCredentialCommand(Key(h),softwareId,Text(b,"name"),entries,Date(b,"expiresAt"),Text(b,"secretMaterial")),h.RequestAborted)).Value,statusCode:201);
        });
        g.MapGet("/software/{softwareId:guid}/plc-upload-credentials",async(Guid softwareId,HttpContext h,ISender s,InstanceCursor c)=>
        {var q=await c.ReadAsync(h,"plc-credentials/"+softwareId,new{softwareId},[],h.RequestAborted);var p=await s.Send(new ListPlcCredentialsQuery(softwareId,q.Size,q.After),h.RequestAborted);return Results.Json(new{items=p.Items,nextCursor=c.Encode(q,p.Next)});});
        g.MapPost("/plc-upload-credentials/{credentialId:guid}/revoke",async(Guid credentialId,HttpContext h,ISender s,IAntiforgery a)=>
        {using var d=await Input(h,a,["expectedRevision","reason"]);return Results.Json((await s.Send(new RevokePlcCredentialCommand(Key(h),credentialId,Revision(d.RootElement),Text(d.RootElement,"reason")),h.RequestAborted)).Value);});
        g.MapPost("/releases/{releaseId:guid}/plc-publish",async(Guid releaseId,HttpContext h,ISender s,IAntiforgery a)=>
        {
            using var d=await Input(h,a,["expectedRevision","bindingId","bindingRevision","fieldAt","verification","materials","publishReason","publishConclusion"]);var b=d.RootElement;
            return Results.Json((await s.Send(new PublishPlcReleaseCommand(Key(h),releaseId,Revision(b),Id(b,"bindingId"),Number(b,"bindingRevision"),Date(b,"fieldAt"),Text(b,"verification"),Text(b,"materials"),Text(b,"publishReason"),Text(b,"publishConclusion")),h.RequestAborted)).Value);
        });
        g.MapGet("/releases/{releaseId:guid}/plc-evidence",async(Guid releaseId,HttpContext h,ISender s)=>{PackageEndpoints.Empty(h);return Results.Json(await s.Send(new GetPlcEvidenceQuery(releaseId),h.RequestAborted));});
        g.MapGet("/hardware/{hardwareId:guid}/plc-usage",async(Guid hardwareId,HttpContext h,ISender s,InstanceCursor c)=>
        {var q=await c.ReadAsync(h,"plc-usage/"+hardwareId,new{hardwareId},[],h.RequestAborted);var p=await s.Send(new ListPlcUsageQuery(hardwareId,q.Size,q.After),h.RequestAborted);return Results.Json(new{items=p.Items,nextCursor=c.Encode(q,p.Next)});});
        g.MapGet("/hardware-bindings/{bindingId:guid}/plc-usage",async(Guid bindingId,HttpContext h,ISender s)=>{PackageEndpoints.Empty(h);return Results.Json(await s.Send(new GetPlcUsageQuery(bindingId),h.RequestAborted));});
        g.MapPost("/hardware-bindings/{bindingId:guid}/plc-usage",async(Guid bindingId,HttpContext h,ISender s,IAntiforgery a)=>
        {
            using var d=await Input(h,a,["bindingRevision","releaseId","expectedRevision","fieldAt","note","materials","correctsId"]);var b=d.RootElement;
            return Results.Json((await s.Send(new RecordPlcUsageCommand(Key(h),bindingId,Number(b,"bindingRevision"),Id(b,"releaseId"),Revision(b),Date(b,"fieldAt"),Text(b,"note"),Text(b,"materials"),OptionalId(b,"correctsId")),h.RequestAborted)).Value,statusCode:201);
        });
        var upload=app.MapGroup("/api/v1/plc-upload").WithMetadata(new PersonnelEndpointKind(RequestKind.PlcUpload));
        upload.AddEndpointFilter(async(c,next)=>{PackageEndpoints.Require(c.HttpContext,enabled);return await next(c);});
        upload.MapPost("/archives",async(HttpContext h,ISender s)=>
        {
            using var d=await MachineInput(h,["bindingId","bindingRevision","changeLevel","changeSummary","changeReason","expectedVersion","package"]);var b=d.RootElement;
            if(!b.TryGetProperty("package",out var p))throw Invalid();Fields(p,["fileName","sizeBytes","sha256"]);
            var result=await s.Send(new CreatePlcArchiveCommand(Key(h),Id(b,"bindingId"),Number(b,"bindingRevision"),Text(b,"changeLevel"),Text(b,"changeSummary"),Text(b,"changeReason"),OptionalText(b,"expectedVersion"),new(Text(p,"fileName"),Number(p,"sizeBytes"),Text(p,"sha256"))),h.RequestAborted);
            return Results.Json(result.Value,statusCode:201);
        });
        upload.MapPut("/uploads/{uploadId:guid}/content",Receive);
        upload.MapGet("/uploads/{uploadId:guid}",async(Guid uploadId,HttpContext h,ISender s)=>{PackageEndpoints.Empty(h);return Results.Json(await s.Send(new PlcGetPackageQuery(uploadId,true),h.RequestAborted));});
        upload.MapGet("/package-work/{workId:guid}",async(Guid workId,HttpContext h,ISender s)=>{PackageEndpoints.Empty(h);return Results.Json(await s.Send(new PlcGetPackageWorkQuery(workId),h.RequestAborted));});
        upload.MapPost("/packages/{packageId:guid}/retry",async(Guid packageId,HttpContext h,ISender s)=>
        {using var d=await MachineInput(h,["expectedRevision","reason"]);return Results.Json((await s.Send(new PlcRetryPackageCommand(Key(h),packageId,Revision(d.RootElement),Text(d.RootElement,"reason")),h.RequestAborted)).Value,statusCode:202);});
        app.MapPut("/internal/v1/plc-upload/uploads/{uploadId:guid}/content",Receive).WithMetadata(new PersonnelEndpointKind(RequestKind.PlcUpload))
            .AddEndpointFilter(async(c,next)=>{PackageEndpoints.Require(c.HttpContext,enabled);if(c.HttpContext.Items[HttpPackageIdentity.PeerItem] is not PackagePeer{Role:"Peer"})throw new RequestRejectedException(RequestFailure.PermissionDenied);return await next(c);});
    }
    private static async Task<JsonDocument> MachineInput(HttpContext h,string[] fields)
    {
        PackageEndpoints.Empty(h);if(!h.Request.HasJsonContentType())throw Invalid();JsonDocument? d=null;
        try{d=await JsonDocument.ParseAsync(h.Request.Body,new JsonDocumentOptions{MaxDepth=8},h.RequestAborted);Fields(d.RootElement,fields);return d;}
        catch(JsonException){d?.Dispose();throw Invalid();}catch{d?.Dispose();throw;}
    }
    private static Guid Id(JsonElement b,string name)=>Guid.TryParseExact(Text(b,name),"D",out var id)&&id!=Guid.Empty?id:throw Invalid();
    private static Guid? OptionalId(JsonElement b,string name)=>!b.TryGetProperty(name,out var value)||value.ValueKind==JsonValueKind.Null?null:Id(b,name);
    private static long Number(JsonElement b,string name)=>b.TryGetProperty(name,out var value)&&value.ValueKind==JsonValueKind.Number&&value.TryGetInt64(out var n)?n:throw Invalid();
    private static DateTimeOffset Date(JsonElement b,string name)=>b.TryGetProperty(name,out var v)&&v.ValueKind==JsonValueKind.String&&v.TryGetDateTimeOffset(out var t)?t.ToUniversalTime():throw Invalid();
    private static RequestRejectedException Invalid()=>new(RequestFailure.InvalidRequest);
    internal static async Task<IResult> Receive(Guid uploadId, HttpContext http, ISender sender)
    {
        var options = http.RequestServices.GetService<PackageFileOptions>() ?? throw new RequestRejectedException(RequestFailure.ConfigurationInvalid);
        PackageEndpoints.Require(http, true);
        if (http.Request.ContentType != "application/octet-stream") throw Invalid(); PackageEndpoints.Empty(http);
        var feature = http.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (feature is null || feature.IsReadOnly) throw Invalid(); feature.MaxRequestBodySize = options.Limits.MaxPackageBytes;
        if (http.Request.ContentLength > options.Limits.MaxPackageBytes) throw new RequestRejectedException(RequestFailure.PayloadTooLarge);
        var source = await sender.Send(new PlcGetUploadTargetQuery(uploadId), http.RequestAborted);
        if (source.Length != 0 && source != options.NodeId)
        {
            if (http.Request.Path.StartsWithSegments("/internal")) throw new RequestRejectedException(RequestFailure.InvalidState);
            var node = options.Nodes.Single(n => n.NodeId == source);
            using var request = new HttpRequestMessage(HttpMethod.Put, new Uri(new Uri(node.InternalBaseUri), $"internal/v1/plc-upload/uploads/{uploadId:D}/content"));
            request.Headers.TryAddWithoutValidation("Authorization", http.Request.Headers.Authorization.ToArray());
            request.Content = new StreamContent(http.Request.Body); request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            request.Content.Headers.ContentLength = http.Request.ContentLength;
            using var response = await http.RequestServices.GetRequiredService<PackagePeerClient>().Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, http.RequestAborted);
            http.Response.StatusCode = (int)response.StatusCode; http.Response.ContentType = response.Content.Headers.ContentType?.ToString() ?? "application/json";
            await response.Content.CopyToAsync(http.Response.Body, http.RequestAborted); return Results.Empty;
        }
        var result = await sender.Send(new PlcUploadContentCommand(uploadId, http.Request.Body, http.Request.ContentLength), http.RequestAborted);
        return Results.Json(result.Value, statusCode: 202);
    }
}
