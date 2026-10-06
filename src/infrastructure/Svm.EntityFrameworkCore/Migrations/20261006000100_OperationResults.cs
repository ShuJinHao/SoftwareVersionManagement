using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Svm.EntityFrameworkCore.Framework;

namespace Svm.EntityFrameworkCore.Migrations;

[DbContext(typeof(SvmDbContext))]
[Migration("20261006000100_OperationResults")]
internal sealed class OperationResults : Migration
{
    public OperationResults() { }
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            CREATE FUNCTION framework.check_operation_result_commit() RETURNS trigger
            LANGUAGE plpgsql SET search_path=pg_catalog AS $svm_result$
            DECLARE finalized boolean;
            BEGIN
              EXECUTE format('SELECT "Status" IS NOT NULL FROM %I.operation_results WHERE "ActorKind"=$1 AND "SubjectId"=$2 AND "Operation"=$3 AND "IdempotencyKey"=$4', TG_TABLE_SCHEMA)
                INTO finalized USING NEW."ActorKind",NEW."SubjectId",NEW."Operation",NEW."IdempotencyKey";
              IF finalized IS DISTINCT FROM TRUE THEN
                RAISE EXCEPTION 'SVM operation result was not finalized' USING ERRCODE='23514';
              END IF;
              RETURN NULL;
            END;
            $svm_result$;
            CREATE FUNCTION framework.protect_operation_result() RETURNS trigger
            LANGUAGE plpgsql SET search_path=pg_catalog AS $svm_result$
            BEGIN
              IF OLD."Status" IS NOT NULL OR NEW."Status" IS NULL OR
                ROW(NEW."ActorKind",NEW."SubjectId",NEW."Operation",NEW."IdempotencyKey",NEW."RequestDigest",NEW."OperationId",NEW."CreatedAt")
                IS DISTINCT FROM ROW(OLD."ActorKind",OLD."SubjectId",OLD."Operation",OLD."IdempotencyKey",OLD."RequestDigest",OLD."OperationId",OLD."CreatedAt") THEN
                RAISE EXCEPTION 'SVM operation result is immutable' USING ERRCODE='23514';
              END IF;
              RETURN NEW;
            END;
            $svm_result$;
            """);
        foreach (var schema in new[] { "iam", "rel", "pkg", "ins", "tsk", "aud" })
            migrationBuilder.Sql($"""
                CREATE TABLE {schema}.operation_results (
                  "ActorKind" smallint NOT NULL CHECK ("ActorKind" BETWEEN 2 AND 7),
                  "SubjectId" uuid NOT NULL CHECK ("SubjectId" <> '00000000-0000-0000-0000-000000000000'),
                  "Operation" varchar(128) NOT NULL CHECK (length("Operation") > 0),
                  "IdempotencyKey" uuid NOT NULL CHECK ("IdempotencyKey" <> '00000000-0000-0000-0000-000000000000'),
                  "RequestDigest" varchar(64) NOT NULL CHECK (length("RequestDigest")=64 AND "RequestDigest" ~ '^[0-9a-f]+$'),
                  "OperationId" uuid NOT NULL CHECK ("OperationId" <> '00000000-0000-0000-0000-000000000000'),
                  "Status" smallint, "ResourceId" uuid, "WorkId" uuid,
                  "CreatedAt" timestamptz NOT NULL DEFAULT clock_timestamp(), "CompletedAt" timestamptz,
                  PRIMARY KEY ("ActorKind","SubjectId","Operation","IdempotencyKey"),
                  CHECK ("ResourceId" IS NULL OR "ResourceId" <> '00000000-0000-0000-0000-000000000000'),
                  CHECK ("WorkId" IS NULL OR "WorkId" <> '00000000-0000-0000-0000-000000000000'),
                  CHECK (("Status" IS NULL AND "ResourceId" IS NULL AND "WorkId" IS NULL AND "CompletedAt" IS NULL)
                    OR ("Status"=1 AND "WorkId" IS NOT NULL AND "CompletedAt" IS NOT NULL)
                    OR ("Status"=2 AND "WorkId" IS NULL AND "CompletedAt" IS NOT NULL))
                );
                CREATE UNIQUE INDEX "IX_operation_results_OperationId" ON {schema}.operation_results ("OperationId");
                CREATE TRIGGER protect_operation_result BEFORE UPDATE ON {schema}.operation_results
                  FOR EACH ROW EXECUTE FUNCTION framework.protect_operation_result();
                CREATE CONSTRAINT TRIGGER check_operation_result_commit AFTER INSERT OR UPDATE ON {schema}.operation_results
                  DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION framework.check_operation_result_commit();
                """);
    }
    protected override void Down(MigrationBuilder migrationBuilder) =>
        throw new NotSupportedException("Retained operation keys require an explicit recovery plan; automatic downgrade is disabled.");
    protected override void BuildTargetModel(ModelBuilder modelBuilder)
    {
        SvmDbContextModelSnapshot.Configure(modelBuilder);
        SvmDbContextModelSnapshot.ConfigurePersonnelV1(modelBuilder);
        SvmDbContextModelSnapshot.ConfigureOperationResultsV1(modelBuilder);
    }
}
