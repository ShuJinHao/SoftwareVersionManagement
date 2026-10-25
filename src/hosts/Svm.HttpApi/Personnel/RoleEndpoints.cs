using System.Text.Json;
using MediatR;
using Microsoft.AspNetCore.Antiforgery;
using Svm.HttpApi.Catalog;
using Svm.Services.Contracts.Catalog;
using Svm.Services.Contracts.Framework;
using Svm.Services.Contracts.Identity;

namespace Svm.HttpApi.Personnel;
internal static class RoleEndpoints
{
    internal static void MapRoles(this WebApplication app)
    {
        app.MapGet("/api/v1/session/authorization", async (HttpContext http, ISender sender) =>
        {
            RequireHttps(http); return Results.Json(await sender.Send(new GetMyAuthorizationQuery(), http.RequestAborted));
        }).WithMetadata(new PersonnelEndpointKind(RequestKind.Session));
        var group = app.MapGroup("/api/v1/manage").WithMetadata(new PersonnelEndpointKind(RequestKind.Manage));
        group.AddEndpointFilter(async (context, next) => { RequireHttps(context.HttpContext); return await next(context); });
        group.MapGet("/roles", async (HttpContext http, ISender sender, CatalogCursor cursor) =>
        {
            var page = await cursor.ReadAsync(http, "roles", ["name"], http.RequestAborted);
            var result = await sender.Send(new ListRolesQuery(page.Input.Filter.Name, page.Input.After?.Id, page.Input.PageSize), http.RequestAborted);
            return Results.Json(new { items = result.Items, nextCursor = cursor.Encode(page, result.Next is { } next ? new(next.ToString("D"), next) : null), serverTime = DateTimeOffset.UtcNow });
        });
        group.MapGet("/roles/{roleId:guid}", async (Guid roleId, HttpContext http, ISender sender) =>
            Results.Json(await sender.Send(new GetRoleQuery(roleId), http.RequestAborted)));
        group.MapPost("/roles", async (HttpContext http, ISender sender, IAntiforgery csrf) =>
        {
            using var doc = await UserEndpoints.Input(http, csrf, ["name", "operations", "reason"]); var b = doc.RootElement;
            var result = await sender.Send(new CreateRoleCommand(UserEndpoints.Key(http), UserEndpoints.Text(b, "name").Trim(), Operations(b), UserEndpoints.Text(b, "reason").Trim()), http.RequestAborted);
            return Results.Created("/api/v1/manage/roles/" + result.Value.Id, result.Value);
        });
        group.MapPatch("/roles/{roleId:guid}", async (Guid roleId, HttpContext http, ISender sender, IAntiforgery csrf) =>
        {
            using var doc = await UserEndpoints.Input(http, csrf, ["name", "operations", "isEnabled", "reason", "expectedRevision"]); var b = doc.RootElement;
            if (!b.TryGetProperty("isEnabled", out var enabled) || enabled.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw Invalid();
            var result = await sender.Send(new UpdateRoleCommand(UserEndpoints.Key(http), roleId, UserEndpoints.Revision(b), UserEndpoints.Text(b, "name").Trim(), enabled.GetBoolean(), Operations(b), UserEndpoints.Text(b, "reason").Trim()), http.RequestAborted);
            return Results.Json(result.Value);
        });
        group.MapGet("/subjects/{subjectId:guid}/authorization", async (Guid subjectId, HttpContext http, ISender sender) =>
            Results.Json(await sender.Send(new GetAuthorizationQuery(subjectId), http.RequestAborted)));
        group.MapPut("/subjects/{subjectId:guid}/authorization", async (Guid subjectId, HttpContext http, ISender sender, IAntiforgery csrf) =>
        {
            using var doc = await UserEndpoints.Input(http, csrf, ["assignments", "directPermissions", "reason", "expectedRevision"]); var b = doc.RootElement;
            var assignments = new List<RoleAssignmentInput>();
            foreach (var item in Array(b, "assignments"))
            {
                UserEndpoints.Fields(item, ["roleId", "scopes"]); var scopes = new List<AccessScope>();
                foreach (var s in Array(item, "scopes"))
                { UserEndpoints.Fields(s, ["kind", "resourceId"]); scopes.Add(new(UserEndpoints.Text(s, "kind"), Id(s, "resourceId", true))); }
                assignments.Add(new(Id(item, "roleId", false)!.Value, scopes));
            }
            var direct = new List<PermissionView>();
            foreach (var p in Array(b, "directPermissions"))
            { UserEndpoints.Fields(p, ["operation", "softwareId"]); direct.Add(new(Id(p, "softwareId", true), UserEndpoints.Text(p, "operation"))); }
            var result = await sender.Send(new ReplaceAuthorizationCommand(UserEndpoints.Key(http), subjectId, UserEndpoints.Revision(b), assignments, direct, UserEndpoints.Text(b, "reason").Trim()), http.RequestAborted);
            return Results.Json(result.Value);
        });
    }
    private static string[] Operations(JsonElement b) => Array(b, "operations").Select(x => x.ValueKind == JsonValueKind.String ? x.GetString()! : throw Invalid()).ToArray();
    private static JsonElement.ArrayEnumerator Array(JsonElement b, string field) => b.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.Array ? value.EnumerateArray() : throw Invalid();
    private static Guid? Id(JsonElement b, string field, bool optional)
    {
        if (!b.TryGetProperty(field, out var v) || v.ValueKind == JsonValueKind.Null) return optional ? null : throw Invalid();
        return v.ValueKind == JsonValueKind.String && v.TryGetGuid(out var id) && id != Guid.Empty ? id : throw Invalid();
    }
    private static RequestRejectedException Invalid() => new(RequestFailure.InvalidRequest);
    private static void RequireHttps(HttpContext http) { if (!http.Request.IsHttps) throw new RequestRejectedException(RequestFailure.PermissionDenied); }
}
