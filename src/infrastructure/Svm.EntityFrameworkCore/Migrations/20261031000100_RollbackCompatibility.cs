using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Svm.EntityFrameworkCore.Framework;
namespace Svm.EntityFrameworkCore.Migrations;

[DbContext(typeof(SvmDbContext))]
[Migration("20261031000100_RollbackCompatibility")]
internal sealed class RollbackCompatibility : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        CREATE TABLE rel.compatibility_declarations (
          "Id" uuid NOT NULL,
          "TargetReleaseId" uuid NOT NULL,
          "DeclarationRevision" bigint NOT NULL CHECK ("DeclarationRevision">0),
          "SourceReleaseIds" uuid[] NOT NULL CHECK (cardinality("SourceReleaseIds") BETWEEN 1 AND 200),
          "DatabaseMode" text NOT NULL CHECK ("DatabaseMode" IN ('None','Present')),
          "DatabaseRequirementsJson" jsonb NOT NULL CHECK (jsonb_typeof("DatabaseRequirementsJson")='array'),
          "Statement" text NOT NULL,
          "Reason" text NOT NULL,
          "DeclaredBy" uuid NOT NULL,
          "CreatedAt" timestamp with time zone NOT NULL,
          CONSTRAINT "PK_compatibility_declarations" PRIMARY KEY ("Id")
        );
        CREATE UNIQUE INDEX "IX_compatibility_target_revision" ON rel.compatibility_declarations("TargetReleaseId","DeclarationRevision");
        ALTER TABLE rel.compatibility_declarations ADD FOREIGN KEY ("TargetReleaseId") REFERENCES rel.releases("Id") ON DELETE NO ACTION DEFERRABLE INITIALLY DEFERRED;
        ALTER TABLE rel.compatibility_declarations ADD FOREIGN KEY ("DeclaredBy") REFERENCES iam.users("Id") ON DELETE NO ACTION DEFERRABLE INITIALLY DEFERRED;
        ALTER TABLE tsk.deployments ADD COLUMN "Kind" text NOT NULL DEFAULT 'Update' CHECK ("Kind" IN ('Update','Rollback'));
        ALTER TABLE tsk.tasks ADD COLUMN "AdmissionCompatibilityJson" jsonb, ADD COLUMN "AdmissionCompatibilityId" uuid;
        ALTER TABLE tsk.attempts ADD COLUMN "CompatibilityJson" jsonb, ADD COLUMN "CompatibilityDeclarationId" uuid;
        ALTER TABLE tsk.tasks ADD FOREIGN KEY ("AdmissionCompatibilityId") REFERENCES rel.compatibility_declarations("Id") ON DELETE NO ACTION DEFERRABLE INITIALLY DEFERRED;
        ALTER TABLE tsk.attempts ADD FOREIGN KEY ("CompatibilityDeclarationId") REFERENCES rel.compatibility_declarations("Id") ON DELETE NO ACTION DEFERRABLE INITIALLY DEFERRED;
        ALTER TABLE tsk.tasks ADD CHECK (("AdmissionCompatibilityJson" IS NULL)=("AdmissionCompatibilityId" IS NULL));
        ALTER TABLE tsk.attempts ADD CHECK (("CompatibilityJson" IS NULL)=("CompatibilityDeclarationId" IS NULL));
        CREATE FUNCTION rel.protect_compatibility_history() RETURNS trigger LANGUAGE plpgsql AS $body$
        BEGIN RAISE EXCEPTION 'compatibility declarations are append only' USING ERRCODE='23514'; END $body$;
        CREATE TRIGGER "TR_compatibility_history" BEFORE UPDATE OR DELETE ON rel.compatibility_declarations FOR EACH ROW EXECUTE FUNCTION rel.protect_compatibility_history();
        CREATE FUNCTION tsk.protect_rollback_facts() RETURNS trigger LANGUAGE plpgsql AS $body$
        BEGIN
         IF TG_TABLE_NAME='deployments' THEN
          IF OLD."Kind" IS DISTINCT FROM NEW."Kind" THEN RAISE EXCEPTION 'deployment kind is immutable' USING ERRCODE='23514'; END IF;
         ELSIF TG_TABLE_NAME='tasks' THEN
          IF ROW(OLD."AdmissionCompatibilityId",OLD."AdmissionCompatibilityJson") IS DISTINCT FROM ROW(NEW."AdmissionCompatibilityId",NEW."AdmissionCompatibilityJson") THEN
           RAISE EXCEPTION 'admission facts are immutable' USING ERRCODE='23514'; END IF;
         ELSIF OLD."StartAuthorizedAt" IS NOT NULL AND ROW(OLD."PackageId",OLD."Sha256",OLD."StartAuthorizedAt",OLD."MustBeginBefore",OLD."CompatibilityDeclarationId",OLD."CompatibilityJson") IS DISTINCT FROM
             ROW(NEW."PackageId",NEW."Sha256",NEW."StartAuthorizedAt",NEW."MustBeginBefore",NEW."CompatibilityDeclarationId",NEW."CompatibilityJson") THEN
           RAISE EXCEPTION 'start permit is immutable' USING ERRCODE='23514';
         END IF;
         RETURN NEW;
        END $body$;
        CREATE TRIGGER "TR_deployment_kind" BEFORE UPDATE ON tsk.deployments FOR EACH ROW EXECUTE FUNCTION tsk.protect_rollback_facts();
        CREATE TRIGGER "TR_task_compatibility" BEFORE UPDATE ON tsk.tasks FOR EACH ROW EXECUTE FUNCTION tsk.protect_rollback_facts();
        CREATE TRIGGER "TR_attempt_compatibility" BEFORE UPDATE ON tsk.attempts FOR EACH ROW EXECUTE FUNCTION tsk.protect_rollback_facts();
        CREATE OR REPLACE FUNCTION iam.has_field(subject uuid, operation text, process uuid, device uuid, software uuid) RETURNS boolean LANGUAGE sql STABLE SECURITY INVOKER AS $$
                  SELECT EXISTS(SELECT 1 FROM iam.users u WHERE u."Id"=subject AND u."IsEnabled" AND NOT u."MustChangePassword") AND
                    (EXISTS(SELECT 1 FROM iam.permissions p WHERE p."SubjectId"=subject AND p."Operation"=operation AND (p."SoftwareId" IS NULL OR p."SoftwareId"=software))
                     OR operation='asset.create' AND EXISTS(SELECT 1 FROM iam.permissions p WHERE p."SubjectId"=subject AND p."SoftwareId" IS NULL AND p."Operation"='asset.manage')
                     OR operation='binding.manage' AND EXISTS(SELECT 1 FROM iam.permissions p WHERE p."SubjectId"=subject AND p."SoftwareId" IS NULL AND p."Operation"='asset.manage') AND
                        EXISTS(SELECT 1 FROM iam.permissions p WHERE p."SubjectId"=subject AND p."SoftwareId"=software AND p."Operation"='instance.manage')
                     OR operation IN ('asset.read','asset.create','asset.manage','software.create','binding.manage','software.read','instance.read','instance.manage','enrollment.manage','deployment.create','deployment.control','deployment.rollback','task.closeUnknown','plc.verify','plc.usage.record','audit.read') AND EXISTS(SELECT 1 FROM iam.effective_role_grants g WHERE g."SubjectId"=subject AND g."Operation"=operation AND
                        (g."ScopeKind"='Site' OR g."ScopeKind"='Process' AND g."ResourceId"=process OR g."ScopeKind"='Device' AND g."ResourceId"=device)))
                $$;
        CREATE OR REPLACE FUNCTION iam.has_software(subject uuid, operation text, software uuid) RETURNS boolean LANGUAGE sql STABLE SECURITY INVOKER AS $$
                  SELECT iam.has_product(subject,operation,software) OR operation IN ('software.read','instance.read','instance.manage','enrollment.manage','deployment.create','deployment.control','deployment.rollback','task.closeUnknown','plc.verify','plc.usage.record','audit.read') AND EXISTS(SELECT 1 FROM ins.device_software_bindings b JOIN ins.devices d ON d."Id"=b."DeviceId"
                    WHERE b."SoftwareId"=software AND b."IsActive" AND iam.has_field(subject,operation,d."ProcessId",d."Id",software))
                $$;
        """);
    protected override void Down(MigrationBuilder migrationBuilder) => throw new NotSupportedException("Preserve rollback and declaration history; use an explicit forward repair.");
    protected override void BuildTargetModel(ModelBuilder modelBuilder)
    {
        SvmDbContextModelSnapshot.Configure(modelBuilder); SvmDbContextModelSnapshot.ConfigurePersonnelV1(modelBuilder);
        SvmDbContextModelSnapshot.ConfigureOperationResultsV1(modelBuilder); BusOutboxV1Model.Configure(modelBuilder); CatalogV1Model.Configure(modelBuilder);
        InstanceAccessV1Model.Configure(modelBuilder); ReleasePackagesV1Model.Configure(modelBuilder); ReleasePublicationV1Model.Configure(modelBuilder);
        TaskWorkflowV1Model.Configure(modelBuilder); IntegrationMaterialsV1Model.Configure(modelBuilder); GitLabImportV1Model.Configure(modelBuilder);
        HardwareWorkspaceV1Model.Configure(modelBuilder); RolesAndScopesV1Model.Configure(modelBuilder); GitLabSourceV1Model.Configure(modelBuilder); PlcArchivesV1Model.Configure(modelBuilder); RollbackCompatibilityV1Model.Configure(modelBuilder);
    }
}
