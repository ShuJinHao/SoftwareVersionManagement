using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Svm.EntityFrameworkCore.Framework;

namespace Svm.EntityFrameworkCore.Migrations;

[DbContext(typeof(SvmDbContext))]
internal sealed class SvmDbContextModelSnapshot : ModelSnapshot
{
    protected override void BuildModel(ModelBuilder modelBuilder)
    {
        Configure(modelBuilder);
        ConfigurePersonnelV1(modelBuilder);
        ConfigureOperationResultsV1(modelBuilder);
        BusOutboxV1Model.Configure(modelBuilder);
        CatalogV1Model.Configure(modelBuilder);
        InstanceAccessV1Model.Configure(modelBuilder);
        ReleasePackagesV1Model.Configure(modelBuilder);
    }
    internal static void Configure(ModelBuilder modelBuilder) => modelBuilder.HasDefaultSchema("framework").HasAnnotation("ProductVersion", "8.0.31");

    // Frozen property-bag model; no dependency on the live operation-result mapping.
    internal static void ConfigureOperationResultsV1(ModelBuilder model)
    {
        foreach (var schema in new[] { "iam", "rel", "pkg", "ins", "tsk", "aud" })
            model.SharedTypeEntity<Dictionary<string, object>>("Svm.OperationResult." + schema, b =>
            {
                b.IndexerProperty<short>("ActorKind"); b.IndexerProperty<Guid>("SubjectId").ValueGeneratedNever();
                b.IndexerProperty<string>("Operation").IsRequired().HasMaxLength(128);
                b.IndexerProperty<Guid>("IdempotencyKey").ValueGeneratedNever();
                b.IndexerProperty<string>("RequestDigest").IsRequired().HasMaxLength(64);
                b.IndexerProperty<Guid>("OperationId").ValueGeneratedNever();
                b.IndexerProperty<short?>("Status"); b.IndexerProperty<Guid?>("ResourceId"); b.IndexerProperty<Guid?>("WorkId");
                b.IndexerProperty<DateTimeOffset>("CreatedAt").HasDefaultValueSql("clock_timestamp()");
                b.IndexerProperty<DateTimeOffset?>("CompletedAt");
                b.HasKey("ActorKind", "SubjectId", "Operation", "IdempotencyKey");
                b.HasIndex("OperationId").IsUnique().HasDatabaseName("IX_operation_results_OperationId"); b.ToTable("operation_results", schema);
            });
    }

    // Frozen migration model. Never delegate snapshots to the live module mapping.
    internal static void ConfigurePersonnelV1(ModelBuilder model)
    {
        model.Entity("Svm.Core.Identity.UserAccount", b =>
        {
            b.Property<Guid>("Id").ValueGeneratedNever();
            b.Property<string>("EmployeeNo").IsRequired().HasMaxLength(64);
            b.Property<string>("DisplayName").IsRequired().HasMaxLength(128);
            b.Property<string>("PasswordHash").IsRequired().HasMaxLength(1024);
            b.Property<bool>("IsEnabled"); b.Property<bool>("MustChangePassword");
            b.HasKey("Id"); b.HasIndex("EmployeeNo").IsUnique(); b.ToTable("users", "iam");
        });
        model.Entity("Svm.Core.Identity.WebSession", b =>
        {
            b.Property<Guid>("Id").ValueGeneratedNever(); b.Property<Guid>("SubjectId");
            b.Property<string>("SecretHash").IsRequired().HasMaxLength(64);
            b.Property<DateTimeOffset>("ExpiresAt"); b.Property<DateTimeOffset?>("RevokedAt");
            b.HasKey("Id"); b.HasIndex("SubjectId"); b.HasIndex("ExpiresAt"); b.ToTable("sessions", "iam");
        });
        model.Entity("Svm.EntityFrameworkCore.Identity.SubjectGuard", b =>
        {
            b.Property<Guid>("SubjectId").ValueGeneratedNever(); b.Property<long>("Revision");
            b.HasKey("SubjectId"); b.ToTable("subject_guards", "iam");
        });
        model.Entity("Svm.EntityFrameworkCore.Identity.PermissionDefinition", b =>
        {
            b.Property<string>("Operation").HasMaxLength(64); b.Property<bool>("Global");
            b.HasKey("Operation"); b.ToTable("permission_catalog", "iam");
        });
        model.Entity("Svm.EntityFrameworkCore.Identity.PermissionGrant", b =>
        {
            b.Property<Guid>("Id").ValueGeneratedNever(); b.Property<Guid>("SubjectId"); b.Property<Guid?>("SoftwareId");
            b.Property<string>("Operation").IsRequired().HasMaxLength(64);
            b.HasKey("Id"); b.HasIndex("Operation");
            b.HasIndex("SubjectId", "SoftwareId", "Operation").IsUnique().AreNullsDistinct(false);
            b.HasOne("Svm.EntityFrameworkCore.Identity.PermissionDefinition", null).WithMany()
                .HasForeignKey("Operation").OnDelete(DeleteBehavior.Restrict).IsRequired();
            b.ToTable("permissions", "iam");
        });
        model.Entity("Svm.EntityFrameworkCore.Identity.SeedMarker", b =>
        {
            b.Property<string>("Name").HasMaxLength(64); b.Property<DateTimeOffset>("CompletedAt");
            b.HasKey("Name"); b.ToTable("seed_markers", "iam");
        });
        model.Entity("Svm.EntityFrameworkCore.Identity.LoginLimit", b =>
        {
            b.Property<string>("Key").HasMaxLength(66); b.Property<int>("Failures"); b.Property<DateTimeOffset>("WindowEnd");
            b.HasKey("Key"); b.HasIndex("WindowEnd"); b.ToTable("login_limits", "iam");
        });
        model.Entity("Svm.Core.Audit.AuditEntry", b =>
        {
            b.Property<Guid>("Id").ValueGeneratedNever(); b.Property<Guid>("OperationId"); b.Property<Guid?>("SubjectId"); b.Property<Guid?>("ObjectId");
            b.Property<string>("ActorKind").IsRequired().HasMaxLength(32); b.Property<string>("EmployeeNo").HasMaxLength(64);
            b.Property<string>("DisplayName").HasMaxLength(128); b.Property<string>("Operation").IsRequired().HasMaxLength(64);
            b.Property<string>("Result").IsRequired().HasMaxLength(32); b.Property<string>("Reason").IsRequired().HasMaxLength(256);
            b.Property<string>("CorrelationId").IsRequired().HasMaxLength(128); b.Property<DateTimeOffset>("OccurredAt");
            b.HasKey("Id"); b.HasIndex("OperationId").IsUnique(); b.HasIndex("OccurredAt"); b.ToTable("events", "aud");
        });
    }
}
