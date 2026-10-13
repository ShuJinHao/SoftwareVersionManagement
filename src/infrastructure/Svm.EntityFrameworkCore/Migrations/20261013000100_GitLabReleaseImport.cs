using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Svm.EntityFrameworkCore.Framework;

namespace Svm.EntityFrameworkCore.Migrations;

[DbContext(typeof(SvmDbContext))]
[Migration("20261013000100_GitLabReleaseImport")]
internal sealed class GitLabReleaseImportMigration : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        CREATE TABLE rel.source_bindings (
          "Id" uuid NOT NULL,
          "SoftwareId" uuid NOT NULL,
          "SubjectId" uuid NOT NULL,
          "BaseUri" text NOT NULL,
          "ProjectId" bigint NOT NULL,
          "PackageName" text NOT NULL,
          "Initialized" boolean NOT NULL,
          "LastSyncedAt" timestamp with time zone,
          "NextScanAt" timestamp with time zone,
          "LatestWorkId" uuid,
          "LastErrorCode" text,
          PRIMARY KEY ("Id"), FOREIGN KEY ("SoftwareId") REFERENCES rel.software("Id") ON DELETE RESTRICT);
        CREATE UNIQUE INDEX "IX_source_bindings_SoftwareId" ON rel.source_bindings("SoftwareId");
        CREATE UNIQUE INDEX "IX_source_bindings_SubjectId" ON rel.source_bindings("SubjectId");
        CREATE TABLE rel.sync_works (
          "Id" uuid NOT NULL,
          "BindingId" uuid NOT NULL,
          "SoftwareId" uuid NOT NULL,
          "Kind" text NOT NULL,
          "Tag" text,
          "Stage" text NOT NULL,
          "Page" integer NOT NULL,
          "CandidateJson" text,
          "InitialScan" boolean NOT NULL,
          "ReleaseId" uuid,
          "ReceiveToken" uuid,
          "LeaseToken" uuid,
          "LeaseGeneration" bigint NOT NULL,
          "LeaseUntil" timestamp with time zone,
          "NextAttemptAt" timestamp with time zone NOT NULL,
          "LastErrorCode" text,
          "CreatedAt" timestamp with time zone NOT NULL,
          "CompletedAt" timestamp with time zone,
          "Revision" bigint NOT NULL,
          PRIMARY KEY ("Id"), FOREIGN KEY ("BindingId") REFERENCES rel.source_bindings("Id") ON DELETE RESTRICT,
          FOREIGN KEY ("ReleaseId") REFERENCES rel.releases("Id") ON DELETE RESTRICT);
        CREATE UNIQUE INDEX "IX_sync_works_BindingId_Tag" ON rel.sync_works("BindingId","Tag") WHERE "Tag" IS NOT NULL;
        CREATE UNIQUE INDEX "IX_sync_works_BindingId" ON rel.sync_works("BindingId") WHERE "Kind"='Scan' AND "Stage" NOT IN ('Completed','Conflict');
        CREATE INDEX "IX_sync_works_SoftwareId_NextAttemptAt_LeaseUntil" ON rel.sync_works("SoftwareId","NextAttemptAt","LeaseUntil");
        ALTER TABLE rel.releases ADD COLUMN "SourceKind" varchar(16) NOT NULL DEFAULT 'Manual', ADD COLUMN "SourceBindingId" uuid REFERENCES rel.source_bindings("Id") ON DELETE RESTRICT,
          ADD COLUMN "SourceTag" varchar(34), ADD COLUMN "SourceFactsJson" text;
        CREATE UNIQUE INDEX "IX_releases_SourceBindingId_SourceTag" ON rel.releases("SourceBindingId","SourceTag");
        ALTER TABLE rel.releases DROP CONSTRAINT "CK_release_publication";
        ALTER TABLE rel.releases ADD CONSTRAINT "CK_release_publication" CHECK (
          ("SourceKind"='Manual' AND "SourceBindingId" IS NULL AND "SourceTag" IS NULL AND "SourceFactsJson" IS NULL AND (("PublishedAt" IS NULL AND "PublishedBy" IS NULL AND "PublishedEmployeeNo" IS NULL AND "TestEvidenceId" IS NULL AND "PublishReason" IS NULL AND "PublishConclusion" IS NULL AND "State"<>'Formal') OR ("PublishedAt" IS NOT NULL AND "PublishedBy" IS NOT NULL AND "PublishedEmployeeNo" IS NOT NULL AND "TestEvidenceId" IS NOT NULL AND "PublishReason" IS NOT NULL AND "PublishConclusion" IS NOT NULL AND "State" IN ('Formal','Disabled'))))
          OR ("SourceKind"='GitLab' AND "SourceBindingId" IS NOT NULL AND "SourceTag" IS NOT NULL AND "SourceFactsJson" IS NOT NULL
            AND "PublishedBy" IS NULL AND "PublishedEmployeeNo" IS NULL AND "TestEvidenceId" IS NULL AND "PublishReason" IS NULL AND "PublishConclusion" IS NULL
            AND "State" IN ('Staging','Formal','Disabled') AND ("State"<>'Formal' OR "PublishedAt" IS NOT NULL)));
        CREATE FUNCTION rel.protect_source_facts() RETURNS trigger LANGUAGE plpgsql SET search_path = pg_catalog AS $body$
        BEGIN
          IF ROW(OLD."SourceKind",OLD."SourceBindingId",OLD."SourceTag",OLD."SourceFactsJson") IS DISTINCT FROM
             ROW(NEW."SourceKind",NEW."SourceBindingId",NEW."SourceTag",NEW."SourceFactsJson") THEN
            RAISE EXCEPTION 'Source facts are immutable' USING ERRCODE = '23514';
          END IF;
          RETURN NEW;
        END;
        $body$;
        CREATE TRIGGER "TR_release_source_facts" BEFORE UPDATE ON rel.releases FOR EACH ROW EXECUTE FUNCTION rel.protect_source_facts();
        """);
    protected override void Down(MigrationBuilder migrationBuilder) => throw new NotSupportedException("Imported release history must be retained.");
    protected override void BuildTargetModel(ModelBuilder modelBuilder)
    {
        SvmDbContextModelSnapshot.Configure(modelBuilder);
        SvmDbContextModelSnapshot.ConfigurePersonnelV1(modelBuilder);
        SvmDbContextModelSnapshot.ConfigureOperationResultsV1(modelBuilder);
        BusOutboxV1Model.Configure(modelBuilder); CatalogV1Model.Configure(modelBuilder);
        InstanceAccessV1Model.Configure(modelBuilder); ReleasePackagesV1Model.Configure(modelBuilder);
        ReleasePublicationV1Model.Configure(modelBuilder); TaskWorkflowV1Model.Configure(modelBuilder);
        IntegrationMaterialsV1Model.Configure(modelBuilder); GitLabImportV1Model.Configure(modelBuilder);
    }
}
