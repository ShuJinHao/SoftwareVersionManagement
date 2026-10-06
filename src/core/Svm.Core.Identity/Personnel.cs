using Svm.SharedKernel.Domain;

namespace Svm.Core.Identity;

public sealed class UserAccount : AggregateRoot<StrongId<UserAccount>>
{
    public UserAccount(StrongId<UserAccount> id, string employeeNo, string displayName, string passwordHash) : base(id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(employeeNo);
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        ArgumentException.ThrowIfNullOrWhiteSpace(passwordHash);
        EmployeeNo = employeeNo; DisplayName = displayName; PasswordHash = passwordHash;
    }
    public string EmployeeNo { get; private set; }
    public string DisplayName { get; private set; }
    public string PasswordHash { get; private set; }
    public bool IsEnabled { get; private set; } = true;
    public bool MustChangePassword { get; private set; } = true;
    public void ChangePassword(string hash)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hash);
        PasswordHash = hash; MustChangePassword = false;
    }
    public override string ToString() => "UserAccount [redacted]";
}

public sealed class WebSession
{
    public Guid Id { get; init; }
    public Guid SubjectId { get; init; }
    public string SecretHash { get; init; } = "";
    public DateTimeOffset ExpiresAt { get; init; }
    public DateTimeOffset? RevokedAt { get; private set; }
    public void Revoke(DateTimeOffset now) => RevokedAt ??= now;
    public override string ToString() => "WebSession [redacted]";
}

public sealed record PersonnelPermission(Guid? SoftwareId, string Operation);

/// <summary>IAM-owned persistence; lock methods require the current unit-of-work transaction.</summary>
public interface IPersonnelRepository
{
    Task<DateTimeOffset> NowAsync(CancellationToken cancellationToken);
    Task<UserAccount?> FindAsync(string employeeNo, CancellationToken cancellationToken);
    Task<UserAccount?> GetAsync(Guid subjectId, CancellationToken cancellationToken);
    Task LockSubjectAsync(Guid subjectId, bool exclusive, CancellationToken cancellationToken);
    Task AdvanceRevisionAsync(Guid subjectId, CancellationToken cancellationToken);
    Task<WebSession?> SessionAsync(Guid sessionId, CancellationToken cancellationToken);
    Task<IReadOnlyList<PersonnelPermission>> PermissionsAsync(Guid subjectId, CancellationToken cancellationToken);
    void AddSession(WebSession session);
    Task RevokeSessionsAsync(Guid subjectId, DateTimeOffset now, CancellationToken cancellationToken);
    Task<int> LockRateLimitAsync(string accountKey, string addressKey, int accountLimit, int accountWindowSeconds,
        int addressLimit, int addressWindowSeconds, CancellationToken cancellationToken);
    Task RecordFailureAsync(string accountKey, string addressKey, CancellationToken cancellationToken);
    Task<bool> BeginSeedAsync(CancellationToken cancellationToken);
    void AddInitialAccount(UserAccount user, IReadOnlyList<string> globalPermissions);
    Task EnsurePermissionCatalogAsync(IReadOnlyList<(string Operation, bool Global)> permissions, CancellationToken cancellationToken);
}
