using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
namespace IPTVProxy.Data.Migrations;
[DbContext(typeof(CatalogDb))]
[Migration("202611250011_Artwork")]
public sealed class Artwork : Migration
{
    protected override void Up(MigrationBuilder m)
    {
        m.Sql("CREATE TABLE ChannelArtwork (ChannelId INTEGER PRIMARY KEY REFERENCES LogicalChannels(Id) ON DELETE CASCADE, Url TEXT NOT NULL);");
        m.Sql("ALTER TABLE EpgPrograms ADD COLUMN Artwork TEXT NOT NULL DEFAULT '';");
    }
    protected override void Down(MigrationBuilder m)
    {
        m.Sql("DROP TABLE ChannelArtwork;");
        m.Sql("ALTER TABLE EpgPrograms DROP COLUMN Artwork;");
    }
    protected override void BuildTargetModel(ModelBuilder modelBuilder)=>CatalogModelSnapshot.Build(modelBuilder);
}
