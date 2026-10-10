namespace Svm.Services.Contracts.Framework;

public enum ModuleOwner { Identity = 1, Releases, Packages, Instances, Tasks, Audit }
public enum RequestKind { Session = 1, Manage, Client, Enrollment, Recovery, Internal }
public enum ActorKind { Anonymous = 1, Human, ManagementSystem, Instance, EnrollmentGrant, RecoveryGrant, Service }
public enum RequestScope { Global = 1, Software, Instance, InternalWork }
public enum ValidationMode { Required = 1, ExplicitlyNone }
public enum TransactionMode { ReadOnly = 1, DatabaseAtomic, PhasedFile }
public enum IdempotencyMode { None = 1, OperationResult, ReportSequence, ReceiptSequence, SelectionChunk, EnrollmentProtocol }

[AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
public sealed class RequestPolicyAttribute(
    string operation, ModuleOwner owner, RequestKind kind, RequestScope scope,
    TransactionMode transaction, IdempotencyMode idempotency, ValidationMode validation,
    params ActorKind[] actors) : Attribute
{
    public string Operation { get; } = operation;
    public ModuleOwner Owner { get; } = owner;
    public RequestKind Kind { get; } = kind;
    public RequestScope Scope { get; } = scope;
    public TransactionMode Transaction { get; } = transaction;
    public IdempotencyMode Idempotency { get; } = idempotency;
    public ValidationMode Validation { get; } = validation;
    public IReadOnlyList<ActorKind> Actors { get; } = Array.AsReadOnly((ActorKind[])actors.Clone());
    public string? Permission { get; set; }
    public string? ValidationReason { get; set; }
}

/// <summary>A copied, immutable policy; request input cannot change registration metadata.</summary>
public sealed record RequestPolicy
{
    public RequestPolicy(RequestPolicyAttribute attribute)
    {
        Operation = attribute.Operation;
        Owner = attribute.Owner;
        Kind = attribute.Kind;
        Scope = attribute.Scope;
        Transaction = attribute.Transaction;
        Idempotency = attribute.Idempotency;
        Validation = attribute.Validation;
        Actors = Array.AsReadOnly(attribute.Actors.ToArray());
        Permission = attribute.Permission;
        ValidationReason = attribute.ValidationReason;
    }

    public string Operation { get; }
    public ModuleOwner Owner { get; }
    public RequestKind Kind { get; }
    public RequestScope Scope { get; }
    public TransactionMode Transaction { get; }
    public IdempotencyMode Idempotency { get; }
    public ValidationMode Validation { get; }
    public IReadOnlyList<ActorKind> Actors { get; }
    public string? Permission { get; }
    public string? ValidationReason { get; }
}
