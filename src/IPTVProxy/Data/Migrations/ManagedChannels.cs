using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace IPTVProxy.Data.Migrations;

[DbContext(typeof(CatalogDb))]
[Migration("202611250001_ManagedChannels")]
public sealed class ManagedChannels : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>("EpgId", "LiveStreams", nullable: false, defaultValue: "");
        migrationBuilder.AddColumn<string>("Logo", "LiveStreams", nullable: false, defaultValue: "");
        migrationBuilder.Sql("""
            CREATE TABLE LogicalChannels (
                Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                Name TEXT NOT NULL, MatchKey TEXT NOT NULL, Country TEXT NOT NULL, Manual INTEGER NOT NULL
            );
            CREATE INDEX IX_LogicalChannels_MatchKey ON LogicalChannels(MatchKey);
            CREATE TABLE ChannelSources (
                LiveStreamId INTEGER NOT NULL PRIMARY KEY REFERENCES LiveStreams(Id) ON DELETE CASCADE,
                LogicalChannelId INTEGER NOT NULL REFERENCES LogicalChannels(Id) ON DELETE CASCADE,
                NormalizedName TEXT NOT NULL, Country TEXT NOT NULL, Quality INTEGER NOT NULL,
                Codec TEXT NOT NULL, Manual INTEGER NOT NULL, ReviewRequired INTEGER NOT NULL
            );
            CREATE INDEX IX_ChannelSources_LogicalChannelId ON ChannelSources(LogicalChannelId);
            """);
    }
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable("ChannelSources");
        migrationBuilder.DropTable("LogicalChannels");
        migrationBuilder.DropColumn("EpgId", "LiveStreams");
        migrationBuilder.DropColumn("Logo", "LiveStreams");
    }
    protected override void BuildTargetModel(ModelBuilder modelBuilder) => CatalogModelSnapshot.Build(modelBuilder, true, false, false, false, false);
}
