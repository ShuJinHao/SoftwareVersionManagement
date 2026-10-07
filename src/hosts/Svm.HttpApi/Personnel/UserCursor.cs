using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Svm.Services.Contracts.Framework;
using Svm.Services.Contracts.Identity;

namespace Svm.HttpApi.Personnel;

internal sealed class UserCursor(IDataProtectionProvider protection, PersonnelManagementOptions options)
{
    private readonly IDataProtector _protector = protection.CreateProtector("svm.personnel.cursor.v1");
    private sealed record Payload(Guid SubjectId, string? Filter, bool? Enabled, int PageSize, UserPagePosition After, DateTimeOffset ExpiresAt);
    internal string Encode(Guid subjectId, UserListInput input, UserPagePosition position) => _protector.Protect(JsonSerializer.Serialize(
        new Payload(subjectId, input.EmployeeNo, input.IsEnabled, input.PageSize, position, DateTimeOffset.UtcNow.AddMinutes(options.CursorMinutes))));
    internal UserPagePosition? Decode(string? value, Guid subjectId, string? filter, bool? enabled, int pageSize)
    {
        if (value is null) return null;
        try
        {
            if (value.Length is < 1 or > 4096) throw new InvalidOperationException();
            var payload = JsonSerializer.Deserialize<Payload>(_protector.Unprotect(value)) ?? throw new InvalidOperationException();
            if (payload.SubjectId != subjectId || payload.Filter != filter || payload.Enabled != enabled || payload.PageSize != pageSize ||
                payload.ExpiresAt <= DateTimeOffset.UtcNow || payload.After is null || payload.After.Id == Guid.Empty)
                throw new InvalidOperationException();
            return payload.After;
        }
        catch (Exception error) when (error is CryptographicException or JsonException or InvalidOperationException or ArgumentException)
        { throw new RequestRejectedException(RequestFailure.InvalidRequest); }
    }
}
