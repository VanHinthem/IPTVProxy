using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
namespace IPTVProxy.Data.Migrations;
[DbContext(typeof(CatalogDb))]
[Migration("202611250009_ProviderOrder")]
public sealed class ProviderOrder : Migration
{
    protected override void Up(MigrationBuilder m)
    {
        m.AddColumn<int>("SourceOrder","LiveStreams",nullable:false,defaultValue:int.MaxValue);
        m.AddColumn<int>("SourceOrder","LiveCategories",nullable:false,defaultValue:int.MaxValue);
        m.AddColumn<int>("SortProviderId","ChannelGroups",nullable:false,defaultValue:0);
        m.AddColumn<bool>("ProviderOrder","ChannelGroups",nullable:false,defaultValue:true);
        m.Sql("CREATE TABLE EpgPrograms (ProviderId INTEGER NOT NULL, EpgId TEXT NOT NULL, Start TEXT NOT NULL, Stop TEXT NOT NULL, Title TEXT NOT NULL, Description TEXT NOT NULL, PRIMARY KEY(ProviderId,EpgId,Start));");
        m.Sql("CREATE TABLE EpgRefreshState (ProviderId INTEGER PRIMARY KEY, Updated TEXT NOT NULL, ProgramCount INTEGER NOT NULL);");
    }
    protected override void Down(MigrationBuilder m)
    {
        m.DropColumn("SourceOrder","LiveStreams");m.DropColumn("SourceOrder","LiveCategories");
        m.DropColumn("SortProviderId","ChannelGroups");m.DropColumn("ProviderOrder","ChannelGroups");
        m.Sql("DROP TABLE EpgPrograms; DROP TABLE EpgRefreshState;");
    }
    protected override void BuildTargetModel(ModelBuilder modelBuilder)=>CatalogModelSnapshot.Build(modelBuilder, true, true, true, true, true, true, true, true, false);
}
