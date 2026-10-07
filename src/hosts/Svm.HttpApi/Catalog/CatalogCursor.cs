using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Svm.Services.Contracts.Catalog;
using Svm.Services.Contracts.Identity;
using Svm.Services.Contracts.Framework;

namespace Svm.HttpApi.Catalog;

internal sealed record CatalogPageRequest(CatalogListInput Input, Guid SubjectId, long PermissionRevision, string Route);
internal sealed class CatalogCursor(IDataProtectionProvider protection, SiteCatalogOptions options, IUserQueries users, ISessionProofSource proof)
{
    private readonly IDataProtector _protector = protection.CreateProtector("svm.site-catalog.cursor.v1");
    private sealed record Payload(Guid SiteId, Guid SubjectId, long Revision, string Route, CatalogFilter Filter, int PageSize,
        CatalogPosition After, DateTimeOffset ExpiresAt);
    internal async Task<CatalogPageRequest> ReadAsync(HttpContext http, string route, string[] filters, CancellationToken token)
    {
        if (http.Request.Query.Keys.Any(k => !filters.Contains(k, StringComparer.Ordinal) && k is not ("pageSize" or "cursor")) ||
            http.Request.Query.Any(p => p.Value.Count != 1)) throw new RequestRejectedException(RequestFailure.InvalidRequest);
        var subject = proof.Proof?.SubjectId ?? throw new RequestRejectedException(RequestFailure.AuthenticationRequired);
        var user = await users.GetAsync(subject, token) ?? throw new RequestRejectedException(RequestFailure.AuthenticationRequired);
        var size = Query(http, "pageSize") is { } pageSize ? int.TryParse(pageSize, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n :
            throw new RequestRejectedException(RequestFailure.InvalidRequest) : options.DefaultPageSize;
        var filter = new CatalogFilter(Query(http, "code") ?? Query(http, "deviceNo"), Query(http, "name"), Query(http, "category"), QueryId(http, "processId"), QueryId(http, "softwareId"));
        CatalogPosition? after = null;
        if (Query(http, "cursor") is { } value)
        {
            try
            {
                if (value.Length is < 1 or > 4096) throw new InvalidOperationException();
                var payload = JsonSerializer.Deserialize<Payload>(_protector.Unprotect(value)) ?? throw new InvalidOperationException();
                if (payload.SiteId != options.Require().SiteId || payload.SubjectId != subject || payload.Revision != user.Revision || payload.Route != route ||
                    payload.Filter != filter || payload.PageSize != size || payload.After is null || payload.After.Id == Guid.Empty || payload.ExpiresAt <= DateTimeOffset.UtcNow)
                    throw new InvalidOperationException();
                after = payload.After;
            }
            catch (Exception e) when (e is CryptographicException or JsonException or InvalidOperationException or ArgumentException)
            { throw new RequestRejectedException(RequestFailure.InvalidRequest); }
        }
        return new(new(filter, size, after), subject, user.Revision, route);
    }
    internal string? Encode(CatalogPageRequest request, CatalogPosition? next) => next is null ? null : _protector.Protect(JsonSerializer.Serialize(
        new Payload(options.Require().SiteId, request.SubjectId, request.PermissionRevision, request.Route, request.Input.Filter,
            request.Input.PageSize, next, DateTimeOffset.UtcNow.AddMinutes(options.CursorMinutes))));
    private static string? Query(HttpContext http, string name) => http.Request.Query.TryGetValue(name, out var value) && !string.IsNullOrEmpty(value[0]) ? value[0] : null;
    private static Guid? QueryId(HttpContext http, string name) => Query(http, name) is not { } text ? null :
        Guid.TryParseExact(text, "D", out var id) && id != Guid.Empty ? id : throw new RequestRejectedException(RequestFailure.InvalidRequest);
}
