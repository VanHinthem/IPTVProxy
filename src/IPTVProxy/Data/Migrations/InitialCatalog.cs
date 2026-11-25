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
    protected override void BuildTargetModel(ModelBuilder modelBuilder) => CatalogModelSnapshot.Build(modelBuilder, false);
}

[DbContext(typeof(CatalogDb))]
public sealed class CatalogModelSnapshot : ModelSnapshot
{
    protected override void BuildModel(ModelBuilder modelBuilder) => Build(modelBuilder);
    internal static void Build(ModelBuilder model, bool managedChannels = true, bool groups = true, bool identities = true, bool preferences = true, bool metadata = true, bool classification = true, bool ordering = true, bool sourceOrdering = true, bool composition = true)
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
            if (managedChannels) { e.Property(x => x.EpgId).IsRequired(); e.Property(x => x.Logo).IsRequired(); }
            else { e.Ignore(x => x.EpgId); e.Ignore(x => x.Logo); }
            e.Property(x => x.Active);
            if(sourceOrdering)e.Property(x=>x.SourceOrder);else e.Ignore(x=>x.SourceOrder);
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
            if(sourceOrdering)e.Property(x=>x.SourceOrder);else e.Ignore(x=>x.SourceOrder);
            e.HasIndex(x => new { x.ProviderId, x.SourceId }).IsUnique();
            e.HasOne<Provider>().WithMany().HasForeignKey(x => x.ProviderId).OnDelete(DeleteBehavior.Cascade);
        });
        if (managedChannels)
        {
        model.Entity<LogicalChannel>(e => {
            e.ToTable("LogicalChannels");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedOnAdd();
            e.Property(x => x.Name).IsRequired();
            e.Property(x => x.MatchKey).IsRequired();
            if (preferences) e.Property(x => x.PreferredStreamId); else e.Ignore(x => x.PreferredStreamId);
            e.Property(x => x.Country).IsRequired();
            e.Property(x => x.Manual);
            e.HasIndex(x => x.MatchKey);
        });
        model.Entity<ChannelSource>(e => {
            e.ToTable("ChannelSources");
            e.HasKey(x => x.LiveStreamId);
            e.Property(x => x.LiveStreamId).ValueGeneratedNever();
            e.Property(x => x.LogicalChannelId);
            e.Property(x => x.NormalizedName).IsRequired();
            e.Property(x => x.Country).IsRequired();
            e.Property(x => x.Quality);
            e.Property(x => x.Codec).IsRequired();
            if(classification) { e.Property(x=>x.ChannelType).IsRequired(); e.Property(x=>x.QualityOrigin).IsRequired(); e.Property(x=>x.FrameRate); e.Property(x=>x.CatchupHint); }
            else {e.Ignore(x=>x.ChannelType);e.Ignore(x=>x.QualityOrigin);e.Ignore(x=>x.FrameRate);e.Ignore(x=>x.CatchupHint);}
            if (metadata) { e.Property(x => x.Package).IsRequired(); e.Property(x => x.Tags).IsRequired(); e.Property(x => x.Separator); }
            else { e.Ignore(x => x.Package); e.Ignore(x => x.Tags); e.Ignore(x => x.Separator); }
            e.Property(x => x.Manual);
            e.Property(x => x.ReviewRequired);
            e.HasIndex(x => x.LogicalChannelId);
            e.HasOne<LiveStream>().WithMany().HasForeignKey(x => x.LiveStreamId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.LogicalChannel).WithMany().HasForeignKey(x => x.LogicalChannelId).OnDelete(DeleteBehavior.Cascade);
        });

        }
        if (managedChannels && groups)
        {
            model.Entity<ChannelGroup>(e => {
                e.ToTable("ChannelGroups"); e.HasKey(x => x.Id);
                e.Property(x => x.Id).ValueGeneratedOnAdd();
                e.Property(x => x.Name).IsRequired();
                e.Property(x => x.Country).IsRequired();
                e.Property(x => x.NameContains).IsRequired();
                e.Property(x => x.CategoryContains).IsRequired();
                if(classification)e.Property(x=>x.ChannelType).IsRequired();else e.Ignore(x=>x.ChannelType);
                e.Property(x => x.Enabled); e.Property(x => x.Automatic);
                e.Property(x => x.ProviderId); e.Property(x => x.MinimumQuality);
                e.Property(x => x.UnknownFallback); e.Property(x => x.LowerFallback);
                if(sourceOrdering){e.Property(x=>x.SortProviderId);e.Property(x=>x.ProviderOrder);}else{e.Ignore(x=>x.SortProviderId);e.Ignore(x=>x.ProviderOrder);}
                e.Property(x => x.QualityFirst); e.Property(x => x.SortOrder);
                if(composition){e.Property(x=>x.ReferenceCategories).IsRequired();e.Property(x=>x.PackagePriority).IsRequired();}else{e.Ignore(x=>x.ReferenceCategories);e.Ignore(x=>x.PackagePriority);}
            });
            model.Entity<GroupOverride>(e => {
                e.ToTable("GroupOverrides"); e.HasKey(x => new { x.GroupId, x.ChannelId });
                e.Property(x => x.Include);
                if(ordering){e.Property(x=>x.Position);e.Property(x=>x.OrderOnly);}else{e.Ignore(x=>x.Position);e.Ignore(x=>x.OrderOnly);}
                e.HasIndex(x => x.ChannelId);
                e.HasOne<ChannelGroup>().WithMany().HasForeignKey(x => x.GroupId).OnDelete(DeleteBehavior.Cascade);
                e.HasOne<LogicalChannel>().WithMany().HasForeignKey(x => x.ChannelId).OnDelete(DeleteBehavior.Cascade);
            });
            model.Entity<PublicationSettings>(e => {
                e.ToTable("PublicationSettings"); e.HasKey(x => x.Id);
                e.Property(x => x.Id).ValueGeneratedOnAdd(); e.Property(x => x.Managed);
            });
        }
        if (managedChannels && groups && identities)
        {
            model.Entity<PublishedIdentity>(e => {
                e.ToTable("PublishedIdentities"); e.HasKey(x => x.Id);
                e.Property(x => x.Id).ValueGeneratedOnAdd();
                e.HasIndex(x => new { x.GroupId, x.ChannelId }).IsUnique();
                e.HasIndex(x => x.ChannelId);
                e.HasOne<ChannelGroup>().WithMany().HasForeignKey(x => x.GroupId).OnDelete(DeleteBehavior.Cascade);
                e.HasOne<LogicalChannel>().WithMany().HasForeignKey(x => x.ChannelId).OnDelete(DeleteBehavior.Cascade);
            });
        }
    }
}
