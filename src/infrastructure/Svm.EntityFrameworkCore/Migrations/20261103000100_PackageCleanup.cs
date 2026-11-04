using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Svm.EntityFrameworkCore.Framework;
namespace Svm.EntityFrameworkCore.Migrations;

[DbContext(typeof(SvmDbContext))]
[Migration("20261103000100_PackageCleanup")]
internal sealed class PackageCleanup : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        CREATE TABLE pkg.cleanup_intents (
          "Id" uuid NOT NULL, "PackageId" uuid NOT NULL, "RequestedBy" uuid NOT NULL,
          "CreatedAt" timestamp with time zone NOT NULL, "Reason" varchar(256) NOT NULL,
          CONSTRAINT "PK_cleanup_intents" PRIMARY KEY ("Id")
        );
        CREATE UNIQUE INDEX "IX_cleanup_intents_PackageId" ON pkg.cleanup_intents("PackageId");
        CREATE UNIQUE INDEX "IX_works_one_cleanup" ON pkg.works("PackageId") WHERE "Kind"='Cleanup';
        ALTER TABLE pkg.cleanup_intents ADD FOREIGN KEY ("Id") REFERENCES pkg.works("Id") ON DELETE NO ACTION DEFERRABLE INITIALLY DEFERRED;
        ALTER TABLE pkg.cleanup_intents ADD FOREIGN KEY ("PackageId") REFERENCES pkg.packages("Id") ON DELETE NO ACTION DEFERRABLE INITIALLY DEFERRED;
        ALTER TABLE pkg.cleanup_intents ADD FOREIGN KEY ("RequestedBy") REFERENCES iam.users("Id") ON DELETE NO ACTION DEFERRABLE INITIALLY DEFERRED;
        CREATE TABLE pkg.download_stop_evidence (
          "Id" uuid NOT NULL, "PackageId" uuid NOT NULL, "NodeId" varchar(64) NOT NULL, "WorkerGeneration" uuid NOT NULL,
          "StoppedAt" timestamp with time zone NOT NULL, "RecordedBy" uuid NOT NULL, "RecordedAt" timestamp with time zone NOT NULL,
          "Evidence" varchar(2000) NOT NULL, "Reason" varchar(256) NOT NULL,
          CONSTRAINT "PK_download_stop_evidence" PRIMARY KEY ("Id"),
          CHECK ("StoppedAt"<="RecordedAt")
        );
        CREATE INDEX "IX_download_stop_lookup" ON pkg.download_stop_evidence("PackageId","NodeId","WorkerGeneration","StoppedAt");
        ALTER TABLE pkg.download_stop_evidence ADD FOREIGN KEY ("PackageId") REFERENCES pkg.packages("Id") ON DELETE NO ACTION DEFERRABLE INITIALLY DEFERRED;
        ALTER TABLE pkg.download_stop_evidence ADD FOREIGN KEY ("RecordedBy") REFERENCES iam.users("Id") ON DELETE NO ACTION DEFERRABLE INITIALLY DEFERRED;
        CREATE FUNCTION pkg.protect_cleanup_history() RETURNS trigger LANGUAGE plpgsql AS $body$
        BEGIN RAISE EXCEPTION 'cleanup facts are append only' USING ERRCODE='23514'; END $body$;
        CREATE TRIGGER "TR_cleanup_history" BEFORE UPDATE OR DELETE ON pkg.cleanup_intents FOR EACH ROW EXECUTE FUNCTION pkg.protect_cleanup_history();
        CREATE TRIGGER "TR_download_stop_history" BEFORE UPDATE OR DELETE ON pkg.download_stop_evidence FOR EACH ROW EXECUTE FUNCTION pkg.protect_cleanup_history();
        ALTER TABLE pkg.packages ADD CONSTRAINT "CK_cleanup_disabled" CHECK ("State" NOT IN ('Cleaning','Cleaned') OR "Disabled");
        CREATE FUNCTION pkg.protect_cleanup_state() RETURNS trigger LANGUAGE plpgsql AS $body$
        BEGIN
          IF OLD."State"='Cleaned' AND NEW."State"<>'Cleaned' OR OLD."State"='Cleaning' AND NEW."State" NOT IN ('Cleaning','Cleaned') THEN
            RAISE EXCEPTION 'cleanup cannot reopen package content' USING ERRCODE='23514';
          END IF;
          RETURN NEW;
        END $body$;
        CREATE TRIGGER "TR_cleanup_state" BEFORE UPDATE ON pkg.packages FOR EACH ROW EXECUTE FUNCTION pkg.protect_cleanup_state();
        CREATE OR REPLACE FUNCTION iam.has_product(subject uuid, operation text, software uuid) RETURNS boolean LANGUAGE sql STABLE SECURITY INVOKER AS $$
          SELECT EXISTS(SELECT 1 FROM iam.users u WHERE u."Id"=subject AND u."IsEnabled" AND NOT u."MustChangePassword") AND
            (EXISTS(SELECT 1 FROM iam.permissions p WHERE p."SubjectId"=subject AND p."SoftwareId"=software AND p."Operation"=operation)
             OR operation IN ('software.read','release.source.manage','plc.uploadCredential.manage','release.upload','release.publish','release.disable','package.clean','audit.read') AND EXISTS(
                SELECT 1 FROM iam.effective_role_grants g WHERE g."SubjectId"=subject AND g."Operation"=operation AND g."ScopeKind"='Software' AND g."ResourceId"=software))
        $$;
        """);
    protected override void Down(MigrationBuilder migrationBuilder) => throw new NotSupportedException("Preserve cleanup history; use an explicit forward repair.");
    protected override void BuildTargetModel(ModelBuilder modelBuilder)
    {
        SvmDbContextModelSnapshot.Configure(modelBuilder); SvmDbContextModelSnapshot.ConfigurePersonnelV1(modelBuilder);
        SvmDbContextModelSnapshot.ConfigureOperationResultsV1(modelBuilder); BusOutboxV1Model.Configure(modelBuilder); CatalogV1Model.Configure(modelBuilder);
        InstanceAccessV1Model.Configure(modelBuilder); ReleasePackagesV1Model.Configure(modelBuilder); ReleasePublicationV1Model.Configure(modelBuilder);
        TaskWorkflowV1Model.Configure(modelBuilder); IntegrationMaterialsV1Model.Configure(modelBuilder); GitLabImportV1Model.Configure(modelBuilder);
        HardwareWorkspaceV1Model.Configure(modelBuilder); RolesAndScopesV1Model.Configure(modelBuilder); GitLabSourceV1Model.Configure(modelBuilder);
        PlcArchivesV1Model.Configure(modelBuilder); RollbackCompatibilityV1Model.Configure(modelBuilder); PackageCleanupV1Model.Configure(modelBuilder);
    }
}
