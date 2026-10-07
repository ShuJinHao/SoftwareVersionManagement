using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Svm.Services.Contracts.Catalog;
using Svm.Services.Contracts.Identity;
using Svm.Services.Contracts.Framework;

namespace Svm.HttpApi.Instances;

internal sealed record InstancePageRequest(int Size,Guid? After,Guid SubjectId,long Revision,string Route,string Filter);
internal sealed class InstanceCursor(IDataProtectionProvider protection,SiteCatalogOptions options,IUserQueries users,ISessionProofSource proof,TimeProvider clock)
{
    private readonly IDataProtector _protector=protection.CreateProtector("svm.instance-access.cursor.v1");
    private sealed record Payload(Guid SiteId,Guid SubjectId,long Revision,string Route,string Filter,int Size,Guid After,DateTimeOffset ExpiresAt);
    internal async Task<InstancePageRequest> ReadAsync(HttpContext http,string route,object filter,string[] allowed,CancellationToken token)
    {
        if(http.Request.Query.Keys.Any(k=>!allowed.Contains(k,StringComparer.Ordinal) && k is not ("pageSize" or "cursor")) || http.Request.Query.Any(p=>p.Value.Count!=1)) throw Invalid();
        var subject=proof.Proof?.SubjectId ?? throw new RequestRejectedException(RequestFailure.AuthenticationRequired);
        var user=await users.GetAsync(subject,token) ?? throw new RequestRejectedException(RequestFailure.AuthenticationRequired);
        var size=Query(http,"pageSize") is { } text ? int.TryParse(text,NumberStyles.None,CultureInfo.InvariantCulture,out var n)?n:throw Invalid():options.DefaultPageSize;
        if(size<1 || size>options.MaximumPageSize) throw Invalid();
        var serialized=JsonSerializer.Serialize(filter); Guid? after=null;
        if(Query(http,"cursor") is { } cursor)
        {
            try
            {
                if(cursor.Length>4096) throw new InvalidOperationException();
                var p=JsonSerializer.Deserialize<Payload>(_protector.Unprotect(cursor)) ?? throw new InvalidOperationException();
                if(p.SiteId!=options.Require().SiteId || p.SubjectId!=subject || p.Revision!=user.Revision || p.Route!=route || p.Filter!=serialized || p.Size!=size || p.After==Guid.Empty || p.ExpiresAt<=clock.GetUtcNow()) throw new InvalidOperationException();
                after=p.After;
            }
            catch(Exception e) when(e is CryptographicException or JsonException or InvalidOperationException or ArgumentException) { throw Invalid(); }
        }
        return new(size,after,subject,user.Revision,route,serialized);
    }
    internal string? Encode(InstancePageRequest x,Guid? next) => next is null?null:_protector.Protect(JsonSerializer.Serialize(new Payload(options.Require().SiteId,x.SubjectId,x.Revision,x.Route,x.Filter,x.Size,next.Value,clock.GetUtcNow().AddMinutes(options.CursorMinutes))));
    internal static string? Query(HttpContext http,string key) => http.Request.Query.TryGetValue(key,out var value) && value.Count==1 && !string.IsNullOrEmpty(value[0])?value[0]:null;
    internal static Guid? QueryId(HttpContext http,string key) => Query(http,key) is not { } text?null:Guid.TryParseExact(text,"D",out var id) && id!=Guid.Empty?id:throw Invalid();
    private static RequestRejectedException Invalid() => new(RequestFailure.InvalidRequest);
}
