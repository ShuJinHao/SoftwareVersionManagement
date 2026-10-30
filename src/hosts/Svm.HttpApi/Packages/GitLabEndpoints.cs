using MediatR;
using Microsoft.AspNetCore.Antiforgery;
using Svm.HttpApi.Personnel;
using Svm.Services.Contracts.Framework;
using Svm.Services.Contracts.Releases;
using static Svm.HttpApi.Personnel.UserEndpoints;

namespace Svm.HttpApi.Packages;

internal static class GitLabEndpoints
{
    internal static void MapGitLabReleases(this WebApplication app, bool enabled)
    {
        var group = app.MapGroup("/api/v1/manage").WithMetadata(new PersonnelEndpointKind(RequestKind.Manage));
        group.AddEndpointFilter(async (context, next) => { PackageEndpoints.Require(context.HttpContext, enabled); return await next(context); });
        group.MapGet("/software/{softwareId:guid}/release-source", async (Guid softwareId, HttpContext http, ISender sender) =>
        { PackageEndpoints.Empty(http); return Results.Json(await sender.Send(new GetReleaseSourceQuery(softwareId), http.RequestAborted)); });
        group.MapGet("/software/{softwareId:guid}/release-source-options", async (Guid softwareId, HttpContext http, ISender sender) =>
        {
            if (http.Request.Query.Keys.Any(k => k is not ("search" or "page" or "pageSize"))) throw new Svm.Services.Contracts.Framework.RequestRejectedException(Svm.Services.Contracts.Framework.RequestFailure.UnknownField);
            if (http.Request.Query["search"].Count > 1) throw new RequestRejectedException(RequestFailure.InvalidRequest);
            var page = Page(http, "page", 1); var size = Page(http, "pageSize", 50);
            return Results.Json(await sender.Send(new GetReleaseSourceOptionsQuery(softwareId, http.Request.Query["search"].FirstOrDefault(), page, size), http.RequestAborted));
        });
        group.MapPut("/software/{softwareId:guid}/release-source", async (Guid softwareId, HttpContext http, ISender sender, IAntiforgery csrf) =>
        {
            using var doc = await Input(http, csrf, ["connectionId", "projectId", "packageName", "enabled", "reason", "expectedRevision"]);
            var body = doc.RootElement;
            if (!Guid.TryParse(Text(body, "connectionId"), out var connectionId) ||
                !body.TryGetProperty("projectId", out var project) || project.ValueKind != System.Text.Json.JsonValueKind.Number || !project.TryGetInt64(out var projectId) ||
                !body.TryGetProperty("expectedRevision", out var revision) || revision.ValueKind != System.Text.Json.JsonValueKind.Number || !revision.TryGetInt64(out var expectedRevision) ||
                !body.TryGetProperty("enabled", out var enabled) || enabled.ValueKind is not (System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False)) throw new RequestRejectedException(RequestFailure.InvalidRequest);
            var result = await sender.Send(new ChangeReleaseSourceCommand(Key(http), softwareId, connectionId, projectId,
                Text(body, "packageName"), enabled.GetBoolean(), expectedRevision, Text(body, "reason")), http.RequestAborted);
            return Results.Json(result.Value);
        });
        group.MapPost("/software/{softwareId:guid}/release-sync", async (Guid softwareId, HttpContext http, ISender sender, IAntiforgery csrf) =>
        {
            using var doc = await Input(http, csrf, ["tag"]);
            var result = await sender.Send(new RequestReleaseSyncCommand(Key(http), softwareId, OptionalText(doc.RootElement, "tag")), http.RequestAborted);
            http.Response.Headers.Location = $"/api/v1/manage/release-sync-work/{result.Value.Id:D}";
            return Results.Json(result.Value, statusCode: 202);
        });
        group.MapGet("/release-sync-work/{workId:guid}", async (Guid workId, HttpContext http, ISender sender) =>
        { PackageEndpoints.Empty(http); return Results.Json(await sender.Send(new GetReleaseSyncWorkQuery(workId), http.RequestAborted)); });
    }
    private static int Page(HttpContext http, string key, int fallback)
    {
        var values = http.Request.Query[key];
        if (values.Count == 0) return fallback;
        if (values.Count != 1 || !int.TryParse(values[0], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var value)) throw new RequestRejectedException(RequestFailure.InvalidRequest);
        return value;
    }

}
