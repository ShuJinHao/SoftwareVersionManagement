using Microsoft.EntityFrameworkCore;
using Svm.Core.Identity;
using Svm.SharedKernel.Domain;

namespace Svm.EntityFrameworkCore.Identity;

internal sealed class RoleOperation { public StrongId<AccessRole> RoleId { get; set; } public string Operation { get; set; } = ""; }
internal sealed class RoleAssignment
{
    public Guid Id { get; set; }
    public Guid SubjectId { get; set; }
    public StrongId<AccessRole> RoleId { get; set; }
    public string ScopeKind { get; set; } = "";
    public Guid? ResourceId { get; set; }
}
internal static class RoleModel
{
    internal static void Configure(ModelBuilder model)
    {
        var roles = model.Entity<AccessRole>(); roles.ToTable("roles", "iam"); roles.HasKey(x => x.Id);
        roles.Property(x => x.Id).HasConversion(x => x.Value, x => new StrongId<AccessRole>(x)).ValueGeneratedNever(); roles.Ignore(x => x.DomainEvents);
        roles.Property(x => x.Name).HasMaxLength(128); roles.HasIndex(x => x.Name).IsUnique();
        var ops = model.Entity<RoleOperation>(); ops.ToTable("role_operations", "iam"); ops.HasKey(x => new { x.RoleId, x.Operation });
        ops.Property(x => x.RoleId).HasConversion(x => x.Value, x => new StrongId<AccessRole>(x));
        ops.Property(x => x.Operation).HasMaxLength(64);
        ops.HasOne<AccessRole>().WithMany().HasForeignKey(x => x.RoleId).OnDelete(DeleteBehavior.Restrict);
        var assignments = model.Entity<RoleAssignment>();
        assignments.ToTable("role_assignments", "iam", t => t.HasCheckConstraint("CK_role_assignments_scope",
            "(\"ScopeKind\"='Site' AND \"ResourceId\" IS NULL) OR (\"ScopeKind\" IN ('Process','Device','Software') AND \"ResourceId\" IS NOT NULL)"));
        assignments.Property(x => x.RoleId).HasConversion(x => x.Value, x => new StrongId<AccessRole>(x));
        assignments.HasKey(x => x.Id); assignments.Property(x => x.Id).ValueGeneratedNever(); assignments.Property(x => x.ScopeKind).HasMaxLength(16);
        assignments.HasIndex(x => new { x.SubjectId, x.RoleId, x.ScopeKind, x.ResourceId }).IsUnique().AreNullsDistinct(false);
        assignments.HasOne<AccessRole>().WithMany().HasForeignKey(x => x.RoleId).OnDelete(DeleteBehavior.Restrict);
        assignments.HasOne<SubjectGuard>().WithMany().HasForeignKey(x => x.SubjectId).OnDelete(DeleteBehavior.Restrict);
    }
}
