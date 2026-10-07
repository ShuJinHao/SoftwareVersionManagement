using Svm.Services.Contracts.Framework;

namespace Svm.Services.Contracts.Messaging.V1;

public enum PackageWorkKind { UploadVerificationAndCopy = 1, Repair, Cleanup }
public enum TaskPreparationWorkKind { TargetSelection = 1, Deployment }
public enum TaskControlWorkKind { Reschedule = 1, Cancel }

public sealed record PackageWorkAvailableV1(Guid EventId, DateTimeOffset OccurredAt, Guid CorrelationId,
    Guid? CausationId, Guid SiteId, Guid SoftwareId, PackageWorkKind WorkKind, Guid WorkId, long DispatchSequence) : IIntegrationEvent
{
    public int SchemaVersion => 1;
    public string MessageType => "svm.package-work.available.v1";
}

public sealed record TaskPreparationAvailableV1(Guid EventId, DateTimeOffset OccurredAt, Guid CorrelationId,
    Guid? CausationId, Guid SiteId, Guid SoftwareId, TaskPreparationWorkKind WorkKind, Guid WorkId, long DispatchSequence) : IIntegrationEvent
{
    public int SchemaVersion => 1;
    public string MessageType => "svm.task-preparation.available.v1";
}

public sealed record TaskControlAvailableV1(Guid EventId, DateTimeOffset OccurredAt, Guid CorrelationId,
    Guid? CausationId, Guid SiteId, Guid SoftwareId, TaskControlWorkKind WorkKind, Guid WorkId, long DispatchSequence) : IIntegrationEvent
{
    public int SchemaVersion => 1;
    public string MessageType => "svm.task-control.available.v1";
}
