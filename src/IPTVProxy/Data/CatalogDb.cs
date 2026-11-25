using Microsoft.EntityFrameworkCore;

namespace IPTVProxy.Data;

public sealed class CatalogDb(DbContextOptions<CatalogDb> options) : DbContext(options)
{
    public DbSet<Provider> Providers => Set<Provider>();
    public DbSet<LiveCategory> LiveCategories => Set<LiveCategory>();
    public DbSet<LiveStream> LiveStreams => Set<LiveStream>();
    public DbSet<LogicalChannel> LogicalChannels => Set<LogicalChannel>();
    public DbSet<ChannelSource> ChannelSources => Set<ChannelSource>();
    protected override void OnModelCreating(ModelBuilder model)
    {
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
}

public sealed class LiveCategory
{
    public int Id { get; set; }
    public int ProviderId { get; set; }
    public string SourceId { get; set; } = "";
    public string Name { get; set; } = "";
    public bool Active { get; set; } = true;
}

public sealed class LogicalChannel
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string MatchKey { get; set; } = "";
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
    public bool Manual { get; set; }
    public bool ReviewRequired { get; set; }
}
