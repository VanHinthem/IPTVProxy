using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
namespace IPTVProxy.Data.Migrations;
[DbContext(typeof(CatalogDb))]
[Migration("202611250007_ClassificationRefinement")]
public sealed class ClassificationRefinement : Migration
{
    protected override void Up(MigrationBuilder m) { }
    protected override void Down(MigrationBuilder m) { }
    protected override void BuildTargetModel(ModelBuilder modelBuilder)=>CatalogModelSnapshot.Build(modelBuilder, true, true, true, true, true, true, false);
}
