using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
namespace IPTVProxy.Data.Migrations;
[DbContext(typeof(CatalogDb))]
[Migration("202611250004_ChannelPreference")]
public sealed class ChannelPreference : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.AddColumn<int>("PreferredStreamId","LogicalChannels",nullable:false,defaultValue:0);
    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.DropColumn("PreferredStreamId","LogicalChannels");
    protected override void BuildTargetModel(ModelBuilder modelBuilder) => CatalogModelSnapshot.Build(modelBuilder, true, true, true, true, false, false, false);
}
