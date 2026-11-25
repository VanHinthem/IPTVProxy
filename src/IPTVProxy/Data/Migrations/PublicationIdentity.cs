using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
namespace IPTVProxy.Data.Migrations;
[DbContext(typeof(CatalogDb))]
[Migration("202611250003_PublicationIdentity")]
public sealed class PublicationIdentity : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        CREATE TABLE PublishedIdentities (
            Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
            GroupId INTEGER NOT NULL REFERENCES ChannelGroups(Id) ON DELETE CASCADE,
            ChannelId INTEGER NOT NULL REFERENCES LogicalChannels(Id) ON DELETE CASCADE
        );
        CREATE UNIQUE INDEX IX_PublishedIdentities_GroupId_ChannelId ON PublishedIdentities(GroupId, ChannelId);
        CREATE INDEX IX_PublishedIdentities_ChannelId ON PublishedIdentities(ChannelId);
        """);
    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.DropTable("PublishedIdentities");
    protected override void BuildTargetModel(ModelBuilder modelBuilder) => CatalogModelSnapshot.Build(modelBuilder, true, true, true, false, false, false);
}
