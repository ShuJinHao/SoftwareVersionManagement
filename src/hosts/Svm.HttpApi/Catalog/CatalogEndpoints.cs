using System.Text.Json;
using MediatR;
using Microsoft.AspNetCore.Antiforgery;
using Svm.HttpApi.Personnel;
using Svm.Services.Contracts.Catalog;
using Svm.Services.Contracts.Framework;
using static Svm.HttpApi.Personnel.UserEndpoints;

namespace Svm.HttpApi.Catalog;

internal static class CatalogEndpoints
{
    internal static void MapSiteCatalog(this WebApplication app)
    {
        var group = app.MapGroup("/api/v1/manage").WithMetadata(new PersonnelEndpointKind(RequestKind.Manage));
        group.AddEndpointFilter(async (context, next) =>
        {
            if (!context.HttpContext.Request.IsHttps) throw new RequestRejectedException(RequestFailure.PermissionDenied);
            context.HttpContext.Response.Headers.CacheControl = "no-store"; return await next(context);
        });
        group.MapGet("/site", async (HttpContext http, ISender sender) =>
        { EmptyQuery(http); var site = await sender.Send(new GetSiteQuery(), http.RequestAborted); return Results.Json(new { site.SiteId, site.SiteName, site.SiteTimeZone, serverTime = DateTimeOffset.UtcNow }); });
        group.MapGet("/processes", async (HttpContext http, ISender sender, CatalogCursor cursor) =>
        { var input = await cursor.ReadAsync(http, "processes", ["code", "name"], http.RequestAborted); var page = await sender.Send(new ListProcessesQuery(input.Input), http.RequestAborted); return Page(page, input, cursor); });
        group.MapGet("/processes/{processId:guid}", async (Guid processId, HttpContext http, ISender sender) =>
        { EmptyQuery(http); return Detail(await sender.Send(new GetProcessQuery(processId), http.RequestAborted)); });
        group.MapGet("/devices", async (HttpContext http, ISender sender, CatalogCursor cursor) =>
        { var input = await cursor.ReadAsync(http, "devices", ["processId", "deviceNo", "name"], http.RequestAborted); var page = await sender.Send(new ListDevicesQuery(input.Input), http.RequestAborted); return Page(page, input, cursor); });
        group.MapGet("/devices/{deviceId:guid}", async (Guid deviceId, HttpContext http, ISender sender) =>
        { EmptyQuery(http); return Detail(await sender.Send(new GetDeviceQuery(deviceId), http.RequestAborted)); });
        group.MapGet("/software", async (HttpContext http, ISender sender, CatalogCursor cursor) =>
        { var input = await cursor.ReadAsync(http, "software", ["code", "name", "category"], http.RequestAborted); var page = await sender.Send(new ListSoftwareQuery(input.Input), http.RequestAborted); return Page(page, input, cursor); });
        group.MapGet("/software/{softwareId:guid}", async (Guid softwareId, HttpContext http, ISender sender) =>
        { EmptyQuery(http); return Detail(await sender.Send(new GetSoftwareQuery(softwareId), http.RequestAborted)); });
        group.MapGet("/permission-options", async (HttpContext http, ISender sender, CatalogCursor cursor) =>
        {
            var input = await cursor.ReadAsync(http, "permission-options", ["code", "name", "category"], http.RequestAborted);
            var page = await sender.Send(new GetPermissionOptionsQuery(input.Input), http.RequestAborted);
            return Results.Json(new { items = page.Items.Select(s => new { s.Id, s.Code, s.Name, s.Category }), softwareOperations = page.SoftwareOperations,
                nextCursor = cursor.Encode(input, page.Next), serverTime = DateTimeOffset.UtcNow });
        });
        group.MapGet("/devices/{deviceId:guid}/software-bindings", async (Guid deviceId, HttpContext http, ISender sender, CatalogCursor cursor) =>
        {
            var input = await cursor.ReadAsync(http, "bindings/" + deviceId, [], http.RequestAborted);
            var page = await sender.Send(new ListBindingsQuery(deviceId, input.Input), http.RequestAborted);
            return Results.Json(new { items = page.Items.Select(Binding), nextCursor = cursor.Encode(input, page.Next), serverTime = DateTimeOffset.UtcNow });
        });
        group.MapGet("/devices/{deviceId:guid}/software-inventory", async (Guid deviceId, HttpContext http, ISender sender, CatalogCursor cursor) =>
        {
            var input = await cursor.ReadAsync(http, "inventory/" + deviceId, ["category", "softwareId"], http.RequestAborted);
            var page = await sender.Send(new GetDeviceInventoryQuery(deviceId, input.Input), http.RequestAborted);
            return Results.Json(new { items = page.Items.Select(x => new { software = x.Software, binding = Binding(x.Binding), instance = x.Instance }),
                nextCursor = cursor.Encode(input, page.Next), serverTime = DateTimeOffset.UtcNow });
        });
        group.MapPost("/software", async (HttpContext http, ISender sender, IAntiforgery antiforgery) =>
        {
            using var doc = await Input(http, antiforgery, ["code", "name", "category", "description"]); var b = doc.RootElement;
            var result = await sender.Send(new CreateSoftwareCommand(Key(http), Text(b, "code"), Text(b, "name"), Text(b, "category"), OptionalText(b, "description")), http.RequestAborted);
            return Results.Json(Software(result.Value), statusCode: 201);
        });
        group.MapPatch("/software/{softwareId:guid}", async (Guid softwareId, HttpContext http, ISender sender, IAntiforgery antiforgery) =>
        {
            using var doc = await Input(http, antiforgery, ["name", "description", "reason", "expectedRevision"]); var b = doc.RootElement;
            var result = await sender.Send(new UpdateSoftwareCommand(Key(http), softwareId, Revision(b), Text(b, "name"), OptionalText(b, "description"), Text(b, "reason")), http.RequestAborted);
            return Results.Json(Software(result.Value));
        });
        group.MapPost("/processes", async (HttpContext http, ISender sender, IAntiforgery antiforgery) =>
        {
            using var doc = await Input(http, antiforgery, ["code", "name"]); var b = doc.RootElement;
            var result = await sender.Send(new CreateProcessCommand(Key(http), Text(b, "code"), Text(b, "name")), http.RequestAborted);
            return Results.Json(Process(result.Value), statusCode: 201);
        });
        group.MapPatch("/processes/{processId:guid}", async (Guid processId, HttpContext http, ISender sender, IAntiforgery antiforgery) =>
        {
            using var doc = await Input(http, antiforgery, ["name", "reason", "expectedRevision"]); var b = doc.RootElement;
            var result = await sender.Send(new UpdateProcessCommand(Key(http), processId, Revision(b), Text(b, "name"), Text(b, "reason")), http.RequestAborted);
            return Results.Json(Process(result.Value));
        });
        group.MapPost("/devices", async (HttpContext http, ISender sender, IAntiforgery antiforgery) =>
        {
            using var doc = await Input(http, antiforgery, ["processId", "deviceNo", "name"]); var b = doc.RootElement;
            var result = await sender.Send(new CreateDeviceCommand(Key(http), Identifier(b, "processId"), Text(b, "deviceNo"), Text(b, "name")), http.RequestAborted);
            return Results.Json(Device(result.Value), statusCode: 201);
        });
        group.MapPatch("/devices/{deviceId:guid}", async (Guid deviceId, HttpContext http, ISender sender, IAntiforgery antiforgery) =>
        {
            using var doc = await Input(http, antiforgery, ["processId", "name", "reason", "expectedRevision"]); var b = doc.RootElement;
            var result = await sender.Send(new UpdateDeviceCommand(Key(http), deviceId, Revision(b), b.TryGetProperty("processId", out var p) && p.ValueKind != JsonValueKind.Null ? Identifier(b, "processId") : null,
                OptionalText(b, "name"), Text(b, "reason")), http.RequestAborted);
            return Results.Json(Device(result.Value));
        });
        group.MapPost("/devices/{deviceId:guid}/software-bindings", async (Guid deviceId, HttpContext http, ISender sender, IAntiforgery antiforgery) =>
        {
            using var doc = await Input(http, antiforgery, ["softwareId", "reason"]); var b = doc.RootElement;
            var result = await sender.Send(new CreateBindingCommand(Key(http), deviceId, Identifier(b, "softwareId"), Text(b, "reason")), http.RequestAborted);
            return Results.Json(BindingDetail(result.Value), statusCode: 201);
        });
        group.MapDelete("/devices/{deviceId:guid}/software-bindings/{softwareId:guid}", async (Guid deviceId, Guid softwareId, HttpContext http, ISender sender, IAntiforgery antiforgery) =>
        {
            using var doc = await Input(http, antiforgery, ["reason", "expectedRevision"]); var b = doc.RootElement;
            await sender.Send(new RevokeBindingCommand(Key(http), deviceId, softwareId, Revision(b), Text(b, "reason")), http.RequestAborted); return Results.NoContent();
        });
    }
    private static IResult Page<T>(CatalogPage<T> page, CatalogPageRequest input, CatalogCursor cursor) =>
        Results.Json(new { items = page.Items, nextCursor = cursor.Encode(input, page.Next), serverTime = DateTimeOffset.UtcNow });
    private static IResult Detail(SoftwareView value) => Results.Json(Software(value));
    private static IResult Detail(ProcessView value) => Results.Json(Process(value));
    private static IResult Detail(DeviceView value) => Results.Json(Device(value));
    private static object Software(SoftwareView x) => new { x.Id, x.Code, x.Name, x.Category, x.Description, x.LatestAvailableFormalReleaseId, x.Revision, serverTime = DateTimeOffset.UtcNow };
    private static object Process(ProcessView x) => new { x.Id, x.SiteId, x.Code, x.Name, x.Revision, serverTime = DateTimeOffset.UtcNow };
    private static object Device(DeviceView x) => new { x.Id, x.DeviceNo, x.Name, x.ProcessId, x.ProcessCode, x.ProcessName, x.SiteId, x.SiteName, x.Revision, serverTime = DateTimeOffset.UtcNow };
    private static object Binding(BindingView x) => new { x.DeviceId, x.SoftwareId, x.Revision };
    private static object BindingDetail(BindingView x) => new { x.DeviceId, x.SoftwareId, x.Revision, serverTime = DateTimeOffset.UtcNow };
    private static Guid Identifier(JsonElement body, string field) => Guid.TryParseExact(Text(body, field), "D", out var id) && id != Guid.Empty ? id : throw new RequestRejectedException(RequestFailure.InvalidRequest);
    private static void EmptyQuery(HttpContext http) { if (http.Request.Query.Count != 0) throw new RequestRejectedException(RequestFailure.InvalidRequest); }
}
