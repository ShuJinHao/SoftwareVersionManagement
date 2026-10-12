using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Features;
using Svm.FileStorage;
using Svm.HttpApi.Personnel;
using Svm.HttpApi.Instances;
using Svm.Services.Contracts.Framework;
using Svm.Services.Contracts.Packages;
using Svm.Services.CrossCutting.Pipeline;
using static Svm.HttpApi.Personnel.UserEndpoints;

namespace Svm.HttpApi.Packages;

internal static class PackageInternalEndpoints
{
    internal static void MapPackageInternal(this WebApplication app, bool enabled)
    {
        if (!enabled) return;
        var group = app.MapGroup("/internal/v1").WithMetadata(new PersonnelEndpointKind(RequestKind.Internal));
        group.AddEndpointFilter(async (context, next) => { PackageEndpoints.Require(context.HttpContext, enabled); return await next(context); });
        app.MapPut("/internal/v1/uploads/{uploadId:guid}/content", PackageEndpoints.Receive)
            .WithMetadata(new PersonnelEndpointKind(RequestKind.Manage))
            .AddEndpointFilter(async (context, next) =>
            {
                if (context.HttpContext.Items[HttpPackageIdentity.PeerItem] is not PackagePeer { Role: "Peer" }) throw new RequestRejectedException(RequestFailure.PermissionDenied);
                return await next(context);
            });
        group.MapMethods("/package-replicas/{packageId:guid}/integrity", ["HEAD"], async (Guid packageId, HttpContext http, IPackageFiles files, ScopedRequestExecutor executor) =>
        {
            Bind(http, packageId, "Peer"); PackageEndpoints.Empty(http);
            var p = await executor.SendAsync(new InspectReplicaQuery(packageId), http.RequestAborted);
            var fact = await files.InspectLocalAsync(packageId, p.ExpectedSize, p.ExpectedSha256, http.RequestAborted);
            if (fact.State != "Healthy") return Results.StatusCode(503);
            http.Response.Headers["X-Svm-Sha256"] = p.ExpectedSha256; http.Response.ContentLength = p.ExpectedSize;
            return Results.StatusCode(200);
        });
        group.MapGet("/package-work/{workId:guid}/source", async (Guid workId, HttpContext http, IPackageFiles files, ScopedRequestExecutor executor) =>
        {
            Bind(http, workId, "Peer"); var w = await Lease(http, executor, workId);
            if (w.SourceNode != files.NodeId) throw new RequestRejectedException(RequestFailure.PermissionDenied);
            http.Response.ContentLength = w.ExpectedSize;
            return Results.Stream(await files.OpenSourceAsync(w, http.RequestAborted), "application/octet-stream");
        });
        group.MapPut("/package-replicas/{packageId:guid}/content", async (Guid packageId, HttpContext http, IPackageFiles files, ScopedRequestExecutor executor, PackageLimits limits) =>
        {
            var workId = InstanceCursor.QueryId(http, "workId") ?? throw Invalid(); Bind(http, workId, "Peer");
            var w = await Lease(http, executor, workId);
            if (w.PackageId != packageId || http.Request.ContentType?.Split(';')[0] != "application/octet-stream" ||
                http.Request.ContentLength is { } length && length != w.ExpectedSize) throw Invalid();
            var feature = http.Features.Get<IHttpMaxRequestBodySizeFeature>() ?? throw Invalid();
            if (feature.IsReadOnly) throw Invalid(); feature.MaxRequestBodySize = limits.MaxPackageBytes;
            await files.ReceiveReplicaAsync(w, http.Request.Body, async ct => { await Lease(http, executor, workId, ct); }, http.RequestAborted);
            return Results.Json(new { verified = true });
        });
        group.MapGet("/download-authorizations", async (HttpContext http, IPackageFiles files, ScopedRequestExecutor executor, IPackageDownloadProof proof) =>
        {
            var packageId = HeaderId(http, "X-Svm-Package-Id"); Bind(http, packageId, "Gateway"); PackageEndpoints.Empty(http);
            if (http.Request.Headers.Authorization.Count > 0 && (http.Request.Cookies.ContainsKey(SessionEndpoints.CookieName) || proof.Instance is null))
                throw new RequestRejectedException(RequestFailure.CredentialInvalid);
            var node = Header(http, "X-Svm-Node-Id"); var peer = (PackagePeer)http.Items[HttpPackageIdentity.PeerItem]!;
            var method = Header(http, "X-Svm-Original-Method");
            if (method is not ("GET" or "HEAD") || node != peer.NodeId) throw new RequestRejectedException(RequestFailure.PermissionDenied);
            var requestId = HeaderId(http, "X-Svm-Request-Id"); var generation = HeaderId(http, "X-Svm-Worker-Generation");
            // Check the original caller before probing files or registering a repair intent.
            // HEAD performs the same current authorization without creating a download fact.
            try { await executor.SendAsync(new AuthorizeDownloadCommand(packageId, requestId, node, generation, true), http.RequestAborted); }
            catch (RequestRejectedException e) when (e.Failure == RequestFailure.NoHealthyReplica)
            {
                // This failure is reached only after current caller/release authorization.
                // Cached health may have expired while Worker was offline; probe actual files.
            }
            var p = await executor.SendAsync(new InspectReplicaQuery(packageId), http.RequestAborted);
            var facts = await files.InspectAsync(packageId, p.ExpectedSize, p.ExpectedSha256, http.RequestAborted);
            await executor.SendAsync(new CheckPackageReplicasCommand(packageId, facts), http.RequestAborted);
            var result = await executor.SendAsync(new AuthorizeDownloadCommand(packageId, requestId, node, generation, method == "HEAD"), http.RequestAborted);
            if (http.Request.Headers.ContainsKey("X-Svm-Local-Only") &&
                (Header(http, "X-Svm-Local-Only") != "1" || method != "HEAD" || result.Value.NodeId != files.NodeId))
                throw new RequestRejectedException(RequestFailure.NoHealthyReplica);
            http.Response.Headers["X-Svm-Replica-Uri"] = result.Value.InternalUri;
            http.Response.Headers["X-Svm-Download-Authorized"] = method == "GET" ? "1" : "0";
            http.Response.Headers["X-Svm-Sha256"] = result.Value.Sha256;
            return Results.StatusCode(204);
        }).AddEndpointFilter(async (context, next) =>
        {
            try { return await next(context); }
            catch (RequestRejectedException e) when (e.Failure is RequestFailure.ResourceNotFound or RequestFailure.InvalidState)
            {
                // auth_request accepts only 401/403 as denials. Do not turn a disabled or
                // inaccessible release into a gateway dependency failure.
                return Results.StatusCode(403);
            }
        });
        group.MapGet("/download-completions/{requestId:guid}", async (Guid requestId, HttpContext http, ScopedRequestExecutor executor) =>
        {
            Bind(http, requestId, "Collector");
            if (http.Request.Query.Keys.Any(k => k is not ("nodeId" or "workerGeneration")) || http.Request.Query.Any(q => q.Value.Count != 1)) throw Invalid();
            var node = InstanceCursor.Query(http, "nodeId") ?? throw Invalid(); var generation = InstanceCursor.QueryId(http, "workerGeneration") ?? throw Invalid();
            var end = await executor.SendAsync(new GetDownloadEndQuery(requestId, node, generation), http.RequestAborted);
            return end is null ? Results.NoContent() : Results.Json(end);
        });
        group.MapPost("/download-completions", async (HttpContext http, ScopedRequestExecutor executor) =>
        {
            if (!http.Request.HasJsonContentType() || http.Request.ContentLength > 8192) throw Invalid();
            JsonDocument doc;
            try { doc = await JsonDocument.ParseAsync(http.Request.Body, new JsonDocumentOptions { MaxDepth = 4 }, http.RequestAborted); }
            catch (JsonException) { throw Invalid(); }
            using (doc)
            {
                Fields(doc.RootElement, ["requestId", "nodeId", "workerGeneration", "endedAt", "bytesSent", "outcome"]); var b = doc.RootElement;
                var request = Id(b, "requestId"); Bind(http, request, "Collector");
                if (!b.TryGetProperty("endedAt", out var time) || !time.TryGetDateTimeOffset(out var ended) ||
                    !b.TryGetProperty("bytesSent", out var bytes) || !bytes.TryGetInt64(out var count)) throw Invalid();
                var end = new DownloadEnd(request, Text(b, "nodeId"), Id(b, "workerGeneration"), ended, count, Text(b, "outcome"));
                await executor.SendAsync(new RecordDownloadEndCommand(end), http.RequestAborted);
                return Results.Json(new { recorded = true });
            }
        });
    }
    private static void Bind(HttpContext http, Guid resource, string role)
    {
        if (http.Items[HttpPackageIdentity.PeerItem] is not PackagePeer peer || peer.Role != role) throw new RequestRejectedException(RequestFailure.PermissionDenied);
        http.Items[HttpPackageIdentity.ResourceItem] = resource;
    }
    private static async Task<PackageWorkAuthority> Lease(HttpContext http, ScopedRequestExecutor executor, Guid id, CancellationToken? cancellation = null)
    {
        if (http.Request.Query.Keys.Any(k => k is not ("workId" or "dispatch" or "leaseGeneration" or "leaseToken")) || http.Request.Query.Any(q => q.Value.Count != 1)) throw Invalid();
        var dispatch = Number(http, "dispatch"); var generation = Number(http, "leaseGeneration");
        var token = InstanceCursor.QueryId(http, "leaseToken") ?? throw Invalid();
        var work = await executor.SendAsync(new GetPackageAuthorityQuery(id), cancellation ?? http.RequestAborted);
        var peer = (PackagePeer)http.Items[HttpPackageIdentity.PeerItem]!;
        if (work.DispatchSequence != dispatch || work.LeaseGeneration != generation || work.LeaseToken != token ||
            work.LeaseNode != peer.NodeId || work.LeaseUntil <= DateTimeOffset.UtcNow || work.State != "Running") throw new RequestRejectedException(RequestFailure.InvalidState);
        return work;
    }
    private static long Number(HttpContext http, string key) => long.TryParse(InstanceCursor.Query(http, key), NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > 0 ? n : throw Invalid();
    private static string Header(HttpContext http, string key) => http.Request.Headers.TryGetValue(key, out var values) && values.Count == 1 && values[0] is { Length: > 0 and < 128 } value ? value : throw Invalid();
    private static Guid HeaderId(HttpContext http, string key) => Guid.TryParse(Header(http, key), out var id) && id != Guid.Empty ? id : throw Invalid();
    private static Guid Id(JsonElement b, string key) => Guid.TryParseExact(Text(b, key), "D", out var id) && id != Guid.Empty ? id : throw Invalid();
    private static RequestRejectedException Invalid() => new(RequestFailure.InvalidRequest);
}
