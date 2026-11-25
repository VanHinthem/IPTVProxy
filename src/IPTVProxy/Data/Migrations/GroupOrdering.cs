using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
namespace IPTVProxy.Data.Migrations;
[DbContext(typeof(CatalogDb))]
[Migration("202611250008_GroupOrdering")]
public sealed class GroupOrdering : Migration
{
    protected override void Up(MigrationBuilder m){m.AddColumn<int>("Position","GroupOverrides",nullable:true);m.AddColumn<bool>("OrderOnly","GroupOverrides",nullable:false,defaultValue:false);}
    protected override void Down(MigrationBuilder m){m.DropColumn("Position","GroupOverrides");m.DropColumn("OrderOnly","GroupOverrides");}
    protected override void BuildTargetModel(ModelBuilder modelBuilder)=>CatalogModelSnapshot.Build(modelBuilder, true, true, true, true, true, true, true, false, false);
}
