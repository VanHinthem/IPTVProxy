using Microsoft.EntityFrameworkCore;

namespace IPTVProxy.Data;

public sealed class CatalogDb(DbContextOptions<CatalogDb> options) : DbContext(options)
{
    public DbSet<Provider> Providers => Set<Provider>();
    public DbSet<LiveCategory> LiveCategories => Set<LiveCategory>();
    public DbSet<LiveStream> LiveStreams => Set<LiveStream>();
    public DbSet<LogicalChannel> LogicalChannels => Set<LogicalChannel>();
    public DbSet<ChannelSource> ChannelSources => Set<ChannelSource>();
    public DbSet<ChannelGroup> ChannelGroups => Set<ChannelGroup>();
    public DbSet<GroupOverride> GroupOverrides => Set<GroupOverride>();
    public DbSet<PublishedIdentity> PublishedIdentities => Set<PublishedIdentity>();
    public DbSet<PublicationSettings> PublicationSettings => Set<PublicationSettings>();
    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<PublishedIdentity>().HasIndex(x => new { x.GroupId, x.ChannelId }).IsUnique();
        model.Entity<PublishedIdentity>().HasOne<ChannelGroup>().WithMany().HasForeignKey(x => x.GroupId);
        model.Entity<PublishedIdentity>().HasOne<LogicalChannel>().WithMany().HasForeignKey(x => x.ChannelId);
        model.Entity<GroupOverride>().HasKey(x => new { x.GroupId, x.ChannelId });
        model.Entity<GroupOverride>().HasOne<ChannelGroup>().WithMany().HasForeignKey(x => x.GroupId);
        model.Entity<GroupOverride>().HasOne<LogicalChannel>().WithMany().HasForeignKey(x => x.ChannelId);
        model.Entity<LogicalChannel>().HasIndex(x => x.MatchKey);
        model.Entity<ChannelSource>().HasKey(x => x.LiveStreamId);
        model.Entity<ChannelSource>().HasIndex(x => x.LogicalChannelId);
        model.Entity<ChannelSource>().HasOne<LiveStream>().WithMany().HasForeignKey(x => x.LiveStreamId);
        model.Entity<ChannelSource>().HasOne(x => x.LogicalChannel).WithMany().HasForeignKey(x => x.LogicalChannelId);
        model.Entity<LiveCategory>().HasIndex(x => new { x.ProviderId, x.SourceId }).IsUnique();
        model.Entity<LiveCategory>().HasOne<Provider>().WithMany().HasForeignKey(x => x.ProviderId);
        model.Entity<LiveStream>().HasIndex(x => new { x.ProviderId, x.SourceId }).IsUnique();
        model.Entity<LiveStream>().HasOne<Provider>().WithMany().HasForeignKey(x => x.ProviderId);
    }
}
public sealed class Provider
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string BaseUrl { get; set; } = "";
    public string ProtectedCredentials { get; set; } = "";
    public int Priority { get; set; }
    public int ConnectionLimit { get; set; } = 1;
    public bool Enabled { get; set; } = true;
}
public sealed class LiveStream
{
    public int Id { get; set; }
    public int ProviderId { get; set; }
    public long SourceId { get; set; }
    public string Name { get; set; } = "";
    public string CategoryId { get; set; } = "";
    public string EpgId { get; set; } = "";
    public string Logo { get; set; } = "";
    public bool Active { get; set; } = true;
    public int SourceOrder { get; set; } = int.MaxValue;
}

public sealed class LiveCategory
{
    public int Id { get; set; }
    public int ProviderId { get; set; }
    public string SourceId { get; set; } = "";
    public string Name { get; set; } = "";
    public bool Active { get; set; } = true;
    public int SourceOrder { get; set; } = int.MaxValue;
}

public sealed class LogicalChannel
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string MatchKey { get; set; } = "";
    public int PreferredStreamId { get; set; }
    public string Country { get; set; } = "";
    public bool Manual { get; set; }
}

public sealed class ChannelSource
{
    public int LiveStreamId { get; set; }
    public int LogicalChannelId { get; set; }
    public LogicalChannel? LogicalChannel { get; set; }
    public string NormalizedName { get; set; } = "";
    public string Country { get; set; } = "";
    public int Quality { get; set; }
    public string Codec { get; set; } = "";
    public string Package { get; set; } = "";
    public string Tags { get; set; } = "";
    public bool Separator { get; set; }
    public string ChannelType { get; set; } = "Unknown";
    public string QualityOrigin { get; set; } = "";
    public int FrameRate { get; set; }
    public bool CatchupHint { get; set; }
    public bool Manual { get; set; }
    public bool ReviewRequired { get; set; }
}

public sealed class ChannelGroup
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public bool Automatic { get; set; } = true;
    public string Country { get; set; } = "";
    public string NameContains { get; set; } = "";
    public string CategoryContains { get; set; } = "";
    public string ChannelType { get; set; } = "";
    public int ProviderId { get; set; }
    public int MinimumQuality { get; set; }
    public bool UnknownFallback { get; set; } = true;
    public bool LowerFallback { get; set; }
    public bool QualityFirst { get; set; }
    public int SortProviderId { get; set; }
    public string ReferenceCategories { get; set; } = "";
    public string PackagePriority { get; set; } = "";
    public bool ProviderOrder { get; set; } = true;
    public int SortOrder { get; set; }
}
public sealed class GroupOverride
{
    public int GroupId { get; set; }
    public int ChannelId { get; set; }
    public bool Include { get; set; }
    public int? Position { get; set; }
    public bool OrderOnly { get; set; }
}
public sealed class PublicationSettings
{
    public int Id { get; set; }
    public bool Managed { get; set; }
}

public sealed class PublishedIdentity
{
    public int Id { get; set; }
    public int GroupId { get; set; }
    public int ChannelId { get; set; }
}
