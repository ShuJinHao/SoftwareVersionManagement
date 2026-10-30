namespace Svm.Core.Releases;

/// <summary>Human field evidence is distinct from executable installation reports.</summary>
public sealed class PlcPublication(Guid id, Guid releaseId, Guid hardwareId, Guid bindingId, long bindingRevision,
    DateTimeOffset fieldAt, string verification, string materials, Guid actorId, string employeeNo, DateTimeOffset recordedAt)
{
    public Guid Id { get; private set; } = id;
    public Guid ReleaseId { get; private set; } = releaseId;
    public Guid HardwareId { get; private set; } = hardwareId;
    public Guid BindingId { get; private set; } = bindingId;
    public long BindingRevision { get; private set; } = bindingRevision;
    public DateTimeOffset FieldAt { get; private set; } = fieldAt;
    public string Verification { get; private set; } = verification;
    public string Materials { get; private set; } = materials;
    public Guid ActorId { get; private set; } = actorId;
    public string EmployeeNo { get; private set; } = employeeNo;
    public DateTimeOffset RecordedAt { get; private set; } = recordedAt;
}
public interface IPlcPublicationRepository
{
    Task<PlcPublication?> GetAsync(Guid releaseId, CancellationToken token);
    void Add(PlcPublication evidence);
}
