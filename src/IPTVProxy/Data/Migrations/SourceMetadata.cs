using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
namespace IPTVProxy.Data.Migrations;
[DbContext(typeof(CatalogDb))]
[Migration("202611250005_SourceMetadata")]
public sealed class SourceMetadata : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>("Package","ChannelSources",nullable:false,defaultValue:"");
        migrationBuilder.AddColumn<string>("Tags","ChannelSources",nullable:false,defaultValue:"");
        migrationBuilder.AddColumn<bool>("Separator","ChannelSources",nullable:false,defaultValue:false);
    }
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn("Package","ChannelSources");
        migrationBuilder.DropColumn("Tags","ChannelSources");
        migrationBuilder.DropColumn("Separator","ChannelSources");
    }
    protected override void BuildTargetModel(ModelBuilder modelBuilder)=>CatalogModelSnapshot.Build(modelBuilder, true, true, true, true, true, false, false, false, false);
}
