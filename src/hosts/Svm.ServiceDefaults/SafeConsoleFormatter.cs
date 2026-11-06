using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Console;

namespace Svm.ServiceDefaults;

/// <summary>Allowlisted structured diagnostics; never renders exception messages, URLs or payloads.</summary>
public sealed class SafeConsoleFormatter() : ConsoleFormatter("svm-safe")
{
    private static readonly HashSet<string> Ids = ["WorkId", "PackageId", "BindingId", "OperationId", "TaskId", "DeploymentId"];
    private static readonly HashSet<string> Codes = ["Code", "State", "Failure", "FailureType", "Method", "Role"];
    public static void Configure(ILoggingBuilder logging)
    {
        logging.ClearProviders();
        logging.AddConsole(o => o.FormatterName = "svm-safe").AddConsoleFormatter<SafeConsoleFormatter, ConsoleFormatterOptions>();
    }
    public override void Write<TState>(in LogEntry<TState> entry, IExternalScopeProvider? scopeProvider, TextWriter writer)
    {
        var fields = new Dictionary<string, object?> { ["at"] = DateTimeOffset.UtcNow, ["level"] = entry.LogLevel.ToString(),
            ["category"] = entry.Category, ["eventId"] = entry.EventId.Id,
            ["traceId"] = Activity.Current?.TraceId.ToString(), ["exceptionType"] = entry.Exception?.GetType().Name };
        if (entry.State is IEnumerable<KeyValuePair<string, object?>> values)
            foreach (var (key, value) in values)
            {
                if (Ids.Contains(key) && Guid.TryParse(value?.ToString(), out var id)) fields[key] = id;
                else if (Codes.Contains(key) && value?.ToString() is { Length: > 0 and <= 64 } code && code.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '.')) fields[key] = code;
                else if ((key is "StatusCode" or "ElapsedMs") && (value is int or long or double)) fields[key] = value;
                else if (key == "TraceId" && value?.ToString() is { Length: > 0 and <= 64 } trace && trace.All(c => char.IsAsciiLetterOrDigit(c) || c is ':' or '-')) fields[key] = trace;
            }
        writer.WriteLine(JsonSerializer.Serialize(fields));
    }
}
