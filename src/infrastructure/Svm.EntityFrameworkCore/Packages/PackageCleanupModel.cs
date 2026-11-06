using Microsoft.EntityFrameworkCore;
using Svm.Core.Packages;
namespace Svm.EntityFrameworkCore.Packages;

internal static class PackageCleanupModel
{
    internal static void Configure(ModelBuilder model)
    {
        var c = model.Entity<PackageCleanup>(); c.ToTable("cleanup_intents", "pkg"); c.HasKey(x => x.Id);
        c.Property(x => x.Id).ValueGeneratedNever(); c.Property(x => x.Reason).HasMaxLength(256);
        c.HasIndex(x => x.PackageId).IsUnique();
        var e = model.Entity<DownloadStopEvidence>(); e.ToTable("download_stop_evidence", "pkg"); e.HasKey(x => x.Id);
        e.Property(x => x.Id).ValueGeneratedNever(); e.Property(x => x.NodeId).HasMaxLength(64);
        e.Property(x => x.Evidence).HasMaxLength(2000); e.Property(x => x.Reason).HasMaxLength(256);
        e.HasIndex(x => new { x.PackageId, x.NodeId, x.WorkerGeneration, x.StoppedAt }).HasDatabaseName("IX_download_stop_lookup");
        model.Entity<PackageWork>().HasIndex(x => x.PackageId).IsUnique().HasFilter("\"Kind\"='Cleanup'").HasDatabaseName("IX_works_one_cleanup");
    }
}
