using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Svm.EntityFrameworkCore.Framework;

namespace Svm.EntityFrameworkCore.Migrations;

[DbContext(typeof(SvmDbContext))]
[Migration("20261024000100_RolesAndSiteScopes")]
internal sealed class RolesAndSiteScopes : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        CREATE TABLE iam.roles ("Id" uuid PRIMARY KEY, "Name" varchar(128) NOT NULL, "IsEnabled" boolean NOT NULL, "Revision" bigint NOT NULL);
        CREATE UNIQUE INDEX "IX_roles_Name" ON iam.roles("Name");
        CREATE TABLE iam.role_operations ("RoleId" uuid NOT NULL REFERENCES iam.roles("Id") ON DELETE RESTRICT,
          "Operation" varchar(64) NOT NULL, PRIMARY KEY ("RoleId","Operation"));
        CREATE TABLE iam.role_assignments ("Id" uuid PRIMARY KEY, "SubjectId" uuid NOT NULL REFERENCES iam.subject_guards("SubjectId") ON DELETE RESTRICT,
          "RoleId" uuid NOT NULL REFERENCES iam.roles("Id") ON DELETE RESTRICT, "ScopeKind" varchar(16) NOT NULL, "ResourceId" uuid,
          CONSTRAINT "CK_role_assignments_scope" CHECK (("ScopeKind"='Site' AND "ResourceId" IS NULL) OR ("ScopeKind" IN ('Process','Device','Software') AND "ResourceId" IS NOT NULL)));
        CREATE UNIQUE INDEX "IX_role_assignments_SubjectId_RoleId_ScopeKind_ResourceId" ON iam.role_assignments("SubjectId","RoleId","ScopeKind","ResourceId") NULLS NOT DISTINCT;
        CREATE INDEX "IX_role_assignments_RoleId" ON iam.role_assignments("RoleId");
        INSERT INTO iam.roles VALUES
          ('00000000-0000-4000-a000-000000000001','平台管理员',true,1),
          ('00000000-0000-4000-a000-000000000002','程序工程师',true,1),
          ('00000000-0000-4000-a000-000000000003','现场运维',true,1),
          ('00000000-0000-4000-a000-000000000004','只读人员',true,1);
        INSERT INTO iam.role_operations SELECT '00000000-0000-4000-a000-000000000001',unnest(ARRAY['identity.manage','asset.read','asset.create','asset.manage','binding.manage','software.read','instance.read']);
        INSERT INTO iam.role_operations SELECT '00000000-0000-4000-a000-000000000002',unnest(ARRAY['asset.read','software.read','instance.read','software.create','binding.manage','instance.manage','enrollment.manage','release.upload']);
        INSERT INTO iam.role_operations SELECT '00000000-0000-4000-a000-000000000003',unnest(ARRAY['asset.read','software.read','instance.read','deployment.create','deployment.control']);
        INSERT INTO iam.role_operations SELECT '00000000-0000-4000-a000-000000000004',unnest(ARRAY['asset.read','software.read','instance.read']);

        -- Scope is attached to each operation's own role. This view never expands field grants into software-wide grants.
        CREATE VIEW iam.effective_role_grants AS
          SELECT a."SubjectId", o."Operation", a."ScopeKind", a."ResourceId"
          FROM iam.role_assignments a JOIN iam.roles r ON r."Id"=a."RoleId" JOIN iam.role_operations o ON o."RoleId"=r."Id" WHERE r."IsEnabled";
        CREATE FUNCTION iam.has_global(subject uuid, operation text) RETURNS boolean LANGUAGE sql STABLE SECURITY INVOKER AS $$
          SELECT EXISTS(SELECT 1 FROM iam.users u WHERE u."Id"=subject AND u."IsEnabled" AND NOT u."MustChangePassword") AND
            (EXISTS(SELECT 1 FROM iam.permissions p WHERE p."SubjectId"=subject AND p."SoftwareId" IS NULL AND p."Operation"=operation)
             OR EXISTS(SELECT 1 FROM iam.effective_role_grants g WHERE g."SubjectId"=subject AND g."Operation"=operation AND g."ScopeKind"='Site'))
        $$;
        CREATE FUNCTION iam.has_any(subject uuid, operation text) RETURNS boolean LANGUAGE sql STABLE SECURITY INVOKER AS $$
          SELECT EXISTS(SELECT 1 FROM iam.users u WHERE u."Id"=subject AND u."IsEnabled" AND NOT u."MustChangePassword") AND
            (EXISTS(SELECT 1 FROM iam.permissions p WHERE p."SubjectId"=subject AND (p."Operation"=operation OR operation='asset.create' AND p."SoftwareId" IS NULL AND p."Operation"='asset.manage'))
             OR EXISTS(SELECT 1 FROM iam.effective_role_grants g WHERE g."SubjectId"=subject AND g."Operation"=operation))
        $$;
        CREATE FUNCTION iam.has_product(subject uuid, operation text, software uuid) RETURNS boolean LANGUAGE sql STABLE SECURITY INVOKER AS $$
          SELECT EXISTS(SELECT 1 FROM iam.users u WHERE u."Id"=subject AND u."IsEnabled" AND NOT u."MustChangePassword") AND
            (EXISTS(SELECT 1 FROM iam.permissions p WHERE p."SubjectId"=subject AND p."SoftwareId"=software AND p."Operation"=operation)
             OR operation IN ('software.read','release.upload','release.publish','release.disable','audit.read') AND EXISTS(SELECT 1 FROM iam.effective_role_grants g WHERE g."SubjectId"=subject AND g."Operation"=operation AND g."ScopeKind"='Software' AND g."ResourceId"=software))
        $$;
        CREATE FUNCTION iam.has_field(subject uuid, operation text, process uuid, device uuid, software uuid) RETURNS boolean LANGUAGE sql STABLE SECURITY INVOKER AS $$
          SELECT EXISTS(SELECT 1 FROM iam.users u WHERE u."Id"=subject AND u."IsEnabled" AND NOT u."MustChangePassword") AND
            (EXISTS(SELECT 1 FROM iam.permissions p WHERE p."SubjectId"=subject AND p."Operation"=operation AND (p."SoftwareId" IS NULL OR p."SoftwareId"=software))
             OR operation='asset.create' AND EXISTS(SELECT 1 FROM iam.permissions p WHERE p."SubjectId"=subject AND p."SoftwareId" IS NULL AND p."Operation"='asset.manage')
             OR operation='binding.manage' AND EXISTS(SELECT 1 FROM iam.permissions p WHERE p."SubjectId"=subject AND p."SoftwareId" IS NULL AND p."Operation"='asset.manage') AND
                EXISTS(SELECT 1 FROM iam.permissions p WHERE p."SubjectId"=subject AND p."SoftwareId"=software AND p."Operation"='instance.manage')
             OR operation IN ('asset.read','asset.create','asset.manage','software.create','binding.manage','software.read','instance.read','instance.manage','enrollment.manage','deployment.create','deployment.control','task.closeUnknown','audit.read') AND EXISTS(SELECT 1 FROM iam.effective_role_grants g WHERE g."SubjectId"=subject AND g."Operation"=operation AND
                (g."ScopeKind"='Site' OR g."ScopeKind"='Process' AND g."ResourceId"=process OR g."ScopeKind"='Device' AND g."ResourceId"=device)))
        $$;
        CREATE FUNCTION iam.has_software(subject uuid, operation text, software uuid) RETURNS boolean LANGUAGE sql STABLE SECURITY INVOKER AS $$
          SELECT iam.has_product(subject,operation,software) OR operation IN ('software.read','instance.read','instance.manage','enrollment.manage','deployment.create','deployment.control','task.closeUnknown','audit.read') AND EXISTS(SELECT 1 FROM ins.device_software_bindings b JOIN ins.devices d ON d."Id"=b."DeviceId"
            WHERE b."SoftwareId"=software AND b."IsActive" AND iam.has_field(subject,operation,d."ProcessId",d."Id",software))
        $$;
        CREATE FUNCTION iam.has_instance(subject uuid, operation text, instance uuid, software uuid) RETURNS boolean LANGUAGE sql STABLE SECURITY INVOKER AS $$
          SELECT EXISTS(SELECT 1 FROM ins.instances i JOIN ins.devices d ON d."Id"=i."DeviceId"
            WHERE i."Id"=instance AND i."SoftwareId"=software AND iam.has_field(subject,operation,d."ProcessId",d."Id",software))
        $$;
        REVOKE ALL ON FUNCTION iam.has_global(uuid,text),iam.has_any(uuid,text),iam.has_product(uuid,text,uuid),iam.has_field(uuid,text,uuid,uuid,uuid),iam.has_software(uuid,text,uuid),iam.has_instance(uuid,text,uuid,uuid) FROM PUBLIC;
        """);
    protected override void Down(MigrationBuilder migrationBuilder) => throw new NotSupportedException("Authorization history must be retained.");
    protected override void BuildTargetModel(ModelBuilder modelBuilder)
    {
        SvmDbContextModelSnapshot.Configure(modelBuilder); SvmDbContextModelSnapshot.ConfigurePersonnelV1(modelBuilder);
        SvmDbContextModelSnapshot.ConfigureOperationResultsV1(modelBuilder); BusOutboxV1Model.Configure(modelBuilder); CatalogV1Model.Configure(modelBuilder);
        InstanceAccessV1Model.Configure(modelBuilder); ReleasePackagesV1Model.Configure(modelBuilder); ReleasePublicationV1Model.Configure(modelBuilder);
        TaskWorkflowV1Model.Configure(modelBuilder); IntegrationMaterialsV1Model.Configure(modelBuilder); GitLabImportV1Model.Configure(modelBuilder);
        HardwareWorkspaceV1Model.Configure(modelBuilder); RolesAndScopesV1Model.Configure(modelBuilder);
    }
}
