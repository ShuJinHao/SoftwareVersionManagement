using Svm.Services.Contracts.Framework;

namespace Svm.HttpApi.Personnel;

internal static class SessionErrors
{
    internal static async Task Handle(HttpContext context, Func<Task> next)
    {
        context.Response.Headers.CacheControl = "no-store";
        try { await next(); }
        catch (RequestRejectedException error) { await Write(context, error.Code, error.StatusCode, error.StatusCode == 429, error.Errors); }
        catch (BadHttpRequestException) { await Write(context, "INVALID_REQUEST", 400, false); }
        catch (PersistenceException error)
        {
            context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("Svm.Personnel")
                .LogWarning("Personnel persistence failure {Failure}; operation {OperationId}; trace {TraceId}",
                    error.Failure, error.OperationId, context.TraceIdentifier);
            var code = error.Failure == PersistenceFailure.ConfigurationInvalid ? "CONFIGURATION_INVALID" : "DEPENDENCY_UNAVAILABLE";
            await Write(context, code, 503, error.Failure != PersistenceFailure.CommitOutcomeUnknown);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { context.Abort(); }
        catch (Exception error)
        {
            // Never pass exception/body/claims to logs; database/key-ring failures may embed sensitive diagnostics.
            context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("Svm.Personnel")
                .LogError("Personnel dependency failure {FailureType}; trace {TraceId}", error.GetType().Name, context.TraceIdentifier);
            await Write(context, "DEPENDENCY_UNAVAILABLE", 503, false);
        }
    }
    private static async Task Write(HttpContext context, string code, int status, bool retryable, IReadOnlyList<ValidationIssue>? errors = null)
    {
        if (context.Response.HasStarted) { context.Abort(); return; }
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/problem+json";
        if (status is 429 or 503 && !context.Response.Headers.ContainsKey("Retry-After")) context.Response.Headers.RetryAfter = "5";
        await context.Response.WriteAsJsonAsync(new { type = "about:blank", title = code, status, detail = code,
            code, traceId = context.TraceIdentifier, retryable, errors }, options: null, contentType: "application/problem+json", cancellationToken: context.RequestAborted);
    }
}
