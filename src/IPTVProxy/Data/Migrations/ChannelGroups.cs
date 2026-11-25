using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace IPTVProxy.Data.Migrations;
[DbContext(typeof(CatalogDb))]
[Migration("202611250002_ChannelGroups")]
public sealed class ChannelGroups : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            CREATE TABLE ChannelGroups (
                Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                Name TEXT NOT NULL, Enabled INTEGER NOT NULL, Automatic INTEGER NOT NULL,
                Country TEXT NOT NULL, NameContains TEXT NOT NULL, CategoryContains TEXT NOT NULL,
                ProviderId INTEGER NOT NULL, MinimumQuality INTEGER NOT NULL,
                UnknownFallback INTEGER NOT NULL, LowerFallback INTEGER NOT NULL,
                QualityFirst INTEGER NOT NULL, SortOrder INTEGER NOT NULL
            );
            CREATE TABLE GroupOverrides (
                GroupId INTEGER NOT NULL REFERENCES ChannelGroups(Id) ON DELETE CASCADE,
                ChannelId INTEGER NOT NULL REFERENCES LogicalChannels(Id) ON DELETE CASCADE,
                Include INTEGER NOT NULL, PRIMARY KEY (GroupId, ChannelId)
            );
            CREATE INDEX IX_GroupOverrides_ChannelId ON GroupOverrides(ChannelId);
            CREATE TABLE PublicationSettings (Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT, Managed INTEGER NOT NULL);
            INSERT INTO PublicationSettings (Id, Managed) VALUES (1, 0);
            """);
    }
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable("GroupOverrides");
        migrationBuilder.DropTable("ChannelGroups");
        migrationBuilder.DropTable("PublicationSettings");
    }
    protected override void BuildTargetModel(ModelBuilder modelBuilder) => CatalogModelSnapshot.Build(modelBuilder, true, true, false, false);
}
