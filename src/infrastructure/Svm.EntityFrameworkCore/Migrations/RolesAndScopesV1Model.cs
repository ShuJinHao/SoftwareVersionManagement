using Microsoft.EntityFrameworkCore;

namespace Svm.EntityFrameworkCore.Migrations;

// Frozen thirteenth migration additions. Do not delegate to the live role mapping.
internal static class RolesAndScopesV1Model
{
    internal static void Configure(ModelBuilder model)
    {
        model.Entity("Svm.Core.Identity.AccessRole", b =>
        {
            b.Property<Guid>("Id").ValueGeneratedNever(); b.Property<string>("Name").IsRequired().HasMaxLength(128);
            b.Property<bool>("IsEnabled"); b.Property<long>("Revision"); b.HasKey("Id"); b.HasIndex("Name").IsUnique(); b.ToTable("roles", "iam");
        });
        model.Entity("Svm.EntityFrameworkCore.Identity.RoleOperation", b =>
        {
            b.Property<Guid>("RoleId"); b.Property<string>("Operation").HasMaxLength(64); b.HasKey("RoleId", "Operation");
            b.HasOne("Svm.Core.Identity.AccessRole", null).WithMany().HasForeignKey("RoleId").OnDelete(DeleteBehavior.Restrict).IsRequired(); b.ToTable("role_operations", "iam");
        });
        model.Entity("Svm.EntityFrameworkCore.Identity.RoleAssignment", b =>
        {
            b.Property<Guid>("Id").ValueGeneratedNever(); b.Property<Guid>("SubjectId"); b.Property<Guid>("RoleId");
            b.Property<string>("ScopeKind").IsRequired().HasMaxLength(16); b.Property<Guid?>("ResourceId"); b.HasKey("Id");
            b.HasIndex("SubjectId", "RoleId", "ScopeKind", "ResourceId").IsUnique().AreNullsDistinct(false); b.HasIndex("RoleId");
            b.HasOne("Svm.Core.Identity.AccessRole", null).WithMany().HasForeignKey("RoleId").OnDelete(DeleteBehavior.Restrict).IsRequired();
            b.HasOne("Svm.EntityFrameworkCore.Identity.SubjectGuard", null).WithMany().HasForeignKey("SubjectId").OnDelete(DeleteBehavior.Restrict).IsRequired();
            b.ToTable("role_assignments", "iam", t => t.HasCheckConstraint("CK_role_assignments_scope",
                "(\"ScopeKind\"='Site' AND \"ResourceId\" IS NULL) OR (\"ScopeKind\" IN ('Process','Device','Software') AND \"ResourceId\" IS NOT NULL)"));
        });
    }
}
