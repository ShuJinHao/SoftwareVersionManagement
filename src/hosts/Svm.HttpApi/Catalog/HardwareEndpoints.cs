using System.Text.Json;
using MediatR;
using Microsoft.AspNetCore.Antiforgery;
using Svm.Services.Contracts.Catalog;
using Svm.Services.Contracts.Framework;
using static Svm.HttpApi.Personnel.UserEndpoints;

namespace Svm.HttpApi.Catalog;

internal static class HardwareEndpoints
{
    internal static void Map(RouteGroupBuilder group)
    {
        group.MapGet("/devices/{deviceId:guid}/hardware", async (Guid deviceId, HttpContext h, ISender s, HardwareCursor c) =>
        {
            var input = await c.ReadAsync(h, "hardware/" + deviceId, ["name"], h.RequestAborted);
            var page = await s.Send(new ListHardwareQuery(deviceId, input.Input), h.RequestAborted);
            return Results.Json(new { items = page.Items, nextCursor = c.Encode(input, page.Next), serverTime = DateTimeOffset.UtcNow });
        });
        group.MapGet("/hardware/{hardwareId:guid}", async (Guid hardwareId, HttpContext h, ISender s) =>
        { Empty(h); return Results.Json(await s.Send(new GetHardwareQuery(hardwareId), h.RequestAborted)); });
        group.MapPost("/devices/{deviceId:guid}/hardware", async (Guid deviceId, HttpContext h, ISender s, IAntiforgery a) =>
        {
            using var doc = await Input(h, a, ["type", "name", "assetNumber", "managementIp"]); var b = doc.RootElement;
            var result = await s.Send(new CreateHardwareCommand(Key(h), deviceId, Text(b, "type"), Text(b, "name"), OptionalText(b, "assetNumber"), OptionalText(b, "managementIp")), h.RequestAborted);
            return Results.Json(result.Value, statusCode: 201);
        });
        group.MapPatch("/hardware/{hardwareId:guid}", async (Guid hardwareId, HttpContext h, ISender s, IAntiforgery a) =>
        {
            using var doc = await Input(h, a, ["name", "assetNumber", "managementIp", "reason", "expectedRevision"]); var b = doc.RootElement;
            return Results.Json((await s.Send(new UpdateHardwareCommand(Key(h), hardwareId, Revision(b), Text(b, "name"), OptionalText(b, "assetNumber"), OptionalText(b, "managementIp"), Text(b, "reason")), h.RequestAborted)).Value);
        });
        group.MapGet("/hardware/{hardwareId:guid}/software-bindings", async (Guid hardwareId, HttpContext h, ISender s, HardwareCursor c) =>
        {
            var input = await c.ReadAsync(h, "hardware-bindings/" + hardwareId, [], h.RequestAborted);
            var page = await s.Send(new ListHardwareBindingsQuery(hardwareId, input.Input), h.RequestAborted);
            return Results.Json(new { items = page.Items, nextCursor = c.Encode(input, page.Next), serverTime = DateTimeOffset.UtcNow });
        });
        group.MapPost("/hardware/{hardwareId:guid}/software-bindings", async (Guid hardwareId, HttpContext h, ISender s, IAntiforgery a) =>
        {
            using var doc = await Input(h, a, ["softwareId", "newSoftware", "reason"]); var b = doc.RootElement;
            NewHardwareSoftware? draft = null;
            if (b.TryGetProperty("newSoftware", out var n) && n.ValueKind != JsonValueKind.Null)
            {
                if (n.ValueKind != JsonValueKind.Object || n.EnumerateObject().Any(p => p.Name is not ("name" or "category" or "description")) ||
                    n.EnumerateObject().Select(p => p.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != n.EnumerateObject().Count()) throw Invalid();
                draft = new(Text(n, "name"), Text(n, "category"), OptionalText(n, "description"));
            }
            var result = await s.Send(new CreateHardwareBindingCommand(Key(h), hardwareId,
                b.TryGetProperty("softwareId", out var sid) && sid.ValueKind != JsonValueKind.Null ? Id(b, "softwareId") : null, draft, Text(b, "reason")), h.RequestAborted);
            return Results.Json(result.Value, statusCode: 201);
        });
        group.MapDelete("/hardware/{hardwareId:guid}/software-bindings/{softwareId:guid}", async (Guid hardwareId, Guid softwareId, HttpContext h, ISender s, IAntiforgery a) =>
        {
            using var doc = await Input(h, a, ["expectedRevision", "reason"]); var b = doc.RootElement;
            await s.Send(new RevokeHardwareBindingCommand(Key(h), hardwareId, softwareId, Revision(b), Text(b, "reason")), h.RequestAborted);
            return Results.NoContent();
        });
        group.MapPut("/instances/{instanceId:guid}/hardware-binding", async (Guid instanceId, HttpContext h, ISender s, IAntiforgery a) =>
        {
            using var doc = await Input(h, a, ["hardwareBindingId", "expectedBindingRevision", "expectedRevision", "reason"]); var b = doc.RootElement;
            if (!b.TryGetProperty("expectedBindingRevision", out var v) || !v.TryGetInt64(out var revision)) throw Invalid();
            return Results.Json((await s.Send(new BindInstanceHardwareCommand(Key(h), instanceId, Id(b, "hardwareBindingId"), Revision(b), revision, Text(b, "reason")), h.RequestAborted)).Value);
        });
        group.MapGet("/workspace/devices", async (HttpContext h, ISender s, HardwareCursor c) =>
        {
            var input = await c.ReadAsync(h, "workspace/devices", ["processId", "deviceId", "hardwareId", "softwareId", "name", "category"], h.RequestAborted);
            var page = await s.Send(new GetWorkspaceDevicesQuery(input.Input), h.RequestAborted);
            return Results.Json(new { items = page.Items, totalCount = page.TotalCount, nextCursor = c.Encode(input, page.Next), serverTime = DateTimeOffset.UtcNow });
        });
        group.MapGet("/workspace/installations", async (HttpContext h, ISender s, HardwareCursor c) =>
        {
            var input = await c.ReadAsync(h, "workspace/installations", ["processId", "deviceId", "hardwareId", "softwareId", "name", "category"], h.RequestAborted);
            var page = await s.Send(new GetWorkspaceInstallationsQuery(input.Input), h.RequestAborted);
            return Results.Json(new { items = page.Items, totalCount = page.TotalCount, nextCursor = c.Encode(input, page.Next), serverTime = DateTimeOffset.UtcNow });
        });
    }
    private static Guid Id(JsonElement b, string name) => Guid.TryParseExact(Text(b, name), "D", out var id) && id != Guid.Empty ? id : throw Invalid();
    private static void Empty(HttpContext h) { if (h.Request.Query.Count != 0) throw Invalid(); }
    private static RequestRejectedException Invalid() => new(RequestFailure.InvalidRequest);
}
