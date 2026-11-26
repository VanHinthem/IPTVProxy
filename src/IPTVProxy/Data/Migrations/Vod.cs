using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
namespace IPTVProxy.Data.Migrations;
[DbContext(typeof(CatalogDb))]
[Migration("202611260001_Vod")]
public sealed class Vod : Migration
{
    protected override void Up(MigrationBuilder m) => m.Sql("""
        CREATE TABLE VodCategories (Id INTEGER PRIMARY KEY AUTOINCREMENT, ProviderId INTEGER NOT NULL REFERENCES Providers(Id), Kind TEXT NOT NULL, SourceId TEXT NOT NULL, Name TEXT NOT NULL, Active INTEGER NOT NULL DEFAULT 1, Selected INTEGER NOT NULL DEFAULT 0, SortOrder INTEGER NOT NULL DEFAULT 0, UNIQUE(ProviderId,Kind,SourceId));
        CREATE TABLE VodItems (Id INTEGER PRIMARY KEY AUTOINCREMENT, ProviderId INTEGER NOT NULL REFERENCES Providers(Id), Kind TEXT NOT NULL, SourceId INTEGER NOT NULL, CategoryId INTEGER NOT NULL REFERENCES VodCategories(Id), Name TEXT NOT NULL, Extension TEXT NOT NULL, Metadata TEXT NOT NULL, Active INTEGER NOT NULL DEFAULT 1, SortOrder INTEGER NOT NULL DEFAULT 0, UNIQUE(ProviderId,Kind,SourceId));
        CREATE INDEX IX_VodItems_Category_Active ON VodItems(CategoryId,Active);
        CREATE TABLE VodDetails (ItemId INTEGER PRIMARY KEY REFERENCES VodItems(Id), Metadata TEXT NOT NULL, Fetched INTEGER NOT NULL);
        CREATE TABLE VodEpisodes (Id INTEGER PRIMARY KEY AUTOINCREMENT, ItemId INTEGER NOT NULL REFERENCES VodItems(Id), SourceId INTEGER NOT NULL, Extension TEXT NOT NULL, Active INTEGER NOT NULL DEFAULT 1, UNIQUE(ItemId,SourceId));
        """);
    protected override void Down(MigrationBuilder m) => m.Sql("DROP TABLE VodEpisodes; DROP TABLE VodDetails; DROP TABLE VodItems; DROP TABLE VodCategories;");
    protected override void BuildTargetModel(ModelBuilder m)=>CatalogModelSnapshot.Build(m);
}
