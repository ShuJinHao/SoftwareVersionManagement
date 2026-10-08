using System.Text.Json;
using System.Net.Http.Headers;
using Svm.FileStorage;
using MediatR;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http.Features;
using Svm.HttpApi.Personnel;
using Svm.HttpApi.Instances;
using Svm.Services.Contracts.Framework;
using Svm.Services.Contracts.Packages;
using static Svm.HttpApi.Personnel.UserEndpoints;

namespace Svm.HttpApi.Packages;

internal static class PackageEndpoints
{
    internal static void MapPackages(this WebApplication app, bool enabled)
    {
        var group = app.MapGroup("/api/v1/manage").WithMetadata(new PersonnelEndpointKind(RequestKind.Manage));
        group.AddEndpointFilter(async (context, next) => { Require(context.HttpContext, enabled); return await next(context); });
        group.MapGet("/capabilities", async (HttpContext http, ISender sender) => { Empty(http); return Results.Json(await sender.Send(new GetPackageCapabilitiesQuery(), http.RequestAborted)); });
        group.MapPost("/software/{softwareId:guid}/releases", async (Guid softwareId, HttpContext http, ISender sender, IAntiforgery csrf) =>
        {
            using var doc = await Input(http, csrf, ["changeLevel", "changeSummary", "changeReason", "expectedVersion", "package"]); var b = doc.RootElement;
            if (!b.TryGetProperty("package", out var package)) throw Invalid(); Fields(package, ["fileName", "sizeBytes", "sha256"]);
            if (!package.TryGetProperty("sizeBytes", out var size) || !size.TryGetInt64(out var bytes)) throw Invalid();
            var result = await sender.Send(new CreateReleaseCommand(Key(http), softwareId, Text(b, "changeLevel"), Text(b, "changeSummary"), Text(b, "changeReason"),
                OptionalText(b, "expectedVersion"), new(Text(package, "fileName"), bytes, Text(package, "sha256"))), http.RequestAborted);
            return Results.Json(result.Value, statusCode: 201);
        });
        group.MapGet("/software/{softwareId:guid}/releases", async (Guid softwareId, HttpContext http, ISender sender, PackageCursor cursor) =>
        {
            var input = await cursor.ReadAsync(http, softwareId, "releases/" + softwareId, false, http.RequestAborted);
            var page = await sender.Send(new ListReleasesQuery(input.Input), http.RequestAborted);
            return Results.Json(new { items = page.Items, nextCursor = cursor.Encode(input, page.Next), serverTime = DateTimeOffset.UtcNow });
        });
        group.MapGet("/releases/{releaseId:guid}", async (Guid releaseId, HttpContext http, ISender sender) => { Empty(http); return Results.Json(await sender.Send(new GetReleaseQuery(releaseId), http.RequestAborted)); });
        group.MapPost("/releases/{releaseId:guid}/disable", async (Guid releaseId, HttpContext http, ISender sender, IAntiforgery csrf) =>
        { using var doc = await Input(http, csrf, ["reason", "expectedRevision"]); return Results.Json((await sender.Send(new DisableReleaseCommand(Key(http), releaseId, Revision(doc.RootElement), Text(doc.RootElement, "reason")), http.RequestAborted)).Value); });
        group.MapPut("/uploads/{uploadId:guid}/content", Receive);
        group.MapGet("/uploads/{uploadId:guid}", async (Guid uploadId, HttpContext http, ISender sender) => { Empty(http); return Results.Json(await sender.Send(new GetPackageQuery(uploadId, true), http.RequestAborted)); });
        group.MapGet("/packages/{packageId:guid}", async (Guid packageId, HttpContext http, ISender sender) => { Empty(http); return Results.Json(await sender.Send(new GetPackageQuery(packageId), http.RequestAborted)); });
        group.MapPost("/packages/{packageId:guid}/retry", async (Guid packageId, HttpContext http, ISender sender, IAntiforgery csrf) =>
        { using var doc = await Input(http, csrf, ["reason", "expectedRevision"]); return Results.Json((await sender.Send(new RetryPackageCommand(Key(http), packageId, Revision(doc.RootElement), Text(doc.RootElement, "reason")), http.RequestAborted)).Value, statusCode: 202); });
        group.MapGet("/package-work/{workId:guid}", async (Guid workId, HttpContext http, ISender sender) => { Empty(http); return Results.Json(await sender.Send(new GetPackageWorkQuery(workId), http.RequestAborted)); });
        group.MapGet("/releases/{releaseId:guid}/test-evidence", async (Guid releaseId, HttpContext http, ISender sender, InstanceCursor cursor) =>
        {
            var input = await cursor.ReadAsync(http, "test-evidence/" + releaseId, new { releaseId }, [], http.RequestAborted);
            var page = await sender.Send(new GetTestEvidenceQuery(releaseId, input.Size, input.After), http.RequestAborted);
            return Results.Json(new { items = page.Items, nextCursor = cursor.Encode(input, page.Next), serverTime = DateTimeOffset.UtcNow });
        });
        group.MapGet("/audit-events", async (HttpContext http, ISender sender, InstanceCursor cursor) =>
        {
            var software = InstanceCursor.QueryId(http, "softwareId") ?? throw Invalid();
            var input = await cursor.ReadAsync(http, "download-audit", new { software }, ["softwareId"], http.RequestAborted);
            var page = await sender.Send(new GetDownloadAuditQuery(software, input.Size, input.After), http.RequestAborted);
            return Results.Json(new { items = page.Items, nextCursor = cursor.Encode(input, page.Next), serverTime = DateTimeOffset.UtcNow });
        });
        var client = app.MapGroup("/api/v1/client").WithMetadata(new PersonnelEndpointKind(RequestKind.Client));
        client.AddEndpointFilter(async (context, next) => { Require(context.HttpContext, enabled); return await next(context); });
        client.MapGet("/versions", async (HttpContext http, ISender sender, ICallContext calls, PackageCursor cursor) =>
        {
            var input = await cursor.ReadAsync(http, calls.Current!.Actor.SoftwareId!.Value, "client-versions", true, http.RequestAborted);
            var page = await sender.Send(new ListClientReleasesQuery(input.Input), http.RequestAborted);
            return Results.Json(new { items = page.Items, nextCursor = cursor.Encode(input, page.Next), serverTime = DateTimeOffset.UtcNow });
        });
        client.MapGet("/versions/{releaseId:guid}", async (Guid releaseId, HttpContext http, ISender sender) => { Empty(http); return Results.Json(await sender.Send(new GetClientReleaseQuery(releaseId), http.RequestAborted)); });
        client.MapGet("/packages/{packageId:guid}", async (Guid packageId, HttpContext http, ISender sender) => { Empty(http); return Results.Json(await sender.Send(new GetClientPackageQuery(packageId), http.RequestAborted)); });
    }
    internal static async Task<IResult> Receive(Guid uploadId, HttpContext http, ISender sender, IAntiforgery csrf)
    {
        var options = http.RequestServices.GetService<PackageFileOptions>() ?? throw new RequestRejectedException(RequestFailure.ConfigurationInvalid);
        Require(http, true);
        try { await csrf.ValidateRequestAsync(http); } catch (AntiforgeryValidationException) { throw new RequestRejectedException(RequestFailure.PermissionDenied); }
        if (http.Request.ContentType != "application/octet-stream") throw Invalid(); Empty(http);
        var feature = http.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (feature is null || feature.IsReadOnly) throw Invalid(); feature.MaxRequestBodySize = options.Limits.MaxPackageBytes;
        if (http.Request.ContentLength > options.Limits.MaxPackageBytes) throw new RequestRejectedException(RequestFailure.PayloadTooLarge);
        var source = await sender.Send(new GetUploadTargetQuery(uploadId), http.RequestAborted);
        if (source.Length != 0 && source != options.NodeId)
        {
            if (http.Request.Path.StartsWithSegments("/internal")) throw new RequestRejectedException(RequestFailure.InvalidState);
            var node = options.Nodes.Single(n => n.NodeId == source);
            using var request = new HttpRequestMessage(HttpMethod.Put, new Uri(new Uri(node.InternalBaseUri), $"internal/v1/uploads/{uploadId:D}/content"));
            request.Headers.TryAddWithoutValidation("Cookie", http.Request.Headers.Cookie.ToArray());
            request.Headers.TryAddWithoutValidation("X-CSRF-TOKEN", http.Request.Headers["X-CSRF-TOKEN"].ToArray());
            request.Content = new StreamContent(http.Request.Body); request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            request.Content.Headers.ContentLength = http.Request.ContentLength;
            using var response = await http.RequestServices.GetRequiredService<PackagePeerClient>().Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, http.RequestAborted);
            http.Response.StatusCode = (int)response.StatusCode; http.Response.ContentType = response.Content.Headers.ContentType?.ToString() ?? "application/json";
            await response.Content.CopyToAsync(http.Response.Body, http.RequestAborted); return Results.Empty;
        }
        var result = await sender.Send(new UploadContentCommand(uploadId, http.Request.Body, http.Request.ContentLength), http.RequestAborted);
        return Results.Json(result.Value, statusCode: 202);
    }
    internal static void Require(HttpContext http, bool enabled)
    { if (!http.Request.IsHttps) throw new RequestRejectedException(RequestFailure.PermissionDenied); if (!enabled) throw new RequestRejectedException(RequestFailure.ConfigurationInvalid); }
    internal static void Empty(HttpContext http) { if (http.Request.Query.Count != 0) throw Invalid(); }
    private static RequestRejectedException Invalid() => new(RequestFailure.InvalidRequest);
}
