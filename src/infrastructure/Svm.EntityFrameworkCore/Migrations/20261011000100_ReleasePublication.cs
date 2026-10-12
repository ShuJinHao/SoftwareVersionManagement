using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Svm.EntityFrameworkCore.Framework;

namespace Svm.EntityFrameworkCore.Migrations;

[DbContext(typeof(SvmDbContext))]
[Migration("20261011000100_ReleasePublication")]
internal sealed class ReleasePublicationMigration : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        ALTER TABLE rel.releases
          ADD COLUMN "PublishedBy" uuid,
          ADD COLUMN "PublishedEmployeeNo" varchar(64),
          ADD COLUMN "PublishedAt" timestamp with time zone,
          ADD COLUMN "TestEvidenceId" uuid,
          ADD COLUMN "PublishReason" varchar(256),
          ADD COLUMN "PublishConclusion" varchar(2000);
        CREATE INDEX "IX_releases_SoftwareId_PublishedAt" ON rel.releases ("SoftwareId","PublishedAt");
        CREATE UNIQUE INDEX "IX_installation_evidence_Id_InstalledReleaseId_SoftwareId"
          ON ins.installation_evidence ("Id","InstalledReleaseId","SoftwareId");
        ALTER TABLE rel.releases ADD CONSTRAINT "FK_release_publication_evidence"
          FOREIGN KEY ("TestEvidenceId","Id","SoftwareId") REFERENCES ins.installation_evidence ("Id","InstalledReleaseId","SoftwareId") ON DELETE RESTRICT;
        ALTER TABLE rel.releases ADD CONSTRAINT "FK_release_publication_person"
          FOREIGN KEY ("PublishedBy") REFERENCES iam.users ("Id") ON DELETE RESTRICT;
        ALTER TABLE rel.releases ADD CONSTRAINT "CK_release_publication" CHECK (
          ("PublishedAt" IS NULL AND "PublishedBy" IS NULL AND "PublishedEmployeeNo" IS NULL
            AND "TestEvidenceId" IS NULL AND "PublishReason" IS NULL AND "PublishConclusion" IS NULL AND "State"<>'Formal')
          OR ("PublishedAt" IS NOT NULL AND "PublishedBy" IS NOT NULL AND "PublishedEmployeeNo" IS NOT NULL
            AND "TestEvidenceId" IS NOT NULL AND "PublishReason" IS NOT NULL AND "PublishConclusion" IS NOT NULL AND "State" IN ('Formal','Disabled')));
        CREATE FUNCTION rel.protect_publication_facts() RETURNS trigger LANGUAGE plpgsql SET search_path = pg_catalog AS $body$
        BEGIN
          IF OLD."PublishedAt" IS NOT NULL AND
             ROW(OLD."PublishedBy",OLD."PublishedEmployeeNo",OLD."PublishedAt",OLD."TestEvidenceId",OLD."PublishReason",OLD."PublishConclusion") IS DISTINCT FROM
             ROW(NEW."PublishedBy",NEW."PublishedEmployeeNo",NEW."PublishedAt",NEW."TestEvidenceId",NEW."PublishReason",NEW."PublishConclusion") THEN
            RAISE EXCEPTION 'Publication facts are immutable' USING ERRCODE = '23514';
          END IF;
          RETURN NEW;
        END;
        $body$;
        CREATE TRIGGER "TR_release_publication_facts" BEFORE UPDATE ON rel.releases
          FOR EACH ROW EXECUTE FUNCTION rel.protect_publication_facts();
        """);
    protected override void Down(MigrationBuilder migrationBuilder) => throw new NotSupportedException("Publication and evidence history must be retained.");
    protected override void BuildTargetModel(ModelBuilder modelBuilder)
    {
        SvmDbContextModelSnapshot.Configure(modelBuilder);
        SvmDbContextModelSnapshot.ConfigurePersonnelV1(modelBuilder);
        SvmDbContextModelSnapshot.ConfigureOperationResultsV1(modelBuilder);
        BusOutboxV1Model.Configure(modelBuilder);
        CatalogV1Model.Configure(modelBuilder);
        InstanceAccessV1Model.Configure(modelBuilder);
        ReleasePackagesV1Model.Configure(modelBuilder);
        ReleasePublicationV1Model.Configure(modelBuilder);
    }
}
