using IPTVProxy.Data;
using Microsoft.EntityFrameworkCore;

namespace IPTVProxy.Providers;

public static class ManagedPublication
{
    // Managed and raw catalogs are mutually exclusive. Published IDs are durable,
    // positive database identities, independent of source and group/channel numbering.
    public const int PublicOffset = 1_000_000_000;
    private static readonly SemaphoreSlim IdentityGate = new(1, 1);
    public sealed record Candidate(int ChannelId, int StreamId, int ProviderId, int Priority, int Limit, int Quality,
        string Country, string Category, string Name, string Logo, string EpgId, string ChannelType = "Unknown", string Package = "", int CategoryOrder = int.MaxValue, int SourceOrder = int.MaxValue, int CategoryId = 0);
    public sealed record Published(int Id, int GroupId, int ChannelId, string Name, Candidate Source);
    public static Task<bool> IsEnabled(CatalogDb db, CancellationToken ct) =>
        db.PublicationSettings.AsNoTracking().AnyAsync(x => x.Id == 1 && x.Managed, ct);
    public static IQueryable<Candidate> Candidates(CatalogDb db, int? channelId = null) =>
        from link in db.ChannelSources.AsNoTracking()
        join s in db.LiveStreams.AsNoTracking() on link.LiveStreamId equals s.Id
        join p in db.Providers.AsNoTracking() on s.ProviderId equals p.Id
        join c in db.LiveCategories.AsNoTracking() on new { s.ProviderId, SourceId = s.CategoryId } equals new { c.ProviderId, c.SourceId }
        where (channelId == null || link.LogicalChannelId == channelId) && s.Active && p.Enabled && c.Active && !link.Separator && (!link.ReviewRequired || !db.ChannelSources.Any(other => other.LogicalChannelId == link.LogicalChannelId && other.LiveStreamId != link.LiveStreamId))
        select new Candidate(link.LogicalChannelId, s.Id, p.Id, p.Priority, p.ConnectionLimit, link.Quality, link.Country, c.Name, s.Name, s.Logo, s.EpgId, link.ChannelType, link.Package, c.SourceOrder, s.SourceOrder, c.Id);

    public static Candidate[] Rank(ChannelGroup group, IEnumerable<Candidate> candidates, int preferredStreamId = 0)
    {
        var packagePriority=group.PackagePriority.Split('\n',StringSplitOptions.TrimEntries|StringSplitOptions.RemoveEmptyEntries);
        int PackageRank(Candidate c){var i=Array.FindIndex(packagePriority,p=>p.Equals(c.Package,StringComparison.OrdinalIgnoreCase));return i<0?int.MaxValue:i;}
        var eligible = candidates.Where(c => (group.ProviderId == 0 || c.ProviderId == group.ProviderId) && (group.ChannelType == "" || c.ChannelType == group.ChannelType)).ToArray();
        var known = eligible.Where(c => c.Quality > 0 && c.Quality >= group.MinimumQuality).ToArray();
        var unknown = eligible.Where(c => c.Quality == 0).ToArray();
        var lower = eligible.Where(c => c.Quality > 0 && c.Quality < group.MinimumQuality).ToArray();
        IEnumerable<Candidate> Sort(IEnumerable<Candidate> values) => group.QualityFirst
            ? values.OrderByDescending(c => c.StreamId == preferredStreamId).ThenByDescending(c => c.Quality).ThenBy(c => c.Priority).ThenBy(c => c.ProviderId).ThenBy(PackageRank).ThenBy(c => c.StreamId)
            : values.OrderByDescending(c => c.StreamId == preferredStreamId).ThenBy(c => c.Priority).ThenBy(c => c.ProviderId).ThenBy(PackageRank).ThenByDescending(c => c.Quality).ThenBy(c => c.StreamId);
        // Unknown is only a fallback; not equivalent to satisfying a quality constraint.
        return Sort(known).Concat(group.MinimumQuality == 0 || group.UnknownFallback ? Sort(unknown) : [])
            .Concat(group.LowerFallback ? Sort(lower) : []).ToArray();
    }
    private static bool Contains(string value, string filter) => string.IsNullOrWhiteSpace(filter) || value.Contains(filter.Trim(), StringComparison.OrdinalIgnoreCase);
    public static int[] ReferenceIds(ChannelGroup g)=>g.ReferenceCategories.Split(',',StringSplitOptions.TrimEntries|StringSplitOptions.RemoveEmptyEntries).Select(x=>int.TryParse(x,out var id)?id:0).Where(x=>x>0).Distinct().ToArray();
    public static Candidate[] Eligible(ChannelGroup group, LogicalChannel channel, IEnumerable<Candidate> candidates, bool? manual)
    {
        if (manual == false || (manual is null && !group.Automatic)) return [];
        if (manual is null && !Contains(channel.Name, group.NameContains)) return [];
        var all=candidates.ToArray();
        var refs=ReferenceIds(group);
        // Reference categories determine membership, not playback source eligibility.
        if(manual is null && refs.Length>0 && !all.Any(c=>refs.Contains(c.CategoryId) && (string.IsNullOrWhiteSpace(group.Country)||group.Country.Split(',',StringSplitOptions.TrimEntries|StringSplitOptions.RemoveEmptyEntries).Contains(c.Country,StringComparer.OrdinalIgnoreCase))))return [];
        var sources = all.Where(c => manual == true ||
            ((string.IsNullOrWhiteSpace(group.Country) || group.Country.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Contains(c.Country, StringComparer.OrdinalIgnoreCase))
             && (refs.Length>0 || Contains(c.Category, group.CategoryContains))));
        return Rank(group, sources, channel.PreferredStreamId);
    }
    public static (int Provider,int Category,int Stream) SortKey(ChannelGroup group,IEnumerable<Candidate> sources)
    {
        if(!group.ProviderOrder)return (int.MaxValue,int.MaxValue,int.MaxValue);
        var refs=ReferenceIds(group);
        if(refs.Length>0)return sources.Where(s=>refs.Contains(s.CategoryId)).Select(s=>(0,Array.IndexOf(refs,s.CategoryId),s.SourceOrder)).Order().DefaultIfEmpty((int.MaxValue,int.MaxValue,int.MaxValue)).First();
        return sources.Where(s=>group.SortProviderId==0||s.ProviderId==group.SortProviderId)
            .Where(s=>s.SourceOrder!=int.MaxValue)
            .Select(s=>(s.Priority,s.CategoryOrder,s.SourceOrder)).Order().DefaultIfEmpty((int.MaxValue,int.MaxValue,int.MaxValue)).First();
    }
    public static async Task<List<Published>> ListAsync(CatalogDb db, int? groupId, CancellationToken ct, bool allocateIdentities = true)
    {
        var groups = await db.ChannelGroups.AsNoTracking().Where(g => g.Enabled && (groupId == null || g.Id == groupId)).OrderBy(g => g.SortOrder).ThenBy(g => g.Id).ToListAsync(ct);
        if (groups.Count == 0) return [];
        var channels = await db.LogicalChannels.AsNoTracking().Where(c=>db.ChannelSources.Any(l=>l.LogicalChannelId==c.Id && db.LiveStreams.Any(s=>s.Id==l.LiveStreamId && s.Active))).OrderBy(c => c.Name).ThenBy(c => c.Id).ToListAsync(ct);
        var sources = (await Candidates(db).ToListAsync(ct)).ToLookup(c => c.ChannelId);
        var overrides = (await db.GroupOverrides.AsNoTracking().ToListAsync(ct)).ToDictionary(x => (x.GroupId,x.ChannelId), x => x);
        var result = new List<Published>();
        foreach (var group in groups)
            foreach (var channel in channels.OrderBy(c => overrides.GetValueOrDefault((group.Id,c.Id))?.Position ?? int.MaxValue).ThenBy(c=>SortKey(group,sources[c.Id])).ThenBy(c=>c.Name).ThenBy(c=>c.Id))
            {
                bool? manual = overrides.TryGetValue((group.Id,channel.Id), out var include) && !include.OrderOnly ? include.Include : null;
                var ranked = Eligible(group, channel, sources[channel.Id], manual);
                if (ranked.Length > 0) result.Add(new(0, group.Id, channel.Id, channel.Name, ranked[0]));
            }
        if (!allocateIdentities) return result;
        await IdentityGate.WaitAsync(ct);
        try
        {
            var identities = (await db.PublishedIdentities.AsNoTracking().ToListAsync(ct))
                .ToDictionary(x => (x.GroupId, x.ChannelId));
            foreach (var row in result)
                if (!identities.ContainsKey((row.GroupId, row.ChannelId)))
                {
                    var identity = new PublishedIdentity { GroupId = row.GroupId, ChannelId = row.ChannelId };
                    db.PublishedIdentities.Add(identity);
                    identities.Add((row.GroupId, row.ChannelId), identity);
                }
            await db.SaveChangesAsync(ct);
            return result.Select(x => x with { Id = checked(PublicOffset + identities[(x.GroupId, x.ChannelId)].Id) }).ToList();
        }
        finally { IdentityGate.Release(); }
    }
    public static async Task<Candidate[]> ResolveAsync(CatalogDb db, int publicId, CancellationToken ct)
    {
        if (publicId <= PublicOffset || !await IsEnabled(db, ct)) return [];
        var identity = await db.PublishedIdentities.AsNoTracking().SingleOrDefaultAsync(x => x.Id == publicId - PublicOffset, ct);
        if (identity is null) return [];
        var groupId = identity.GroupId;
        var channelId = identity.ChannelId;
        var group = await db.ChannelGroups.AsNoTracking().SingleOrDefaultAsync(x => x.Id == groupId && x.Enabled, ct);
        var channel = await db.LogicalChannels.AsNoTracking().SingleOrDefaultAsync(x => x.Id == channelId, ct);
        if (group is null || channel is null) return [];
        var manual = await db.GroupOverrides.AsNoTracking().Where(x => x.GroupId == groupId && x.ChannelId == channelId).Select(x => x.OrderOnly ? (bool?)null : x.Include).SingleOrDefaultAsync(ct);
        return Eligible(group, channel, await Candidates(db, channelId).ToListAsync(ct), manual);
    }
}
