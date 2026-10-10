namespace Svm.Services.Contracts.Framework;

public enum RequestFailure
{
    AuthenticationRequired = 1, CredentialInvalid, PermissionDenied, HumanRequired,
    ResourceNotFound, InstanceSuspended, ValidationFailed, ConfigurationInvalid, DependencyUnavailable,
    GrantExpired, GrantExhausted, GrantRevoked, RateLimited, InvalidRequest, UnknownField, IdempotencyConflict,
    RevisionRequired, RevisionConflict, InvalidState, PayloadTooLarge, RegistrationConflict, ReportConflict, VersionConflict, UploadInProgress, ChecksumMismatch, NoHealthyReplica, ReceiptConflict, RollbackRequired, CurrentVersionUnknown, RetrySourceInvalid
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
        RequestFailure.InvalidRequest or RequestFailure.UnknownField or RequestFailure.RevisionRequired => 400,
        RequestFailure.PayloadTooLarge => 413,
        RequestFailure.RateLimited => 429,
        RequestFailure.AuthenticationRequired or RequestFailure.CredentialInvalid => 401,
        RequestFailure.PermissionDenied or RequestFailure.HumanRequired or RequestFailure.InstanceSuspended => 403,
        RequestFailure.ResourceNotFound => 404,
        RequestFailure.ReceiptConflict or RequestFailure.RollbackRequired or RequestFailure.CurrentVersionUnknown or RequestFailure.RetrySourceInvalid or RequestFailure.IdempotencyConflict or RequestFailure.RevisionConflict or RequestFailure.InvalidState or RequestFailure.RegistrationConflict or RequestFailure.ReportConflict or RequestFailure.VersionConflict or RequestFailure.UploadInProgress => 409,
        RequestFailure.ValidationFailed or RequestFailure.GrantExpired or RequestFailure.GrantExhausted or RequestFailure.GrantRevoked or RequestFailure.ChecksumMismatch => 422,
        RequestFailure.ConfigurationInvalid or RequestFailure.DependencyUnavailable or RequestFailure.NoHealthyReplica => 503,
        _ => throw new ArgumentOutOfRangeException(nameof(Failure))
    };

    private static string GetCode(RequestFailure failure) => failure switch
    {
        RequestFailure.ReceiptConflict => "RECEIPT_CONFLICT",
        RequestFailure.RollbackRequired => "ROLLBACK_REQUIRED",
        RequestFailure.CurrentVersionUnknown => "CURRENT_VERSION_UNKNOWN",
        RequestFailure.RetrySourceInvalid => "RETRY_SOURCE_INVALID",
        RequestFailure.InvalidRequest => "INVALID_REQUEST",
        RequestFailure.UnknownField => "UNKNOWN_FIELD",
        RequestFailure.IdempotencyConflict => "IDEMPOTENCY_CONFLICT",
        RequestFailure.RevisionRequired => "REVISION_REQUIRED",
        RequestFailure.RevisionConflict => "REVISION_CONFLICT",
        RequestFailure.InvalidState => "INVALID_STATE",
        RequestFailure.RegistrationConflict => "REGISTRATION_CONFLICT",
        RequestFailure.ReportConflict => "REPORT_CONFLICT",
        RequestFailure.VersionConflict => "VERSION_CONFLICT",
        RequestFailure.UploadInProgress => "UPLOAD_IN_PROGRESS",
        RequestFailure.ChecksumMismatch => "CHECKSUM_MISMATCH",
        RequestFailure.NoHealthyReplica => "NO_HEALTHY_REPLICA",
        RequestFailure.PayloadTooLarge => "PAYLOAD_TOO_LARGE",
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
