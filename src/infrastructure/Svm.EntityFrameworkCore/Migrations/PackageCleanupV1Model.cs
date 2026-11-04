using Microsoft.EntityFrameworkCore;
namespace Svm.EntityFrameworkCore.Migrations;
// Frozen seventeenth migration model; never delegates to live mappings.
internal static class PackageCleanupV1Model
{
    internal static void Configure(ModelBuilder model)
    {
        model.Entity("Svm.Core.Packages.PackageCleanup", b =>
        {
            b.Property<Guid>("Id").ValueGeneratedNever(); b.HasKey("Id");
            b.Property<Guid>("PackageId"); b.Property<Guid>("RequestedBy"); b.Property<DateTimeOffset>("CreatedAt");
            b.Property<string>("Reason").IsRequired().HasMaxLength(256);
            b.HasIndex("PackageId").IsUnique(); b.ToTable("cleanup_intents", "pkg");
        });
        model.Entity("Svm.Core.Packages.DownloadStopEvidence", b =>
        {
            b.Property<Guid>("Id").ValueGeneratedNever(); b.HasKey("Id"); b.Property<Guid>("PackageId");
            b.Property<string>("NodeId").IsRequired().HasMaxLength(64); b.Property<Guid>("WorkerGeneration");
            b.Property<DateTimeOffset>("StoppedAt"); b.Property<Guid>("RecordedBy"); b.Property<DateTimeOffset>("RecordedAt");
            b.Property<string>("Evidence").IsRequired().HasMaxLength(2000); b.Property<string>("Reason").IsRequired().HasMaxLength(256);
            b.HasIndex("PackageId", "NodeId", "WorkerGeneration", "StoppedAt").HasDatabaseName("IX_download_stop_lookup");
            b.ToTable("download_stop_evidence", "pkg");
        });
        model.Entity("Svm.Core.Packages.PackageWork").HasIndex("PackageId").IsUnique().HasFilter("\"Kind\"='Cleanup'").HasDatabaseName("IX_works_one_cleanup");
    }
}
