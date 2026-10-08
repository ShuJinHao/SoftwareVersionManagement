using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Svm.HttpApi.Instances;
using Svm.Services.Contracts.Catalog;
using Svm.Services.Contracts.Framework;
using Svm.Services.Contracts.Identity;
using Svm.Services.Contracts.Instances;
using Svm.Services.Contracts.Packages;

namespace Svm.HttpApi.Packages;

internal sealed record PackagePageRequest(ReleaseListInput Input, Guid SubjectId, string Revision, string Route);
internal sealed class PackageCursor(IDataProtectionProvider protection, SiteCatalogOptions site, IUserQueries users,
    ICallContext calls, IAccessProofSource proofs, IInstanceAccess access, IManagedInstances instances, TimeProvider clock)
{
    private readonly IDataProtector _protector = protection.CreateProtector("svm.releases.cursor.v1");
    private sealed record Payload(Guid SiteId, Guid SubjectId, string Revision, string Route, ReleaseListInput Filter, ReleasePosition After, DateTimeOffset ExpiresAt);
    internal async Task<PackagePageRequest> ReadAsync(HttpContext http, Guid softwareId, string route, bool client, CancellationToken token)
    {
        if (http.Request.Query.Keys.Any(k => k is not ("state" or "channel" or "pageSize" or "cursor")) || http.Request.Query.Any(p => p.Value.Count != 1) ||
            client && http.Request.Query.ContainsKey("state")) throw Invalid();
        var call = calls.Current ?? throw new RequestRejectedException(RequestFailure.AuthenticationRequired);
        var subject = call.Actor.ActorId!.Value; string revision;
        if (client)
        {
            var credential = await access.CredentialAsync(proofs.Proof!.Id, token);
            var instance = await instances.GetIdentityAsync(call.Actor.InstanceId!.Value, false, token) ?? throw Invalid();
            revision = $"{credential.Id:D}:{credential.Revision}:{instance.Revision}";
        }
        else revision = (await users.GetAsync(subject, token) ?? throw Invalid()).Revision.ToString(CultureInfo.InvariantCulture);
        var size = InstanceCursor.Query(http, "pageSize") is { } text ? int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : throw Invalid() : 50;
        if (size is < 1 or > 200) throw Invalid();
        var filter = new ReleaseListInput(softwareId, InstanceCursor.Query(http, "state"), InstanceCursor.Query(http, "channel") ?? (client ? "Formal" : null), size);
        ReleasePosition? after = null;
        if (InstanceCursor.Query(http, "cursor") is { } value)
        {
            try
            {
                if (value.Length > 4096) throw new InvalidOperationException();
                var p = JsonSerializer.Deserialize<Payload>(_protector.Unprotect(value)) ?? throw new InvalidOperationException();
                if (p.SiteId != site.Require().SiteId || p.SubjectId != subject || p.Revision != revision || p.Route != route ||
                    p.Filter != filter || p.After.Id == Guid.Empty || p.ExpiresAt <= clock.GetUtcNow()) throw new InvalidOperationException();
                after = p.After;
            }
            catch (Exception e) when (e is JsonException or CryptographicException or InvalidOperationException or ArgumentException) { throw Invalid(); }
        }
        return new(filter with { After = after }, subject, revision, route);
    }
    internal string? Encode(PackagePageRequest r, ReleasePosition? after) => after is null ? null : _protector.Protect(JsonSerializer.Serialize(
        new Payload(site.Require().SiteId, r.SubjectId, r.Revision, r.Route, r.Input with { After = null }, after, clock.GetUtcNow().AddMinutes(site.CursorMinutes))));
    private static RequestRejectedException Invalid() => new(RequestFailure.InvalidRequest);
}
