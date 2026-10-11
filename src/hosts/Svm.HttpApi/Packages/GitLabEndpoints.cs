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
}
