using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Svm.Services.Contracts.Catalog;
using Svm.Services.Contracts.Identity;
using Svm.Services.Contracts.Instances;
using Svm.Services.Contracts.Framework;

namespace Svm.HttpApi.Tasks;

internal sealed record TaskPageRequest(int Size, Guid? After, Guid Subject, string IdentityRevision, string Route, string Filter);
internal sealed class TaskCursor(IDataProtectionProvider protection, SiteCatalogOptions site, IUserQueries users,
    ISessionProofSource people, IAccessProofSource machines, TimeProvider clock)
{
    private readonly IDataProtector _protector = protection.CreateProtector("svm.tasks.cursor.v1");
    private sealed record Payload(Guid Site, Guid Subject, string Revision, string Route, string Filter, int Size, Guid After, DateTimeOffset Expires);
    internal async Task<TaskPageRequest> Read(HttpContext h, string route, object filter, string[] allowed)
    {
        if (h.Request.Query.Keys.Any(k => !allowed.Contains(k, StringComparer.Ordinal) && k is not ("pageSize" or "cursor")) || h.Request.Query.Any(x => x.Value.Count != 1)) throw Invalid();
        Guid subject; string revision;
        if (machines.Proof is { Kind: ActorKind.Instance } credential)
        { subject = credential.Id; revision = credential.Id.ToString("D"); }
        else { subject = people.Proof?.SubjectId ?? throw new RequestRejectedException(RequestFailure.AuthenticationRequired); revision = (await users.GetAsync(subject, h.RequestAborted) ?? throw new RequestRejectedException(RequestFailure.AuthenticationRequired)).Revision.ToString(System.Globalization.CultureInfo.InvariantCulture); }
        var size = Query(h, "pageSize") is { } raw ? int.TryParse(raw, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var n) ? n : throw Invalid() : 50;
        if (size is < 1 or > 200) throw Invalid(); var serialized = JsonSerializer.Serialize(filter); Guid? after = null;
        if (Query(h, "cursor") is { } cursor)
        {
            try { if (cursor.Length > 4096) throw Invalid(); var p = JsonSerializer.Deserialize<Payload>(_protector.Unprotect(cursor)) ?? throw Invalid();
                if (p.Site != site.Require().SiteId || p.Subject != subject || p.Revision != revision || p.Route != route || p.Filter != serialized || p.Size != size || p.After == Guid.Empty || p.Expires <= clock.GetUtcNow()) throw Invalid(); after = p.After; }
            catch (Exception e) when (e is CryptographicException or JsonException or ArgumentException) { throw Invalid(); }
        }
        return new(size, after, subject, revision, route, serialized);
    }
    internal string? Encode(TaskPageRequest p, Guid? next) => next is null ? null : _protector.Protect(JsonSerializer.Serialize(new Payload(site.Require().SiteId, p.Subject, p.IdentityRevision, p.Route, p.Filter, p.Size, next.Value, clock.GetUtcNow().AddMinutes(site.CursorMinutes))));
    internal static string? Query(HttpContext h, string key) => h.Request.Query.TryGetValue(key, out var v) && v.Count == 1 && v[0]?.Length > 0 ? v[0] : null;
    internal static Guid? Id(HttpContext h, string key) => Query(h, key) is not { } v ? null : Guid.TryParseExact(v, "D", out var id) && id != Guid.Empty ? id : throw Invalid();
    private static RequestRejectedException Invalid() => new(RequestFailure.InvalidRequest);
}
