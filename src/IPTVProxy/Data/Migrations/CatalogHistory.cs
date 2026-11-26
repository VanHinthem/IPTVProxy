using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
namespace IPTVProxy.Data.Migrations;
[DbContext(typeof(CatalogDb))]
[Migration("202611260003_CatalogHistory")]
public sealed class CatalogHistory:Migration
{
    protected override void Up(MigrationBuilder m)=>m.Sql("""
        CREATE TABLE CatalogSchedule(ProviderId INTEGER NOT NULL REFERENCES Providers(Id), Kind TEXT NOT NULL, Hours INTEGER NOT NULL CHECK(Hours BETWEEN 0 AND 168), PRIMARY KEY(ProviderId,Kind));
        CREATE TABLE CatalogImportRuns(Id INTEGER PRIMARY KEY AUTOINCREMENT,ProviderId INTEGER NOT NULL REFERENCES Providers(Id),Kind TEXT NOT NULL,Started INTEGER NOT NULL,Finished INTEGER NOT NULL,Success INTEGER NOT NULL,ItemCount INTEGER NOT NULL,CategoryCount INTEGER NOT NULL,Added INTEGER NOT NULL DEFAULT 0,Removed INTEGER NOT NULL DEFAULT 0,Error TEXT NOT NULL DEFAULT '');
        CREATE INDEX IX_CatalogImportRuns_Provider_Kind ON CatalogImportRuns(ProviderId,Kind,Id DESC);
        CREATE TABLE CatalogChanges(Id INTEGER PRIMARY KEY AUTOINCREMENT,RunId INTEGER NOT NULL REFERENCES CatalogImportRuns(Id) ON DELETE CASCADE,Entity TEXT NOT NULL,SourceId INTEGER NOT NULL,Name TEXT NOT NULL,Category TEXT NOT NULL,Change TEXT NOT NULL,Observed INTEGER NOT NULL);
        CREATE INDEX IX_CatalogChanges_Run ON CatalogChanges(RunId);
        CREATE INDEX IX_CatalogChanges_Observed ON CatalogChanges(Observed DESC,Id DESC);
        """);
    protected override void Down(MigrationBuilder m)=>m.Sql("DROP TABLE CatalogChanges; DROP TABLE CatalogImportRuns; DROP TABLE CatalogSchedule;");
    protected override void BuildTargetModel(ModelBuilder m)=>CatalogModelSnapshot.Build(m);
}
