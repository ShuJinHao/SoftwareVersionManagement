using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Svm.EntityFrameworkCore.Framework;
namespace Svm.EntityFrameworkCore.Migrations;

[DbContext(typeof(SvmDbContext))]
[Migration("20261028000100_PlcArchives")]
internal sealed class PlcArchives : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        ALTER TABLE rel.operation_results DROP CONSTRAINT "operation_results_ActorKind_check";
        ALTER TABLE rel.operation_results ADD CONSTRAINT "operation_results_ActorKind_check" CHECK ("ActorKind" BETWEEN 2 AND 8);
        ALTER TABLE pkg.operation_results DROP CONSTRAINT "operation_results_ActorKind_check";
        ALTER TABLE pkg.operation_results ADD CONSTRAINT "operation_results_ActorKind_check" CHECK ("ActorKind" BETWEEN 2 AND 8);
        CREATE TABLE iam.plc_upload_credentials (
          "Id" uuid NOT NULL,
          "SoftwareId" uuid NOT NULL,
          "Name" varchar(128) NOT NULL,
          "SecretHash" varchar(256) NOT NULL,
          "ScopeJson" jsonb NOT NULL,
          "ExpiresAt" timestamp with time zone NOT NULL,
          "CreatedBy" uuid NOT NULL,
          "CreatedAt" timestamp with time zone NOT NULL,
          "RevokedAt" timestamp with time zone,
          "Revision" bigint NOT NULL,
          CONSTRAINT "PK_plc_upload_credentials" PRIMARY KEY ("Id")
        );
        CREATE INDEX "IX_plc_upload_credentials_SoftwareId_Id" ON iam.plc_upload_credentials("SoftwareId","Id");
        CREATE TABLE ins.plc_usage_records (
          "Id" uuid NOT NULL,
          "HardwareId" uuid NOT NULL,
          "BindingId" uuid NOT NULL,
          "BindingRevision" bigint NOT NULL,
          "SoftwareId" uuid NOT NULL,
          "ReleaseId" uuid NOT NULL,
          "Version" varchar(128) NOT NULL,
          "ReleaseState" varchar(32) NOT NULL,
          "PreviousReleaseId" uuid,
          "FieldAt" timestamp with time zone NOT NULL,
          "RecordedAt" timestamp with time zone NOT NULL,
          "ActorId" uuid NOT NULL,
          "EmployeeNo" varchar(64) NOT NULL,
          "Note" varchar(2000) NOT NULL,
          "Materials" varchar(2000) NOT NULL,
          "CorrectsId" uuid,
          CONSTRAINT "PK_plc_usage_records" PRIMARY KEY ("Id")
        );
        CREATE INDEX "IX_plc_usage_records_HardwareId_Id" ON ins.plc_usage_records("HardwareId","Id");
        CREATE INDEX "IX_plc_usage_records_BindingId_BindingRevision_FieldAt" ON ins.plc_usage_records("BindingId","BindingRevision","FieldAt");
        CREATE UNIQUE INDEX "IX_plc_usage_records_CorrectsId" ON ins.plc_usage_records("CorrectsId");
        CREATE TABLE ins.plc_usage_state (
          "Id" uuid NOT NULL,
          "BindingRevision" bigint NOT NULL,
          "Revision" bigint NOT NULL,
          "CurrentRecordId" uuid,
          CONSTRAINT "PK_plc_usage_state" PRIMARY KEY ("Id")
        );
        CREATE TABLE rel.plc_publications (
          "Id" uuid NOT NULL,
          "ReleaseId" uuid NOT NULL,
          "HardwareId" uuid NOT NULL,
          "BindingId" uuid NOT NULL,
          "BindingRevision" bigint NOT NULL,
          "FieldAt" timestamp with time zone NOT NULL,
          "Verification" varchar(2000) NOT NULL,
          "Materials" varchar(2000) NOT NULL,
          "ActorId" uuid NOT NULL,
          "EmployeeNo" varchar(64) NOT NULL,
          "RecordedAt" timestamp with time zone NOT NULL,
          CONSTRAINT "PK_plc_publications" PRIMARY KEY ("Id")
        );
        CREATE UNIQUE INDEX "IX_plc_publications_ReleaseId" ON rel.plc_publications("ReleaseId");
        ALTER TABLE ins.hardware_software_bindings ADD COLUMN "HardwareType" varchar(8) NOT NULL DEFAULT 'IPC';
        CREATE UNIQUE INDEX "IX_hardware_software_bindings_HardwareId" ON ins.hardware_software_bindings("HardwareId") WHERE "IsActive" AND "HardwareType"='PLC';
        ALTER TABLE rel.releases ADD COLUMN "PlcEvidenceId" uuid, ADD COLUMN "PlcUploadBindingId" uuid, ADD COLUMN "PlcUploadBindingRevision" bigint;
        ALTER TABLE pkg.download_sessions ADD COLUMN "PlcBindingId" uuid, ADD COLUMN "PlcBindingRevision" bigint;
        ALTER TABLE rel.releases DROP CONSTRAINT "CK_release_publication";
        ALTER TABLE rel.releases ADD CONSTRAINT "CK_release_publication" CHECK (
                  ("SourceKind"='Manual' AND "SourceBindingId" IS NULL AND "SourceTag" IS NULL AND "SourceFactsJson" IS NULL AND (("PublishedAt" IS NULL AND "PublishedBy" IS NULL AND "PublishedEmployeeNo" IS NULL AND "TestEvidenceId" IS NULL AND "PublishReason" IS NULL AND "PublishConclusion" IS NULL AND "State"<>'Formal') OR ("PublishedAt" IS NOT NULL AND "PublishedBy" IS NOT NULL AND "PublishedEmployeeNo" IS NOT NULL AND "TestEvidenceId" IS NOT NULL AND "PublishReason" IS NOT NULL AND "PublishConclusion" IS NOT NULL AND "State" IN ('Formal','Disabled'))))
                  OR ("SourceKind"='GitLab' AND "SourceBindingId" IS NOT NULL AND "SourceTag" IS NOT NULL AND "SourceFactsJson" IS NOT NULL
                    AND "PublishedBy" IS NULL AND "PublishedEmployeeNo" IS NULL AND "TestEvidenceId" IS NULL AND "PublishReason" IS NULL AND "PublishConclusion" IS NULL
                    AND "State" IN ('Staging','Formal','Disabled') AND ("State"<>'Formal' OR "PublishedAt" IS NOT NULL))
                  OR ("SourceKind"='PLC' AND "SourceBindingId" IS NULL AND "SourceTag" IS NULL AND "SourceFactsJson" IS NULL AND "TestEvidenceId" IS NULL
                    AND (("PublishedAt" IS NULL AND "PlcEvidenceId" IS NULL AND "PublishedBy" IS NULL AND "PublishedEmployeeNo" IS NULL AND "PublishReason" IS NULL AND "PublishConclusion" IS NULL AND "State"<>'Formal')
                      OR ("PublishedAt" IS NOT NULL AND "PlcEvidenceId" IS NOT NULL AND "PublishedBy" IS NOT NULL AND "PublishedEmployeeNo" IS NOT NULL AND "PublishReason" IS NOT NULL AND "PublishConclusion" IS NOT NULL AND "State" IN ('Formal','Disabled')))));

        ALTER TABLE iam.plc_upload_credentials ADD FOREIGN KEY ("SoftwareId") REFERENCES rel.software("Id") ON DELETE NO ACTION DEFERRABLE INITIALLY DEFERRED;
        ALTER TABLE iam.plc_upload_credentials ADD FOREIGN KEY ("CreatedBy") REFERENCES iam.users("Id") ON DELETE NO ACTION DEFERRABLE INITIALLY DEFERRED;
        ALTER TABLE ins.plc_usage_records ADD FOREIGN KEY ("HardwareId") REFERENCES ins.hardware("Id") ON DELETE NO ACTION DEFERRABLE INITIALLY DEFERRED;
        ALTER TABLE ins.plc_usage_records ADD FOREIGN KEY ("BindingId") REFERENCES ins.hardware_software_bindings("Id") ON DELETE NO ACTION DEFERRABLE INITIALLY DEFERRED;
        ALTER TABLE ins.plc_usage_records ADD FOREIGN KEY ("ReleaseId") REFERENCES rel.releases("Id") ON DELETE NO ACTION DEFERRABLE INITIALLY DEFERRED;
        ALTER TABLE ins.plc_usage_records ADD FOREIGN KEY ("PreviousReleaseId") REFERENCES rel.releases("Id") ON DELETE NO ACTION DEFERRABLE INITIALLY DEFERRED;
        ALTER TABLE ins.plc_usage_records ADD FOREIGN KEY ("ActorId") REFERENCES iam.users("Id") ON DELETE NO ACTION DEFERRABLE INITIALLY DEFERRED;
        ALTER TABLE ins.plc_usage_records ADD FOREIGN KEY ("CorrectsId") REFERENCES ins.plc_usage_records("Id") ON DELETE NO ACTION DEFERRABLE INITIALLY DEFERRED;
        ALTER TABLE ins.plc_usage_state ADD FOREIGN KEY ("Id") REFERENCES ins.hardware_software_bindings("Id") ON DELETE NO ACTION DEFERRABLE INITIALLY DEFERRED;
        ALTER TABLE ins.plc_usage_state ADD FOREIGN KEY ("CurrentRecordId") REFERENCES ins.plc_usage_records("Id") ON DELETE NO ACTION DEFERRABLE INITIALLY DEFERRED;
        ALTER TABLE rel.plc_publications ADD FOREIGN KEY ("ReleaseId") REFERENCES rel.releases("Id") ON DELETE NO ACTION DEFERRABLE INITIALLY DEFERRED;
        ALTER TABLE rel.plc_publications ADD FOREIGN KEY ("HardwareId") REFERENCES ins.hardware("Id") ON DELETE NO ACTION DEFERRABLE INITIALLY DEFERRED;
        ALTER TABLE rel.plc_publications ADD FOREIGN KEY ("BindingId") REFERENCES ins.hardware_software_bindings("Id") ON DELETE NO ACTION DEFERRABLE INITIALLY DEFERRED;
        ALTER TABLE rel.plc_publications ADD FOREIGN KEY ("ActorId") REFERENCES iam.users("Id") ON DELETE NO ACTION DEFERRABLE INITIALLY DEFERRED;
        ALTER TABLE rel.releases ADD FOREIGN KEY ("PlcEvidenceId") REFERENCES rel.plc_publications("Id") ON DELETE NO ACTION DEFERRABLE INITIALLY DEFERRED;
        ALTER TABLE rel.releases ADD FOREIGN KEY ("PlcUploadBindingId") REFERENCES ins.hardware_software_bindings("Id") ON DELETE NO ACTION DEFERRABLE INITIALLY DEFERRED;
        ALTER TABLE pkg.download_sessions ADD FOREIGN KEY ("PlcBindingId") REFERENCES ins.hardware_software_bindings("Id") ON DELETE NO ACTION DEFERRABLE INITIALLY DEFERRED;
        INSERT INTO iam.permission_catalog ("Operation","Global") VALUES ('plc.verify',false),('plc.usage.record',false),('plc.uploadCredential.manage',false);
        CREATE FUNCTION ins.check_plc_binding() RETURNS trigger LANGUAGE plpgsql AS $body$
        DECLARE kind text; category text;
        BEGIN
         SELECT "Type" INTO kind FROM ins.hardware WHERE "Id"=NEW."HardwareId";
         SELECT "Category" INTO category FROM rel.software WHERE "Id"=NEW."SoftwareId";
         IF kind IS DISTINCT FROM NEW."HardwareType" OR (kind='PLC') IS DISTINCT FROM (category='PLC') THEN
           RAISE EXCEPTION 'hardware and software category mismatch' USING ERRCODE='23514';
         END IF;
         RETURN NEW;
        END $body$;
        CREATE TRIGGER "TR_plc_binding_type" BEFORE INSERT OR UPDATE ON ins.hardware_software_bindings FOR EACH ROW EXECUTE FUNCTION ins.check_plc_binding();
        CREATE FUNCTION ins.plc_append_only() RETURNS trigger LANGUAGE plpgsql AS $body$
        BEGIN RAISE EXCEPTION 'PLC evidence is append only' USING ERRCODE='23514'; END $body$;
        CREATE TRIGGER "TR_plc_usage_history" BEFORE UPDATE OR DELETE ON ins.plc_usage_records FOR EACH ROW EXECUTE FUNCTION ins.plc_append_only();
        CREATE TRIGGER "TR_plc_publication_history" BEFORE UPDATE OR DELETE ON rel.plc_publications FOR EACH ROW EXECUTE FUNCTION ins.plc_append_only();
        CREATE FUNCTION rel.protect_plc_source() RETURNS trigger LANGUAGE plpgsql AS $body$
        BEGIN
         IF ROW(OLD."PlcUploadBindingId",OLD."PlcUploadBindingRevision") IS DISTINCT FROM ROW(NEW."PlcUploadBindingId",NEW."PlcUploadBindingRevision") OR
            OLD."PlcEvidenceId" IS NOT NULL AND OLD."PlcEvidenceId" IS DISTINCT FROM NEW."PlcEvidenceId" THEN
           RAISE EXCEPTION 'PLC source is immutable' USING ERRCODE='23514';
         END IF;
         RETURN NEW;
        END $body$;
        CREATE TRIGGER "TR_plc_release_facts" BEFORE UPDATE ON rel.releases FOR EACH ROW EXECUTE FUNCTION rel.protect_plc_source();
        CREATE FUNCTION iam.protect_plc_credential() RETURNS trigger LANGUAGE plpgsql AS $body$
        BEGIN
         IF ROW(OLD."Id",OLD."SoftwareId",OLD."Name",OLD."SecretHash",OLD."ScopeJson",OLD."ExpiresAt",OLD."CreatedBy",OLD."CreatedAt") IS DISTINCT FROM
            ROW(NEW."Id",NEW."SoftwareId",NEW."Name",NEW."SecretHash",NEW."ScopeJson",NEW."ExpiresAt",NEW."CreatedBy",NEW."CreatedAt") OR
            OLD."RevokedAt" IS NOT NULL THEN
           RAISE EXCEPTION 'PLC credential scope is immutable' USING ERRCODE='23514';
         END IF;
         RETURN NEW;
        END $body$;
        CREATE TRIGGER "TR_plc_credential_facts" BEFORE UPDATE ON iam.plc_upload_credentials FOR EACH ROW EXECUTE FUNCTION iam.protect_plc_credential();
        CREATE OR REPLACE FUNCTION iam.has_product(subject uuid, operation text, software uuid) RETURNS boolean LANGUAGE sql STABLE SECURITY INVOKER AS $$
                  SELECT EXISTS(SELECT 1 FROM iam.users u WHERE u."Id"=subject AND u."IsEnabled" AND NOT u."MustChangePassword") AND
                    (EXISTS(SELECT 1 FROM iam.permissions p WHERE p."SubjectId"=subject AND p."SoftwareId"=software AND p."Operation"=operation)
                     OR operation IN ('software.read','release.source.manage','plc.uploadCredential.manage','release.upload','release.publish','release.disable','audit.read') AND EXISTS(SELECT 1 FROM iam.effective_role_grants g WHERE g."SubjectId"=subject AND g."Operation"=operation AND g."ScopeKind"='Software' AND g."ResourceId"=software))
                $$;
        CREATE OR REPLACE FUNCTION iam.has_field(subject uuid, operation text, process uuid, device uuid, software uuid) RETURNS boolean LANGUAGE sql STABLE SECURITY INVOKER AS $$
                  SELECT EXISTS(SELECT 1 FROM iam.users u WHERE u."Id"=subject AND u."IsEnabled" AND NOT u."MustChangePassword") AND
                    (EXISTS(SELECT 1 FROM iam.permissions p WHERE p."SubjectId"=subject AND p."Operation"=operation AND (p."SoftwareId" IS NULL OR p."SoftwareId"=software))
                     OR operation='asset.create' AND EXISTS(SELECT 1 FROM iam.permissions p WHERE p."SubjectId"=subject AND p."SoftwareId" IS NULL AND p."Operation"='asset.manage')
                     OR operation='binding.manage' AND EXISTS(SELECT 1 FROM iam.permissions p WHERE p."SubjectId"=subject AND p."SoftwareId" IS NULL AND p."Operation"='asset.manage') AND
                        EXISTS(SELECT 1 FROM iam.permissions p WHERE p."SubjectId"=subject AND p."SoftwareId"=software AND p."Operation"='instance.manage')
                     OR operation IN ('asset.read','asset.create','asset.manage','software.create','binding.manage','software.read','instance.read','instance.manage','enrollment.manage','deployment.create','deployment.control','task.closeUnknown','plc.verify','plc.usage.record','audit.read') AND EXISTS(SELECT 1 FROM iam.effective_role_grants g WHERE g."SubjectId"=subject AND g."Operation"=operation AND
                        (g."ScopeKind"='Site' OR g."ScopeKind"='Process' AND g."ResourceId"=process OR g."ScopeKind"='Device' AND g."ResourceId"=device)))
                $$;
        CREATE OR REPLACE FUNCTION iam.has_software(subject uuid, operation text, software uuid) RETURNS boolean LANGUAGE sql STABLE SECURITY INVOKER AS $$
                  SELECT iam.has_product(subject,operation,software) OR operation IN ('software.read','instance.read','instance.manage','enrollment.manage','deployment.create','deployment.control','task.closeUnknown','plc.verify','plc.usage.record','audit.read') AND EXISTS(SELECT 1 FROM ins.device_software_bindings b JOIN ins.devices d ON d."Id"=b."DeviceId"
                    WHERE b."SoftwareId"=software AND b."IsActive" AND iam.has_field(subject,operation,d."ProcessId",d."Id",software))
                $$;
        """);
    protected override void Down(MigrationBuilder migrationBuilder) => throw new NotSupportedException("Preserve PLC history; use an explicit forward repair.");
    protected override void BuildTargetModel(ModelBuilder modelBuilder)
    {
        SvmDbContextModelSnapshot.Configure(modelBuilder); SvmDbContextModelSnapshot.ConfigurePersonnelV1(modelBuilder);
        SvmDbContextModelSnapshot.ConfigureOperationResultsV1(modelBuilder); BusOutboxV1Model.Configure(modelBuilder); CatalogV1Model.Configure(modelBuilder);
        InstanceAccessV1Model.Configure(modelBuilder); ReleasePackagesV1Model.Configure(modelBuilder); ReleasePublicationV1Model.Configure(modelBuilder);
        TaskWorkflowV1Model.Configure(modelBuilder); IntegrationMaterialsV1Model.Configure(modelBuilder); GitLabImportV1Model.Configure(modelBuilder);
        HardwareWorkspaceV1Model.Configure(modelBuilder); RolesAndScopesV1Model.Configure(modelBuilder); GitLabSourceV1Model.Configure(modelBuilder); PlcArchivesV1Model.Configure(modelBuilder);
    }
}
