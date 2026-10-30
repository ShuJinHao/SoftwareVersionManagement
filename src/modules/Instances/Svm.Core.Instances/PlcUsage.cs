using Svm.SharedKernel.Domain;
namespace Svm.Core.Instances;

/// <summary>Append-only human observation. Association revision prevents a reactivated position inheriting its former usage.</summary>
public sealed class PlcUsageRecord(Guid id, Guid hardwareId, Guid bindingId, long bindingRevision, Guid softwareId,
    Guid releaseId, string version, string releaseState, Guid? previousReleaseId, DateTimeOffset fieldAt, DateTimeOffset recordedAt, Guid actorId,
    string employeeNo, string note, string materials, Guid? correctsId)
{
    public Guid Id { get; private set; } = id;
    public Guid HardwareId { get; private set; } = hardwareId;
    public Guid BindingId { get; private set; } = bindingId;
    public long BindingRevision { get; private set; } = bindingRevision;
    public Guid SoftwareId { get; private set; } = softwareId;
    public Guid ReleaseId { get; private set; } = releaseId;
    public string Version { get; private set; } = version;
    public string ReleaseState { get; private set; } = releaseState;
    public Guid? PreviousReleaseId { get; private set; } = previousReleaseId;
    public DateTimeOffset FieldAt { get; private set; } = fieldAt;
    public DateTimeOffset RecordedAt { get; private set; } = recordedAt;
    public Guid ActorId { get; private set; } = actorId;
    public string EmployeeNo { get; private set; } = employeeNo;
    public string Note { get; private set; } = note;
    public string Materials { get; private set; } = materials;
    public Guid? CorrectsId { get; private set; } = correctsId;
}
public sealed class PlcUsageState(StrongId<PlcUsageState> id, long bindingRevision) : AggregateRoot<StrongId<PlcUsageState>>(id)
{
    public long BindingRevision { get; private set; } = bindingRevision;
    public long Revision { get; private set; }
    public Guid? CurrentRecordId { get; private set; }
    public void Record(long generation, Guid current)
    { BindingRevision = generation; CurrentRecordId = current; Revision++; }
}
public interface IPlcUsageRepository
{
    Task<PlcUsageState?> StateAsync(Guid bindingId, bool protect, CancellationToken token);
    Task<PlcUsageRecord?> GetAsync(Guid id, CancellationToken token);
    Task<PlcUsageRecord?> LatestAsync(Guid bindingId, long generation, Guid? exclude, CancellationToken token);
    Task<bool> IsCorrectedAsync(Guid id, CancellationToken token);
    void Add(PlcUsageState state);
    void Add(PlcUsageRecord record);
}
