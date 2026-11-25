using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
namespace IPTVProxy.Data.Migrations;
[DbContext(typeof(CatalogDb))]
[Migration("202611250006_SourceClassification")]
public sealed class SourceClassification : Migration
{
    protected override void Up(MigrationBuilder m)
    {
        m.AddColumn<string>("ChannelType","ChannelSources",nullable:false,defaultValue:"Unknown");
        m.AddColumn<string>("QualityOrigin","ChannelSources",nullable:false,defaultValue:"");
        m.AddColumn<int>("FrameRate","ChannelSources",nullable:false,defaultValue:0);
        m.AddColumn<bool>("CatchupHint","ChannelSources",nullable:false,defaultValue:false);
        m.AddColumn<string>("ChannelType","ChannelGroups",nullable:false,defaultValue:"");
    }
    protected override void Down(MigrationBuilder m)
    {
        foreach(var name in new[]{"ChannelType","QualityOrigin","FrameRate","CatchupHint"})m.DropColumn(name,"ChannelSources");
        m.DropColumn("ChannelType","ChannelGroups");
    }
    protected override void BuildTargetModel(ModelBuilder modelBuilder)=>CatalogModelSnapshot.Build(modelBuilder, true, true, true, true, true, true, false);
}
