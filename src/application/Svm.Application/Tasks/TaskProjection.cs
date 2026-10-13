using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Svm.Services.Contracts.Framework;
using Svm.Services.Contracts.Instances;
using Svm.Services.Contracts.Tasks;

namespace Svm.Application.Tasks;

internal static class TaskProjection
{
    internal static OperationField G(string n, Guid? x) => new(n, x is { } id ? OperationValue.Identifier(id) : OperationValue.Null);
    internal static OperationField S(string n, string? x) => new(n, x is null ? OperationValue.Null : OperationValue.Text(x));
    internal static OperationField N(string n, long x) => new(n, OperationValue.Integer(x));
    internal static OperationField B(string n, bool x) => new(n, OperationValue.Boolean(x));
    internal static OperationField T(string n, DateTimeOffset? x) => new(n, x is { } at ? OperationValue.Timestamp(at) : OperationValue.Null);
    internal static OperationValue Window(TaskWindow? x) => x is null ? OperationValue.Null : OperationValue.Object(T("notBefore", x.NotBefore), T("latestStart", x.LatestStart));
    internal static OperationValue Filter(InstanceFilter? x) => x is null ? OperationValue.Null : OperationValue.Object(G("softwareId", x.SoftwareId), G("processId", x.ProcessId), G("deviceId", x.DeviceId), S("deviceNo", x.DeviceNo), S("reportedIp", x.ReportedIp), G("installedReleaseId", x.InstalledReleaseId), S("freshness", x.Freshness), S("runningState", x.RunningState), S("lifecycle", x.Lifecycle));
    internal static OperationValue Report(StateReport x) => OperationValue.Object(N("streamEpoch", x.StreamEpoch), N("reportSeq", x.ReportSeq), T("reportedAt", x.ReportedAt), S("installationState", x.InstallationState), G("installedReleaseId", x.InstalledReleaseId), S("installedVersion", x.InstalledVersion), T("installedAt", x.InstalledAt), S("runningState", x.RunningState), new("reportedIps", OperationValue.Array(x.ReportedIps.Select(OperationValue.Text).ToArray())), new("databaseState", OperationValue.Object(S("mode", x.DatabaseState.Mode), new("items", OperationValue.Array(x.DatabaseState.Items.Select(i => OperationValue.Object(S("databaseKey", i.DatabaseKey), S("schemaId", i.SchemaId))).ToArray())))));
    internal static OperationValue Strings(IReadOnlyList<string> x) => OperationValue.Array(x.Select(OperationValue.Text).ToArray());
    internal static string ChunkDigest(IReadOnlyList<Guid> ids) => Hash(string.Join(',', ids.Distinct().Order()));
    internal static string ReceiptDigest(ReceiptInput x) => Hash(JsonSerializer.Serialize(x with { StateReport = x.StateReport is { } r ? r with { ReportedAt = r.ReportedAt.ToUniversalTime(), InstalledAt = r.InstalledAt?.ToUniversalTime() } : null }));
    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
}
