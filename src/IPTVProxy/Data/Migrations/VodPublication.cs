using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
namespace IPTVProxy.Data.Migrations;
[DbContext(typeof(CatalogDb))]
[Migration("202611260002_VodPublication")]
public sealed class VodPublication : Migration
{
    protected override void Up(MigrationBuilder m)=>m.Sql("""
        CREATE TABLE VodGroups (Id INTEGER PRIMARY KEY AUTOINCREMENT, Kind TEXT NOT NULL CHECK(Kind IN ('movie','series')), Name TEXT NOT NULL, Enabled INTEGER NOT NULL DEFAULT 1, SortOrder INTEGER NOT NULL DEFAULT 0);
        CREATE TABLE VodGroupSources (GroupId INTEGER NOT NULL REFERENCES VodGroups(Id) ON DELETE CASCADE, CategoryId INTEGER NOT NULL REFERENCES VodCategories(Id), PRIMARY KEY(GroupId,CategoryId), UNIQUE(CategoryId));
        CREATE TABLE VodPublicationOrder (PublicId INTEGER PRIMARY KEY, Kind TEXT NOT NULL, SortOrder INTEGER NOT NULL);
        CREATE INDEX IX_VodGroups_Kind_Order ON VodGroups(Kind,SortOrder);
        """);
    protected override void Down(MigrationBuilder m)=>m.Sql("DROP TABLE VodPublicationOrder; DROP TABLE VodGroupSources; DROP TABLE VodGroups;");
    protected override void BuildTargetModel(ModelBuilder m)=>CatalogModelSnapshot.Build(m);
}
