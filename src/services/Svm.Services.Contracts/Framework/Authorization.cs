namespace Svm.Services.Contracts.Framework;

public sealed record AuthorizationTarget
{
    private AuthorizationTarget(RequestScope scope, Guid? softwareId, Guid? instanceId, ModuleOwner? owner, Guid? workId)
    {
        if (softwareId == Guid.Empty || instanceId == Guid.Empty || workId == Guid.Empty)
            throw new ArgumentException("Authorization targets need assigned identifiers.");
        if (owner is not null && !Enum.IsDefined(owner.Value)) throw new ArgumentOutOfRangeException(nameof(owner));
        Scope = scope;
        SoftwareId = softwareId;
        InstanceId = instanceId;
        Owner = owner;
        WorkId = workId;
    }

    public RequestScope Scope { get; }
    public Guid? SoftwareId { get; }
    public Guid? InstanceId { get; }
    public ModuleOwner? Owner { get; }
    public Guid? WorkId { get; }
    public static AuthorizationTarget Global() => new(RequestScope.Global, null, null, null, null);
    public static AuthorizationTarget Software(Guid softwareId) => new(RequestScope.Software, softwareId, null, null, null);
    public static AuthorizationTarget Instance(Guid softwareId, Guid instanceId) => new(RequestScope.Instance, softwareId, instanceId, null, null);
    public static AuthorizationTarget Work(ModuleOwner owner, Guid workId, Guid? softwareId = null) => new(RequestScope.InternalWork, softwareId, null, owner, workId);
}

public sealed record AuthorizationRequest(object Request, RequestPolicy Policy, CallContextSnapshot Context);

public sealed record AuthorizationDecision
{
    private AuthorizationDecision(AuthorizationTarget? target, RequestFailure? failure)
    {
        Target = target;
        Failure = failure;
    }

    public AuthorizationTarget? Target { get; }
    public RequestFailure? Failure { get; }
    public static AuthorizationDecision Allow(AuthorizationTarget target) => new(target ?? throw new ArgumentNullException(nameof(target)), null);
    public static AuthorizationDecision Deny(RequestFailure failure) => Enum.IsDefined(failure)
        ? new(null, failure) : throw new ArgumentOutOfRangeException(nameof(failure));
}

/// <summary>
/// The application authorization adapter coordinates current IAM facts and resource-owner reads
/// through module contracts. It must not authorize from body softwareId, cached roles or message claims alone.
/// </summary>
public interface IRequestAuthorizer
{
    ValueTask<AuthorizationDecision> AuthorizeAsync(AuthorizationRequest request, CancellationToken cancellationToken);
}
