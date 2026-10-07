using System.Globalization;
using System.Text.Json;
using MediatR;
using Microsoft.AspNetCore.Antiforgery;
using Svm.Services.Contracts.Framework;
using Svm.Services.Contracts.Identity;

namespace Svm.HttpApi.Personnel;

internal static class UserEndpoints
{
    internal static void MapPersonnelManagement(this WebApplication app)
    {
        var group = app.MapGroup("/api/v1/manage").WithMetadata(new PersonnelEndpointKind(RequestKind.Manage));
        group.AddEndpointFilter(async (context, next) =>
        {
            if (!context.HttpContext.Request.IsHttps) throw new RequestRejectedException(RequestFailure.PermissionDenied);
            return await next(context);
        });
        group.MapGet("/users", async (HttpContext http, ISender sender, ISessionProofSource proof, UserCursor cursor, PersonnelManagementOptions options) =>
        {
            if (http.Request.Query.Keys.Any(k => k is not ("employeeNo" or "isEnabled" or "pageSize" or "cursor")) || http.Request.Query.Any(p => p.Value.Count != 1))
                throw new RequestRejectedException(RequestFailure.InvalidRequest);
            var employeeNo = Query(http, "employeeNo");
            var enabledText = Query(http, "isEnabled");
            bool? enabled = enabledText is null ? null : enabledText is "true" ? true : enabledText is "false" ? false : throw new RequestRejectedException(RequestFailure.InvalidRequest);
            var sizeText = Query(http, "pageSize");
            var size = sizeText is null ? options.DefaultPageSize : int.TryParse(sizeText, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) ? parsed : throw new RequestRejectedException(RequestFailure.InvalidRequest);
            var subjectId = proof.Proof?.SubjectId ?? throw new RequestRejectedException(RequestFailure.AuthenticationRequired);
            var input = new UserListInput(employeeNo, enabled, size, cursor.Decode(Query(http, "cursor"), subjectId, employeeNo, enabled, size));
            var page = await sender.Send(new ListUsersQuery(input), http.RequestAborted);
            return Results.Json(new { items = page.Items, nextCursor = page.Next is null ? null : cursor.Encode(subjectId, input, page.Next), serverTime = DateTimeOffset.UtcNow });
        });
        group.MapGet("/users/{userId:guid}", async (Guid userId, HttpContext http, ISender sender) =>
            Results.Json(View(await sender.Send(new GetUserQuery(userId), http.RequestAborted))));
        group.MapPost("/users", async (HttpContext http, ISender sender, IAntiforgery antiforgery) =>
        {
            using var doc = await Input(http, antiforgery, ["employeeNo", "displayName", "temporaryPassword"]);
            var body = doc.RootElement;
            var result = await sender.Send(new CreateUserCommand(Key(http), Text(body, "employeeNo"), Text(body, "displayName").Trim(), Text(body, "temporaryPassword")), http.RequestAborted);
            return Results.Created("/api/v1/manage/users/" + result.Value.Id, View(result.Value));
        });
        group.MapPatch("/users/{userId:guid}", async (Guid userId, HttpContext http, ISender sender, IAntiforgery antiforgery) =>
        {
            using var doc = await Input(http, antiforgery, ["displayName", "isEnabled", "reason", "expectedRevision"]);
            var body = doc.RootElement;
            var result = await sender.Send(new UpdateUserCommand(Key(http), userId, Revision(body), OptionalText(body, "displayName")?.Trim(),
                OptionalBoolean(body, "isEnabled"), Text(body, "reason").Trim()), http.RequestAborted);
            return Results.Json(View(result.Value));
        });
        group.MapPost("/users/{userId:guid}/reset-password", async (Guid userId, HttpContext http, ISender sender, IAntiforgery antiforgery) =>
        {
            using var doc = await Input(http, antiforgery, ["temporaryPassword", "reason", "expectedRevision"]);
            var body = doc.RootElement;
            var result = await sender.Send(new ResetUserPasswordCommand(Key(http), userId, Revision(body), Text(body, "temporaryPassword"), Text(body, "reason").Trim()), http.RequestAborted);
            return Results.Json(View(result.Value));
        });
        group.MapPut("/subjects/{subjectId:guid}/permissions", async (Guid subjectId, HttpContext http, ISender sender, IAntiforgery antiforgery) =>
        {
            using var doc = await Input(http, antiforgery, ["permissions", "reason", "expectedRevision"]);
            var body = doc.RootElement;
            if (!body.TryGetProperty("permissions", out var array) || array.ValueKind != JsonValueKind.Array) throw new RequestRejectedException(RequestFailure.InvalidRequest);
            var permissions = new List<PermissionView>();
            foreach (var p in array.EnumerateArray())
            {
                Fields(p, ["softwareId", "operation"]);
                Guid? softwareId = null;
                if (p.TryGetProperty("softwareId", out var id) && id.ValueKind != JsonValueKind.Null)
                    softwareId = id.ValueKind == JsonValueKind.String && id.TryGetGuid(out var value) ? value : throw new RequestRejectedException(RequestFailure.InvalidRequest);
                permissions.Add(new(softwareId, Text(p, "operation")));
            }
            var result = await sender.Send(new ReplaceUserPermissionsCommand(Key(http), subjectId, Revision(body), permissions, Text(body, "reason").Trim()), http.RequestAborted);
            return Results.Json(View(result.Value));
        });
    }
    private static string? Query(HttpContext http, string name) => http.Request.Query.TryGetValue(name, out var value) && !string.IsNullOrEmpty(value[0]) ? value[0] : null;
    private static async Task<JsonDocument> Input(HttpContext http, IAntiforgery antiforgery, string[] fields)
    {
        if (!http.Request.IsHttps) throw new RequestRejectedException(RequestFailure.PermissionDenied);
        try { await antiforgery.ValidateRequestAsync(http); }
        catch (AntiforgeryValidationException) { throw new RequestRejectedException(RequestFailure.PermissionDenied); }
        if (!http.Request.HasJsonContentType()) throw new RequestRejectedException(RequestFailure.InvalidRequest);
        if (http.Request.ContentLength > 8192) throw new RequestRejectedException(RequestFailure.PayloadTooLarge);
        JsonDocument? doc = null;
        try
        {
            doc = await JsonDocument.ParseAsync(http.Request.Body, new JsonDocumentOptions { MaxDepth = 8 }, http.RequestAborted);
            Fields(doc.RootElement, fields); return doc;
        }
        catch (JsonException) { doc?.Dispose(); throw new RequestRejectedException(RequestFailure.InvalidRequest); }
        catch { doc?.Dispose(); throw; }
    }
    private static void Fields(JsonElement body, string[] fields)
    {
        if (body.ValueKind != JsonValueKind.Object) throw new RequestRejectedException(RequestFailure.InvalidRequest);
        var properties = body.EnumerateObject().ToArray();
        if (properties.Any(p => !fields.Contains(p.Name, StringComparer.Ordinal))) throw new RequestRejectedException(RequestFailure.UnknownField);
        if (properties.Select(p => p.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != properties.Length) throw new RequestRejectedException(RequestFailure.InvalidRequest);
    }
    private static string Text(JsonElement body, string field) => body.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.String
        ? value.GetString()! : throw new RequestRejectedException(RequestFailure.InvalidRequest);
    private static string? OptionalText(JsonElement body, string field) => !body.TryGetProperty(field, out var value) || value.ValueKind == JsonValueKind.Null
        ? null : value.ValueKind == JsonValueKind.String ? value.GetString() : throw new RequestRejectedException(RequestFailure.InvalidRequest);
    private static bool? OptionalBoolean(JsonElement body, string field) => !body.TryGetProperty(field, out var value) || value.ValueKind == JsonValueKind.Null ? null :
        value.ValueKind == JsonValueKind.True ? true : value.ValueKind == JsonValueKind.False ? false : throw new RequestRejectedException(RequestFailure.InvalidRequest);
    private static long Revision(JsonElement body) => !body.TryGetProperty("expectedRevision", out var value)
        ? throw new RequestRejectedException(RequestFailure.RevisionRequired) : value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var revision) ? revision : throw new RequestRejectedException(RequestFailure.InvalidRequest);
    private static Guid Key(HttpContext http) => http.Request.Headers.TryGetValue("Idempotency-Key", out var value) && value.Count == 1 && Guid.TryParseExact(value[0], "D", out var key) && key != Guid.Empty
        ? key : throw new RequestRejectedException(RequestFailure.InvalidRequest);
    private static object View(UserView user) => new { user.Id, user.EmployeeNo, user.DisplayName, user.IsEnabled, user.MustChangePassword, user.Permissions, user.Revision, serverTime = DateTimeOffset.UtcNow };
}
