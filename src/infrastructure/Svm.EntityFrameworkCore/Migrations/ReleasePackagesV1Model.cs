using Microsoft.EntityFrameworkCore;

namespace Svm.EntityFrameworkCore.Migrations;

// Frozen V1 mapping. Never delegates to live module mappings.
internal static class ReleasePackagesV1Model
{
    internal static void Configure(ModelBuilder model)
    {
        model.Entity("Svm.Core.Releases.SoftwareRelease", b =>
        {
            b.Property<Guid>("Id").ValueGeneratedNever();
            b.Property<Guid>("SoftwareId");
            b.Property<int>("Major");
            b.Property<int>("Minor");
            b.Property<int>("Patch");
            b.Property<string>("State").HasMaxLength(32).IsRequired();
            b.Property<string>("ChangeLevel").HasMaxLength(16).IsRequired();
            b.Property<string>("ChangeSummary").HasMaxLength(2000).IsRequired();
            b.Property<string>("ChangeReason").HasMaxLength(256).IsRequired();
            b.Property<Guid>("PackageId");
            b.Property<Guid>("CreatedBy");
            b.Property<DateTimeOffset>("CreatedAt");
            b.Property<DateTimeOffset?>("DisabledAt");
            b.Property<string>("DisableReason").HasMaxLength(256);
            b.Property<long>("Revision");
            b.HasKey("Id");
            b.HasIndex("SoftwareId","Major","Minor","Patch").IsUnique();
            b.HasIndex("PackageId").IsUnique();
            b.ToTable("releases", "rel");
        });
        model.Entity("Svm.Core.Packages.PackageAsset", b =>
        {
            b.Property<Guid>("Id").ValueGeneratedNever();
            b.Property<Guid>("UploadId");
            b.Property<Guid>("ReleaseId");
            b.Property<Guid>("SoftwareId");
            b.Property<string>("FileName").HasMaxLength(255).IsRequired();
            b.Property<long>("ExpectedSize");
            b.Property<string>("ExpectedSha256").HasMaxLength(64).IsRequired();
            b.Property<long?>("SizeBytes");
            b.Property<string>("Sha256").HasMaxLength(64);
            b.Property<string>("State").HasMaxLength(32).IsRequired();
            b.Property<bool>("Disabled");
            b.Property<long>("Revision");
            b.HasKey("Id");
            b.HasIndex("ReleaseId").IsUnique();
            b.HasIndex("UploadId").IsUnique();
            b.HasIndex("State","Disabled");
            b.ToTable("packages", "pkg");
        });
        model.Entity("Svm.Core.Packages.PackageWork", b =>
        {
            b.Property<Guid>("Id").ValueGeneratedNever();
            b.Property<Guid>("PackageId");
            b.Property<Guid>("SoftwareId");
            b.Property<Guid>("InitiatorId");
            b.Property<string>("Kind").HasMaxLength(32).IsRequired();
            b.Property<string>("State").HasMaxLength(32).IsRequired();
            b.Property<string>("Stage").HasMaxLength(32).IsRequired();
            b.Property<string>("SourceNode").HasMaxLength(64).IsRequired();
            b.Property<Guid?>("ReceiveToken");
            b.Property<long>("ReceiveGeneration");
            b.Property<long>("DispatchSequence");
            b.Property<Guid>("DispatchEventId");
            b.Property<bool>("Accepted");
            b.Property<Guid?>("LeaseToken");
            b.Property<long>("LeaseGeneration");
            b.Property<string>("LeaseNode").HasMaxLength(64);
            b.Property<DateTimeOffset?>("LeaseUntil");
            b.Property<DateTimeOffset>("CreatedAt");
            b.Property<DateTimeOffset?>("CompletedAt");
            b.Property<string>("LastErrorCode").HasMaxLength(64);
            b.Property<long>("Revision");
            b.HasKey("Id");
            b.HasIndex("PackageId","CreatedAt");
            b.HasIndex("Accepted","State","LeaseUntil");
            b.ToTable("works", "pkg");
        });
        model.Entity("Svm.Core.Packages.PackageReplica", b =>
        {
            b.Property<Guid>("Id").ValueGeneratedNever();
            b.Property<Guid>("PackageId");
            b.Property<string>("NodeId").HasMaxLength(64).IsRequired();
            b.Property<string>("State").HasMaxLength(32).IsRequired();
            b.Property<DateTimeOffset?>("CheckedAt");
            b.HasKey("Id");
            b.HasIndex("PackageId","NodeId").IsUnique();
            b.ToTable("replicas", "pkg");
        });
        model.Entity("Svm.Core.Packages.PackageDownload", b =>
        {
            b.Property<Guid>("Id").ValueGeneratedNever();
            b.Property<Guid>("PackageId");
            b.Property<string>("NodeId").HasMaxLength(64).IsRequired();
            b.Property<Guid>("WorkerGeneration");
            b.Property<Guid>("SubjectId");
            b.Property<string>("ActorKind").HasMaxLength(32).IsRequired();
            b.Property<string>("EmployeeNo").HasMaxLength(64);
            b.Property<DateTimeOffset>("StartedAt");
            b.Property<DateTimeOffset?>("EndedAt");
            b.Property<long?>("BytesSent");
            b.Property<string>("State").HasMaxLength(32).IsRequired();
            b.HasKey("Id");
            b.HasIndex("PackageId","State");
            b.HasIndex("NodeId","WorkerGeneration","State");
            b.ToTable("download_sessions", "pkg");
        });
        model.Entity("Svm.Core.Packages.PackageDispatch", b =>
        {
            b.Property<Guid>("Id").ValueGeneratedNever();
            b.Property<Guid>("WorkId");
            b.Property<long>("Sequence");
            b.HasKey("Id");
            b.HasIndex("WorkId","Sequence").IsUnique();
            b.ToTable("dispatches", "pkg");
        });
        model.Entity("Svm.Core.Packages.PackageReceiveAttempt", b =>
        {
            b.Property<Guid>("Id").ValueGeneratedNever();
            b.Property<Guid>("UploadId");
            b.Property<Guid>("PackageId");
            b.Property<long>("Generation");
            b.Property<string>("NodeId").HasMaxLength(64).IsRequired();
            b.HasKey("Id");
            b.HasIndex("UploadId","Generation").IsUnique();
            b.ToTable("receive_attempts", "pkg");
        });
    }
}
