using Svm.Services.Contracts.Framework;

namespace Svm.Services.Contracts.Identity;

public sealed record PersonnelPolicy(int SessionHours, int MinimumPasswordLength, int MaximumPasswordLength,
    int HashIterations, int AccountFailures, int AccountWindowSeconds, int AddressFailures, int AddressWindowSeconds)
{
    public void Validate()
    {
        if (SessionHours is < 1 or > 24 || MinimumPasswordLength < 15 || MaximumPasswordLength < MinimumPasswordLength ||
            MaximumPasswordLength > 128 || HashIterations < 600_000 || HashIterations > 2_000_000 ||
            AccountFailures is < 1 or > 100 || AddressFailures is < 1 or > 1000 ||
            AccountWindowSeconds is < 1 or > 86400 || AddressWindowSeconds is < 1 or > 86400)
            throw new RequestRejectedException(RequestFailure.ConfigurationInvalid);
    }
}

public sealed record PermissionView(Guid? SoftwareId, string Operation);
public sealed record PersonnelView(Guid SubjectId, string EmployeeNo, string DisplayName, bool MustChangePassword,
    IReadOnlyList<PermissionView> Permissions);

// Secrets deliberately use classes with redacted diagnostics, never positional record ToString().
public sealed class SessionProof(Guid sessionId, Guid subjectId, string secret)
{
    public Guid SessionId { get; } = sessionId;
    public Guid SubjectId { get; } = subjectId;
    public string Secret { get; } = secret;
    public override string ToString() => "SessionProof [redacted]";
}
public interface ISessionProofSource
{
    SessionProof? Proof { get; }
    string SourceAddress { get; }
}
public sealed class SessionGrant(SessionProof proof, PersonnelView person, DateTimeOffset expiresAt)
{
    public SessionProof Proof { get; } = proof;
    public PersonnelView Person { get; } = person;
    public DateTimeOffset ExpiresAt { get; } = expiresAt;
    public override string ToString() => "SessionGrant [redacted]";
}
public sealed class PersonnelMutation
{
    public SessionGrant? Grant { get; init; }
    public PersonnelView? Person { get; init; }
    public RequestFailure? Failure { get; init; }
    public int RetryAfterSeconds { get; init; }
    public bool Changed { get; init; }
    public override string ToString() => "PersonnelMutation [redacted]";
}
public interface IPersonnelCrypto
{
    string DummyPasswordHash { get; }
    string HashPassword(string password);
    bool VerifyPassword(string hash, string password);
    string CreateSecret();
    string HashSecret(string secret);
    bool VerifySecret(string hash, string secret);
}
public interface IPersonnelService
{
    Task<PersonnelView?> AuthenticateAsync(SessionProof proof, bool protect, CancellationToken cancellationToken);
    Task<PersonnelMutation> LoginAsync(string employeeNo, string password, string address, CancellationToken cancellationToken);
    Task<PersonnelMutation> LogoutAsync(SessionProof proof, CancellationToken cancellationToken);
    Task<PersonnelMutation> ChangePasswordAsync(SessionProof proof, string currentPassword, string newPassword, string address, CancellationToken cancellationToken);
    Task<PersonnelMutation> SeedAsync(string employeeNo, string displayName, string password, CancellationToken cancellationToken);
}

public static class PermissionCatalog
{
    public static IReadOnlyList<(string Operation, bool Global)> Entries { get; } = Array.AsReadOnly(new[]
    {
        ("identity.manage", true), ("software.create", true), ("software.read", false),
        ("release.upload", false), ("release.publish", false), ("release.disable", false),
        ("instance.read", false), ("instance.manage", false), ("enrollment.manage", false),
        ("deployment.create", false), ("deployment.control", false), ("deployment.rollback", false),
        ("task.closeUnknown", false), ("package.clean", false), ("audit.read", false)
    });
}
