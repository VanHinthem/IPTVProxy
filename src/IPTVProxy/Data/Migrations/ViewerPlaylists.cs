using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
namespace IPTVProxy.Data.Migrations;

[DbContext(typeof(CatalogDb))]
[Migration("202611260004_ViewerPlaylists")]
public sealed class ViewerPlaylists : Migration
{
    protected override void Up(MigrationBuilder m)=>m.Sql("""
        CREATE TABLE ViewerPlaylists(
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            Name TEXT NOT NULL COLLATE NOCASE UNIQUE,
            Enabled INTEGER NOT NULL DEFAULT 1 CHECK(Enabled IN (0,1)),
            AllPublished INTEGER NOT NULL DEFAULT 0 CHECK(AllPublished IN (0,1))
        );
        CREATE TABLE ViewerPlaylistEntries(
            PlaylistId INTEGER NOT NULL REFERENCES ViewerPlaylists(Id) ON DELETE CASCADE,
            Kind TEXT NOT NULL CHECK(Kind IN ('live','raw','movie','series')),
            CategoryId INTEGER NOT NULL CHECK(CategoryId>0),
            SortOrder INTEGER NOT NULL DEFAULT 0,
            PRIMARY KEY(PlaylistId,Kind,CategoryId)
        );
        CREATE TABLE ViewerAccounts(
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            Username TEXT NOT NULL UNIQUE,
            PasswordHash TEXT NOT NULL,
            Enabled INTEGER NOT NULL DEFAULT 1 CHECK(Enabled IN (0,1)),
            MaxStreams INTEGER NOT NULL DEFAULT 1 CHECK(MaxStreams BETWEEN 1 AND 64),
            AllowRemote INTEGER NOT NULL DEFAULT 0 CHECK(AllowRemote IN (0,1))
        );
        CREATE TABLE ViewerPlaylistAssignments(
            AccountId INTEGER NOT NULL REFERENCES ViewerAccounts(Id) ON DELETE CASCADE,
            PlaylistId INTEGER NOT NULL REFERENCES ViewerPlaylists(Id) ON DELETE RESTRICT,
            IsDefault INTEGER NOT NULL DEFAULT 0 CHECK(IsDefault IN (0,1)),
            PRIMARY KEY(AccountId,PlaylistId)
        );
        CREATE UNIQUE INDEX IX_ViewerPlaylistAssignments_Default ON ViewerPlaylistAssignments(AccountId) WHERE IsDefault=1;
        INSERT INTO ViewerPlaylists(Name,Enabled,AllPublished) VALUES('Standaard',1,1);
        """);
    protected override void Down(MigrationBuilder m)=>m.Sql("""
        DROP TABLE ViewerPlaylistAssignments;
        DROP TABLE ViewerAccounts;
        DROP TABLE ViewerPlaylistEntries;
        DROP TABLE ViewerPlaylists;
        """);
    protected override void BuildTargetModel(ModelBuilder m)=>CatalogModelSnapshot.Build(m);
}
