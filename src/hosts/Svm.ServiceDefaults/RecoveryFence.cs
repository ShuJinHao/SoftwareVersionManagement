using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Svm.Services.Contracts.Framework;

namespace Svm.ServiceDefaults;

/// <summary>Deployment-owned fence, independent of restored database state and personnel claims.</summary>
public sealed class RecoveryFence(string? directory)
{
    public static RecoveryFence Load() => new(Environment.GetEnvironmentVariable("SVM_RECOVERY_DIRECTORY"));
    public string Mode()
    {
        if (directory is null) return "Normal";
        try
        {
            if (!Path.IsPathFullyQualified(directory) || !Directory.Exists(directory) || new DirectoryInfo(directory).LinkTarget is not null) return "Isolated";
            var file = new FileInfo(Path.Combine(directory, "locked.json"));
            if (!file.Exists) return "Normal";
            if (file.Length > 16384 || file.LinkTarget is not null) return "Isolated";
            using var json = JsonDocument.Parse(File.ReadAllText(file.FullName));
            return json.RootElement.TryGetProperty("state", out var state) && state.GetString() == "Review" ? "Review" : "Isolated";
        }
        catch { return "Isolated"; }
    }
    public void RequireWorkerOpen()
    { if (Mode() != "Normal") throw new RequestRejectedException(RequestFailure.ConfigurationInvalid); }
    public bool Allows(string method, string path)
    {
        var mode = Mode(); if (mode == "Normal") return true; if (mode != "Review") return false;
        path = path.ToLowerInvariant();
        if (path.StartsWith("/internal/", StringComparison.Ordinal)) return false;
        if (method is "GET" or "HEAD")
            return (!path.StartsWith("/api/", StringComparison.Ordinal) || path == "/api/v1/session" ||
                path == "/api/v1/session/authorization" || path.StartsWith("/api/v1/manage/", StringComparison.Ordinal)) &&
                !path.Contains("download", StringComparison.Ordinal) && !path.Contains("/content", StringComparison.Ordinal);
        if (path == "/api/v1/session" && method is "POST" or "DELETE") return true;
        if (method != "POST") return false;
        if (path is "/api/v1/session/password" or "/api/v1/client/status-reports" or "/api/v1/client/report-streams") return true;
        return Regex.IsMatch(path, "^/api/v1/(manage/deployments/[0-9a-f-]{36}/pause|manage/tasks/[0-9a-f-]{36}/close-unknown|manage/(enrollment-grants|recovery-grants|credentials|plc-upload-credentials)/[0-9a-f-]{36}/revoke|client/attempts/[0-9a-f-]{36}/receipts)$", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));
    }
    public async Task InvokeAsync(HttpContext context, Func<Task> next)
    {
        if (Allows(context.Request.Method, context.Request.Path.Value ?? "/") || await DisablingPerson(context)) { await next(); return; }
        context.Response.StatusCode = 503; context.Response.Headers.CacheControl = "no-store";
        await context.Response.WriteAsJsonAsync(new { code = "DEPENDENCY_UNAVAILABLE", detail = "RECOVERY_REVIEW_REQUIRED",
            retryable = false, traceId = context.TraceIdentifier }, context.RequestAborted);
    }
    private async Task<bool> DisablingPerson(HttpContext context)
    {
        if (Mode() != "Review" || context.Request.Method != "PATCH" || !Regex.IsMatch(context.Request.Path.Value ?? "",
            "^/api/v1/manage/users/[0-9a-fA-F-]{36}/?$", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50))) return false;
        context.Request.EnableBuffering(8192, 8192);
        try
        {
            using var body = await JsonDocument.ParseAsync(context.Request.Body, new JsonDocumentOptions { MaxDepth = 4 }, context.RequestAborted);
            return body.RootElement.ValueKind == JsonValueKind.Object && body.RootElement.TryGetProperty("isEnabled", out var enabled) && enabled.ValueKind == JsonValueKind.False &&
                body.RootElement.EnumerateObject().All(p => p.Name is "isEnabled" or "displayName" or "reason" or "expectedRevision");
        }
        catch (Exception e) when (e is IOException or JsonException) { return false; }
        finally { context.Request.Body.Position = 0; }
    }
}
