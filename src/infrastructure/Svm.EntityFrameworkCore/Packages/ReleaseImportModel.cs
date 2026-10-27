using Microsoft.EntityFrameworkCore;

using Svm.Core.Releases;

namespace Svm.EntityFrameworkCore.Packages;

internal static class ReleaseImportModel
{
    internal static void Configure(ModelBuilder model)
    {
        model.Entity<SoftwareRelease >(b =>
        {
            b.Property<string>("SourceKind").IsRequired().HasMaxLength(16).HasDefaultValue("Manual");
            b.Property<Guid?>("SourceBindingId"); b.Property<string>("SourceTag").HasMaxLength(34); b.Property<string>("SourceFactsJson");
            b.HasIndex("SourceBindingId", "SourceTag").IsUnique();
        });
        model.Entity<ReleaseSourceBinding >(b =>
        {
            b.Property<Guid>("Id").ValueGeneratedNever();
            b.Property<Guid>("SoftwareId");
            b.Property<Guid>("SubjectId");
            b.Property<string>("BaseUri").IsRequired();
            b.Property<long>("ProjectId");
            b.Property<string>("PackageName").IsRequired();
            b.Property<Guid?>("ConnectionId");
            b.Property<bool>("Managed").HasDefaultValue(false);
            b.Property<bool>("IsCurrent").HasDefaultValue(true);
            b.Property<bool>("Enabled").HasDefaultValue(true);
            b.Property<long>("Revision").HasDefaultValue(1L);
            b.Property<long>("SourceRevision").HasDefaultValue(1L);
            b.Property<bool>("Initialized");
            b.Property<DateTimeOffset?>("LastSyncedAt");
            b.Property<DateTimeOffset?>("NextScanAt");
            b.Property<Guid?>("LatestWorkId");
            b.Property<string>("LastErrorCode");
            b.HasKey("Id"); b.HasIndex("SoftwareId", "IsCurrent").IsUnique().HasFilter("\"IsCurrent\""); b.HasIndex("SubjectId").IsUnique(); b.ToTable("source_bindings", "rel");
        });
        model.Entity<ReleaseSourceChange>(b =>
        {
            b.Property<Guid>("Id").ValueGeneratedNever(); b.Property<Guid>("BindingId"); b.Property<long>("Revision");
            b.Property<bool>("Enabled"); b.Property<DateTimeOffset>("CreatedAt");
            b.HasKey("Id"); b.HasIndex("BindingId", "Revision").IsUnique(); b.ToTable("source_changes", "rel");
            b.HasOne<ReleaseSourceBinding>().WithMany().HasForeignKey(x => x.BindingId).OnDelete(DeleteBehavior.Restrict).HasConstraintName("source_changes_BindingId_fkey");
        });
        model.Entity<ReleaseSyncWork >(b =>
        {
            b.Property<Guid>("Id").ValueGeneratedNever();
            b.Property<Guid>("BindingId");
            b.Property<Guid>("SoftwareId");
            b.Property<string>("Kind").IsRequired();
            b.Property<string>("Tag");
            b.Property<string>("Stage").IsRequired();
            b.Property<int>("Page");
            b.Property<string>("CandidateJson");
            b.Property<bool>("InitialScan");
            b.Property<Guid?>("ReleaseId");
            b.Property<Guid?>("ReceiveToken");
            b.Property<Guid?>("LeaseToken");
            b.Property<long>("LeaseGeneration");
            b.Property<DateTimeOffset?>("LeaseUntil");
            b.Property<DateTimeOffset>("NextAttemptAt");
            b.Property<string>("LastErrorCode");
            b.Property<DateTimeOffset>("CreatedAt");
            b.Property<DateTimeOffset?>("CompletedAt");
            b.Property<long>("Revision");
            b.HasKey("Id"); b.HasIndex("BindingId", "Tag").IsUnique().HasFilter("\"Tag\" IS NOT NULL");
            b.HasIndex("BindingId").IsUnique().HasFilter("\"Kind\"='Scan' AND \"Stage\" NOT IN ('Completed','Conflict')");
            b.HasIndex("SoftwareId", "NextAttemptAt", "LeaseUntil"); b.ToTable("sync_works", "rel");
        });
    }
}
