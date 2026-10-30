using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Svm.EntityFrameworkCore.Framework;

namespace Svm.EntityFrameworkCore.Migrations;

[DbContext(typeof(SvmDbContext))]
[Migration("20261025000100_GitLabSourceManagement")]
internal sealed class GitLabSourceManagement : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        ALTER TABLE rel.source_bindings
          ADD COLUMN "ConnectionId" uuid,
          ADD COLUMN "Managed" boolean NOT NULL DEFAULT false,
          ADD COLUMN "IsCurrent" boolean NOT NULL DEFAULT true,
          ADD COLUMN "Enabled" boolean NOT NULL DEFAULT true,
          ADD COLUMN "Revision" bigint NOT NULL DEFAULT 1,
          ADD COLUMN "SourceRevision" bigint NOT NULL DEFAULT 1,
          ADD CONSTRAINT "CK_source_bindings_management" CHECK
            (("Managed" = ("ConnectionId" IS NOT NULL)) AND "SourceRevision">0 AND "Revision">= "SourceRevision");
        DROP INDEX rel."IX_source_bindings_SoftwareId";
        CREATE UNIQUE INDEX "IX_source_bindings_SoftwareId_IsCurrent" ON rel.source_bindings("SoftwareId","IsCurrent") WHERE "IsCurrent";
        CREATE TABLE rel.source_changes (
          "Id" uuid PRIMARY KEY,
          "BindingId" uuid NOT NULL REFERENCES rel.source_bindings("Id") ON DELETE RESTRICT,
          "Revision" bigint NOT NULL CHECK ("Revision">0),
          "Enabled" boolean NOT NULL,
          "CreatedAt" timestamp with time zone NOT NULL);
        CREATE UNIQUE INDEX "IX_source_changes_BindingId_Revision" ON rel.source_changes("BindingId","Revision");
        INSERT INTO iam.permission_catalog ("Operation","Global") VALUES ('release.source.manage',false);
        CREATE FUNCTION rel.protect_source_identity() RETURNS trigger LANGUAGE plpgsql AS $body$
        BEGIN
          IF ROW(OLD."Id",OLD."SoftwareId",OLD."SubjectId",OLD."BaseUri",OLD."ProjectId",OLD."PackageName") IS DISTINCT FROM
             ROW(NEW."Id",NEW."SoftwareId",NEW."SubjectId",NEW."BaseUri",NEW."ProjectId",NEW."PackageName") OR
             OLD."Managed" AND (NOT NEW."Managed" OR ROW(OLD."ConnectionId",OLD."SourceRevision") IS DISTINCT FROM ROW(NEW."ConnectionId",NEW."SourceRevision")) OR
             NOT OLD."IsCurrent" AND NEW."IsCurrent" THEN
            RAISE EXCEPTION 'immutable release source' USING ERRCODE='23514';
          END IF;
          RETURN NEW;
        END $body$;
        CREATE TRIGGER "TR_source_binding_identity" BEFORE UPDATE ON rel.source_bindings FOR EACH ROW EXECUTE FUNCTION rel.protect_source_identity();
        """);
    protected override void Down(MigrationBuilder migrationBuilder) => throw new NotSupportedException("Preserve source history; use an explicit forward repair.");
    protected override void BuildTargetModel(ModelBuilder modelBuilder)
    {
        SvmDbContextModelSnapshot.Configure(modelBuilder); SvmDbContextModelSnapshot.ConfigurePersonnelV1(modelBuilder);
        SvmDbContextModelSnapshot.ConfigureOperationResultsV1(modelBuilder); BusOutboxV1Model.Configure(modelBuilder); CatalogV1Model.Configure(modelBuilder);
        InstanceAccessV1Model.Configure(modelBuilder); ReleasePackagesV1Model.Configure(modelBuilder); ReleasePublicationV1Model.Configure(modelBuilder);
        TaskWorkflowV1Model.Configure(modelBuilder); IntegrationMaterialsV1Model.Configure(modelBuilder); GitLabImportV1Model.Configure(modelBuilder);
        HardwareWorkspaceV1Model.Configure(modelBuilder); RolesAndScopesV1Model.Configure(modelBuilder); GitLabSourceV1Model.Configure(modelBuilder);
    }
}
