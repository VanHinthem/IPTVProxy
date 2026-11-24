using Microsoft.EntityFrameworkCore;

namespace IPTVProxy.Data;

public sealed class CatalogDb(DbContextOptions<CatalogDb> options) : DbContext(options)
{
    public DbSet<Provider> Providers => Set<Provider>();
    public DbSet<LiveStream> LiveStreams => Set<LiveStream>();
    protected override void OnModelCreating(ModelBuilder model)
    {
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
    public bool Active { get; set; } = true;
}
