using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
namespace IPTVProxy.Data.Migrations;
[DbContext(typeof(CatalogDb))]
[Migration("202611250010_GroupComposition")]
public sealed class GroupComposition : Migration
{
    protected override void Up(MigrationBuilder m)
    {
        m.AddColumn<string>("ReferenceCategories","ChannelGroups",nullable:false,defaultValue:"");
        m.AddColumn<string>("PackagePriority","ChannelGroups",nullable:false,defaultValue:"");
    }
    protected override void Down(MigrationBuilder m)
    {
        m.DropColumn("ReferenceCategories","ChannelGroups");
        m.DropColumn("PackagePriority","ChannelGroups");
    }
    protected override void BuildTargetModel(ModelBuilder modelBuilder)=>CatalogModelSnapshot.Build(modelBuilder);
}
