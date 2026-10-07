using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Svm.EntityFrameworkCore.Framework;

namespace Svm.EntityFrameworkCore.Migrations;

[DbContext(typeof(SvmDbContext))]
[Migration("20261007000200_PersonnelAdministrationPermissions")]
internal sealed class PersonnelAdministrationPermissions : Migration
{
    public PersonnelAdministrationPermissions() { }
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        INSERT INTO iam.permission_catalog ("Operation","Global") VALUES ('asset.read',true),('asset.manage',true)
        ON CONFLICT ("Operation") DO NOTHING;
        DO $svm$ BEGIN
          IF EXISTS (SELECT 1 FROM iam.permission_catalog WHERE "Operation" IN ('asset.read','asset.manage') AND NOT "Global")
          THEN RAISE EXCEPTION 'SVM asset permission definition invalid'; END IF;
        END $svm$;
        """);
    protected override void Down(MigrationBuilder migrationBuilder) =>
        throw new NotSupportedException("Permission history requires an explicit recovery plan; automatic downgrade is disabled.");
    protected override void BuildTargetModel(ModelBuilder modelBuilder)
    {
        SvmDbContextModelSnapshot.Configure(modelBuilder);
        SvmDbContextModelSnapshot.ConfigurePersonnelV1(modelBuilder);
        SvmDbContextModelSnapshot.ConfigureOperationResultsV1(modelBuilder);
        BusOutboxV1Model.Configure(modelBuilder);
    }
}
