using Svm.SharedKernel.Domain;

namespace Svm.Core.Releases;

public sealed class SoftwareRelease : AggregateRoot<StrongId<SoftwareRelease>>
{
    private SoftwareRelease(StrongId<SoftwareRelease> id) : base(id) { }
    public SoftwareRelease(Guid id, Guid softwareId, int major, int minor, int patch, string changeLevel,
        string summary, string reason, Guid packageId, Guid createdBy, DateTimeOffset createdAt) : base(new(id))
    {
        SoftwareId = softwareId; Major = major; Minor = minor; Patch = patch;
        ChangeLevel = changeLevel; ChangeSummary = summary; ChangeReason = reason;
        PackageId = packageId; CreatedBy = createdBy; CreatedAt = createdAt;
    }
    public Guid SoftwareId { get; private set; }
    public int Major { get; private set; }
    public int Minor { get; private set; }
    public int Patch { get; private set; }
    public string State { get; private set; } = "Staging";
    public string ChangeLevel { get; private set; } = "";
    public string ChangeSummary { get; private set; } = "";
    public string ChangeReason { get; private set; } = "";
    public Guid PackageId { get; private set; }
    public Guid CreatedBy { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? DisabledAt { get; private set; }
    public string? DisableReason { get; private set; }
    public long Revision { get; private set; } = 1;
    public string Version => $"{Major}.{Minor}.{Patch}";
    public void OpenTest()
    {
        if (State == "Test") return;
        if (State != "Staging") throw new InvalidOperationException("Release cannot open for testing.");
        State = "Test"; Revision++;
    }
    public void Disable(string reason, DateTimeOffset now)
    { if (State == "Disabled") throw new InvalidOperationException("Release is disabled."); State = "Disabled"; DisableReason = reason; DisabledAt = now; Revision++; }
    public static (int Major, int Minor, int Patch) Next(SoftwareRelease? previous, string level) => previous is null ? (1, 0, 0) : level switch
    {
        "Patch" => (previous.Major, previous.Minor, checked(previous.Patch + 1)),
        "Minor" => (previous.Major, checked(previous.Minor + 1), 0),
        "Major" => (checked(previous.Major + 1), 0, 0),
        _ => throw new ArgumentException("Invalid change level.")
    };
}
public interface IReleaseRepository
{
    Task<SoftwareRelease?> GetAsync(Guid id, bool protect, CancellationToken token);
    Task<SoftwareRelease?> LatestAsync(Guid softwareId, CancellationToken token);
    void Add(SoftwareRelease release);
}
