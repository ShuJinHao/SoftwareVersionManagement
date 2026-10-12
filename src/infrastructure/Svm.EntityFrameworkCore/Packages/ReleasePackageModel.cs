using Microsoft.EntityFrameworkCore;
using Svm.Core.Packages;
using Svm.Core.Releases;
using Svm.SharedKernel.Domain;

namespace Svm.EntityFrameworkCore.Packages;

internal static class ReleasePackageModel
{
    internal static void Configure(ModelBuilder model)
    {
        var r = model.Entity<SoftwareRelease>(); r.ToTable("releases", "rel"); r.HasKey(x => x.Id);
        r.Property(x => x.Id).HasConversion(x => x.Value, x => new StrongId<SoftwareRelease>(x)).ValueGeneratedNever(); r.Ignore(x => x.DomainEvents); r.Ignore(x => x.Version);
        r.Property(x => x.State).HasMaxLength(32); r.Property(x => x.ChangeLevel).HasMaxLength(16); r.Property(x => x.ChangeSummary).HasMaxLength(2000);
        r.Property(x => x.ChangeReason).HasMaxLength(256); r.Property(x => x.DisableReason).HasMaxLength(256);
        r.Property(x => x.PublishedEmployeeNo).HasMaxLength(64); r.Property(x => x.PublishReason).HasMaxLength(256);
        r.Property(x => x.PublishConclusion).HasMaxLength(2000);
        r.HasIndex(x => new { x.SoftwareId, x.PublishedAt });
        r.HasIndex(x => new { x.SoftwareId, x.Major, x.Minor, x.Patch }).IsUnique(); r.HasIndex(x => x.PackageId).IsUnique();
        var p = model.Entity<PackageAsset>(); p.ToTable("packages", "pkg"); p.HasKey(x => x.Id);
        p.Property(x => x.Id).HasConversion(x => x.Value, x => new StrongId<PackageAsset>(x)).ValueGeneratedNever(); p.Ignore(x => x.DomainEvents);
        p.Property(x => x.FileName).HasMaxLength(255); p.Property(x => x.ExpectedSha256).HasMaxLength(64); p.Property(x => x.Sha256).HasMaxLength(64); p.Property(x => x.State).HasMaxLength(32);
        p.HasIndex(x => x.ReleaseId).IsUnique(); p.HasIndex(x => x.UploadId).IsUnique(); p.HasIndex(x => new { x.State, x.Disabled });
        var w = model.Entity<PackageWork>(); w.ToTable("works", "pkg"); w.HasKey(x => x.Id);
        w.Property(x => x.Id).HasConversion(x => x.Value, x => new StrongId<PackageWork>(x)).ValueGeneratedNever(); w.Ignore(x => x.DomainEvents);
        w.Property(x => x.Kind).HasMaxLength(32); w.Property(x => x.State).HasMaxLength(32); w.Property(x => x.Stage).HasMaxLength(32);
        w.Property(x => x.SourceNode).HasMaxLength(64); w.Property(x => x.LeaseNode).HasMaxLength(64); w.Property(x => x.LastErrorCode).HasMaxLength(64);
        w.HasIndex(x => new { x.PackageId, x.CreatedAt }); w.HasIndex(x => new { x.Accepted, x.State, x.LeaseUntil });
        var replica = model.Entity<PackageReplica>(); replica.ToTable("replicas", "pkg"); replica.HasKey(x => x.Id); replica.Property(x => x.Id).ValueGeneratedNever();
        replica.Property(x => x.NodeId).HasMaxLength(64); replica.Property(x => x.State).HasMaxLength(32); replica.HasIndex(x => new { x.PackageId, x.NodeId }).IsUnique();
        var d = model.Entity<PackageDownload>(); d.ToTable("download_sessions", "pkg"); d.HasKey(x => x.Id); d.Property(x => x.Id).ValueGeneratedNever();
        d.Property(x => x.NodeId).HasMaxLength(64); d.Property(x => x.ActorKind).HasMaxLength(32); d.Property(x => x.EmployeeNo).HasMaxLength(64); d.Property(x => x.State).HasMaxLength(32);
        d.HasIndex(x => new { x.PackageId, x.State }); d.HasIndex(x => new { x.NodeId, x.WorkerGeneration, x.State });
        var dispatch = model.Entity<PackageDispatch>(); dispatch.ToTable("dispatches", "pkg"); dispatch.HasKey(x => x.Id); dispatch.Property(x => x.Id).ValueGeneratedNever();
        dispatch.HasIndex(x => new { x.WorkId, x.Sequence }).IsUnique();
        var attempt = model.Entity<PackageReceiveAttempt>(); attempt.ToTable("receive_attempts", "pkg"); attempt.HasKey(x => x.Id); attempt.Property(x => x.Id).ValueGeneratedNever();
        attempt.Property(x => x.NodeId).HasMaxLength(64); attempt.HasIndex(x => new { x.UploadId, x.Generation }).IsUnique();
        // Owner models use opaque cross-module IDs; restrictive cross-module FKs are installed by the migration.
    }
}
