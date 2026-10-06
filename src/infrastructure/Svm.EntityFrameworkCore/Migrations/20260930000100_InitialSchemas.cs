using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Svm.EntityFrameworkCore.Framework;

namespace Svm.EntityFrameworkCore.Migrations;

[DbContext(typeof(SvmDbContext))]
[Migration("20260930000100_InitialSchemas")]
internal sealed class InitialSchemas : Migration
{
    public InitialSchemas() { }
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        foreach (var schema in new[] { "iam", "rel", "pkg", "ins", "tsk", "aud", "framework" })
            migrationBuilder.EnsureSchema(schema);
    }
    protected override void Down(MigrationBuilder migrationBuilder) =>
        throw new NotSupportedException("Schema rollback is not an automatic database reset operation.");
    protected override void BuildTargetModel(ModelBuilder modelBuilder) => SvmDbContextModelSnapshot.Configure(modelBuilder);
}
