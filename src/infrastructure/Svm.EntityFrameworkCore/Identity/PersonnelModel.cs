using Microsoft.EntityFrameworkCore;
using Svm.Core.Identity;
using Svm.Core.Audit;
using Svm.SharedKernel.Domain;

namespace Svm.EntityFrameworkCore.Identity;

internal sealed class SubjectGuard { public Guid SubjectId { get; set; } public long Revision { get; set; } }
internal sealed class PermissionDefinition { public string Operation { get; set; } = ""; public bool Global { get; set; } }
internal sealed class PermissionGrant
{
    public Guid Id { get; set; }
    public Guid SubjectId { get; set; }
    public Guid? SoftwareId { get; set; }
    public string Operation { get; set; } = "";
}
internal sealed class SeedMarker { public string Name { get; set; } = ""; public DateTimeOffset CompletedAt { get; set; } }
internal sealed class LoginLimit { public string Key { get; set; } = ""; public int Failures { get; set; } public DateTimeOffset WindowEnd { get; set; } }

internal static class PersonnelModel
{
    internal static void Configure(ModelBuilder model)
    {
        var users = model.Entity<UserAccount>();
        users.ToTable("users", "iam"); users.HasKey(x => x.Id);
        users.Property(x => x.Id).HasConversion(x => x.Value, x => new StrongId<UserAccount>(x)).ValueGeneratedNever();
        users.Ignore(x => x.DomainEvents);
        users.Property(x => x.EmployeeNo).HasMaxLength(64); users.HasIndex(x => x.EmployeeNo).IsUnique();
        users.Property(x => x.DisplayName).HasMaxLength(128); users.Property(x => x.PasswordHash).HasMaxLength(1024);
        var sessions = model.Entity<WebSession>();
        sessions.ToTable("sessions", "iam"); sessions.HasKey(x => x.Id); sessions.Property(x => x.Id).ValueGeneratedNever();
        sessions.Property(x => x.SecretHash).HasMaxLength(64);
        sessions.HasIndex(x => x.SubjectId); sessions.HasIndex(x => x.ExpiresAt);
        var guard = model.Entity<SubjectGuard>(); guard.ToTable("subject_guards", "iam"); guard.HasKey(x => x.SubjectId);
        guard.Property(x => x.SubjectId).ValueGeneratedNever();
        var definitions = model.Entity<PermissionDefinition>(); definitions.ToTable("permission_catalog", "iam");
        definitions.HasKey(x => x.Operation); definitions.Property(x => x.Operation).HasMaxLength(64);
        var grants = model.Entity<PermissionGrant>(); grants.ToTable("permissions", "iam"); grants.HasKey(x => x.Id);
        grants.Property(x => x.Id).ValueGeneratedNever(); grants.Property(x => x.Operation).HasMaxLength(64);
        grants.HasIndex(x => new { x.SubjectId, x.SoftwareId, x.Operation }).IsUnique().AreNullsDistinct(false);
        grants.HasOne<PermissionDefinition>().WithMany().HasForeignKey(x => x.Operation).OnDelete(DeleteBehavior.Restrict);
        var marker = model.Entity<SeedMarker>(); marker.ToTable("seed_markers", "iam"); marker.HasKey(x => x.Name); marker.Property(x => x.Name).HasMaxLength(64);
        var limits = model.Entity<LoginLimit>(); limits.ToTable("login_limits", "iam"); limits.HasKey(x => x.Key); limits.Property(x => x.Key).HasMaxLength(66);
        limits.HasIndex(x => x.WindowEnd);
        var audit = model.Entity<AuditEntry>(); audit.ToTable("events", "aud"); audit.HasKey(x => x.Id); audit.Property(x => x.Id).ValueGeneratedNever();
        audit.HasIndex(x => x.OperationId).IsUnique(); audit.HasIndex(x => x.OccurredAt);
        audit.Property(x => x.ActorKind).HasMaxLength(32); audit.Property(x => x.EmployeeNo).HasMaxLength(64);
        audit.Property(x => x.DisplayName).HasMaxLength(128); audit.Property(x => x.Operation).HasMaxLength(64);
        audit.Property(x => x.Result).HasMaxLength(32); audit.Property(x => x.Reason).HasMaxLength(256);
        audit.Property(x => x.CorrelationId).HasMaxLength(128);
    }
}
