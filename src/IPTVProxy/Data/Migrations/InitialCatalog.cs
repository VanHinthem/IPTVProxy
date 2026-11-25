using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace IPTVProxy.Data.Migrations;

[DbContext(typeof(CatalogDb))]
[Migration("202611240001_InitialCatalog")]
public sealed class InitialCatalog : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            CREATE TABLE Providers (
                Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                Name TEXT NOT NULL, BaseUrl TEXT NOT NULL, ProtectedCredentials TEXT NOT NULL,
                Priority INTEGER NOT NULL, ConnectionLimit INTEGER NOT NULL, Enabled INTEGER NOT NULL
            );
            """);
        migrationBuilder.Sql("""
            CREATE TABLE LiveStreams (
                Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                ProviderId INTEGER NOT NULL REFERENCES Providers(Id) ON DELETE CASCADE,
                SourceId INTEGER NOT NULL, Name TEXT NOT NULL, CategoryId TEXT NOT NULL, Active INTEGER NOT NULL
            );
            CREATE UNIQUE INDEX IX_LiveStreams_ProviderId_SourceId ON LiveStreams(ProviderId, SourceId);
            """);
        migrationBuilder.Sql("""
            CREATE TABLE LiveCategories (
                Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                ProviderId INTEGER NOT NULL REFERENCES Providers(Id) ON DELETE CASCADE,
                SourceId TEXT NOT NULL, Name TEXT NOT NULL, Active INTEGER NOT NULL
            );
            CREATE UNIQUE INDEX IX_LiveCategories_ProviderId_SourceId ON LiveCategories(ProviderId, SourceId);
            """);
    }
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable("LiveCategories");
        migrationBuilder.DropTable("LiveStreams");
        migrationBuilder.DropTable("Providers");
    }
    protected override void BuildTargetModel(ModelBuilder modelBuilder) => CatalogModelSnapshot.Build(modelBuilder);
}

[DbContext(typeof(CatalogDb))]
public sealed class CatalogModelSnapshot : ModelSnapshot
{
    protected override void BuildModel(ModelBuilder modelBuilder) => Build(modelBuilder);
    internal static void Build(ModelBuilder model)
    {
        model.HasAnnotation("ProductVersion", "10.0.0");
        model.Entity<Provider>(e => {
            e.ToTable("Providers");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedOnAdd();
            e.Property(x => x.Name).IsRequired();
            e.Property(x => x.BaseUrl).IsRequired();
            e.Property(x => x.ProtectedCredentials).IsRequired();
            e.Property(x => x.Priority);
            e.Property(x => x.ConnectionLimit);
            e.Property(x => x.Enabled);
        });
        model.Entity<LiveStream>(e => {
            e.ToTable("LiveStreams");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedOnAdd();
            e.Property(x => x.ProviderId);
            e.Property(x => x.SourceId);
            e.Property(x => x.Name).IsRequired();
            e.Property(x => x.CategoryId).IsRequired();
            e.Property(x => x.Active);
            e.HasIndex(x => new { x.ProviderId, x.SourceId }).IsUnique();
            e.HasOne<Provider>().WithMany().HasForeignKey(x => x.ProviderId).OnDelete(DeleteBehavior.Cascade);
        });
        model.Entity<LiveCategory>(e => {
            e.ToTable("LiveCategories");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedOnAdd();
            e.Property(x => x.ProviderId);
            e.Property(x => x.SourceId).IsRequired();
            e.Property(x => x.Name).IsRequired();
            e.Property(x => x.Active);
            e.HasIndex(x => new { x.ProviderId, x.SourceId }).IsUnique();
            e.HasOne<Provider>().WithMany().HasForeignKey(x => x.ProviderId).OnDelete(DeleteBehavior.Cascade);
        });
    }
}
