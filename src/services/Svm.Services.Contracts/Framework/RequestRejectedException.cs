namespace Svm.Services.Contracts.Framework;

public enum RequestFailure
{
    AuthenticationRequired = 1, CredentialInvalid, PermissionDenied, HumanRequired,
    ResourceNotFound, InstanceSuspended, ValidationFailed, ConfigurationInvalid, DependencyUnavailable,
    GrantExpired, GrantExhausted, GrantRevoked, RateLimited, InvalidRequest, UnknownField, IdempotencyConflict
}

public sealed record ValidationIssue(string Field, string Code, string Message);

/// <summary>Error codes are the existing detailed-design 8.7 contract.</summary>
public sealed class RequestRejectedException : Exception
{
    public RequestRejectedException(RequestFailure failure, IEnumerable<ValidationIssue>? errors = null)
        : base(GetCode(failure))
    {
        Failure = failure;
        Errors = Array.AsReadOnly((errors ?? []).ToArray());
    }

    public RequestFailure Failure { get; }
    public string Code => GetCode(Failure);
    public IReadOnlyList<ValidationIssue> Errors { get; }
    public int StatusCode => Failure switch
    {
        RequestFailure.InvalidRequest or RequestFailure.UnknownField => 400,
        RequestFailure.RateLimited => 429,
        RequestFailure.AuthenticationRequired or RequestFailure.CredentialInvalid => 401,
        RequestFailure.PermissionDenied or RequestFailure.HumanRequired or RequestFailure.InstanceSuspended => 403,
        RequestFailure.ResourceNotFound => 404,
        RequestFailure.IdempotencyConflict => 409,
        RequestFailure.ValidationFailed or RequestFailure.GrantExpired or RequestFailure.GrantExhausted or RequestFailure.GrantRevoked => 422,
        RequestFailure.ConfigurationInvalid or RequestFailure.DependencyUnavailable => 503,
        _ => throw new ArgumentOutOfRangeException(nameof(Failure))
    };

    private static string GetCode(RequestFailure failure) => failure switch
    {
        RequestFailure.InvalidRequest => "INVALID_REQUEST",
        RequestFailure.UnknownField => "UNKNOWN_FIELD",
        RequestFailure.IdempotencyConflict => "IDEMPOTENCY_CONFLICT",
        RequestFailure.RateLimited => "RATE_LIMITED",
        RequestFailure.AuthenticationRequired => "AUTHENTICATION_REQUIRED",
        RequestFailure.CredentialInvalid => "CREDENTIAL_INVALID",
        RequestFailure.PermissionDenied => "PERMISSION_DENIED",
        RequestFailure.HumanRequired => "HUMAN_REQUIRED",
        RequestFailure.ResourceNotFound => "RESOURCE_NOT_FOUND",
        RequestFailure.InstanceSuspended => "INSTANCE_SUSPENDED",
        RequestFailure.ValidationFailed => "VALIDATION_FAILED",
        RequestFailure.ConfigurationInvalid => "CONFIGURATION_INVALID",
        RequestFailure.DependencyUnavailable => "DEPENDENCY_UNAVAILABLE",
        RequestFailure.GrantExpired => "GRANT_EXPIRED",
        RequestFailure.GrantExhausted => "GRANT_EXHAUSTED",
        RequestFailure.GrantRevoked => "GRANT_REVOKED",
        _ => throw new ArgumentOutOfRangeException(nameof(failure))
    };
}
