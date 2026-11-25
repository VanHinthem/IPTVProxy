using IPTVProxy.Data;
using Microsoft.EntityFrameworkCore;

namespace IPTVProxy.Providers;

public static class ManagedPublication
{
    // Separate namespace from positive legacy provider IDs. Group-specific publication
    // rules must remain distinguishable even if a channel belongs to multiple groups.
    // Stable pair encoding, bounded by the int Xtream route; fail closed on overflow.
    public const int GroupStride = 100000;
    public static int PublicId(int groupId, int channelId)
    {
        if (groupId <= 0 || channelId <= 0 || channelId >= GroupStride) throw new InvalidOperationException("Publication ID capacity exceeded.");
        return checked(-(groupId * GroupStride + channelId));
    }
    public sealed record Candidate(int ChannelId, int StreamId, int ProviderId, int Priority, int Limit, int Quality,
        string Country, string Category, string Name, string Logo, string EpgId);
    public sealed record Published(int Id, int GroupId, int ChannelId, string Name, Candidate Source);
    public static Task<bool> IsEnabled(CatalogDb db, CancellationToken ct) =>
        db.PublicationSettings.AsNoTracking().AnyAsync(x => x.Id == 1 && x.Managed, ct);
    private static IQueryable<Candidate> Candidates(CatalogDb db) =>
        from link in db.ChannelSources.AsNoTracking()
        join s in db.LiveStreams.AsNoTracking() on link.LiveStreamId equals s.Id
        join p in db.Providers.AsNoTracking() on s.ProviderId equals p.Id
        join c in db.LiveCategories.AsNoTracking() on new { s.ProviderId, SourceId = s.CategoryId } equals new { c.ProviderId, c.SourceId }
        where s.Active && p.Enabled && c.Active
        select new Candidate(link.LogicalChannelId, s.Id, p.Id, p.Priority, p.ConnectionLimit, link.Quality, link.Country, c.Name, s.Name, s.Logo, s.EpgId);

    public static Candidate[] Rank(ChannelGroup group, IEnumerable<Candidate> candidates)
    {
        var eligible = candidates.Where(c => group.ProviderId == 0 || c.ProviderId == group.ProviderId).ToArray();
        var known = eligible.Where(c => c.Quality > 0 && c.Quality >= group.MinimumQuality).ToArray();
        var unknown = eligible.Where(c => c.Quality == 0).ToArray();
        var lower = eligible.Where(c => c.Quality > 0 && c.Quality < group.MinimumQuality).ToArray();
        IEnumerable<Candidate> Sort(IEnumerable<Candidate> values) => group.QualityFirst
            ? values.OrderByDescending(c => c.Quality).ThenBy(c => c.Priority).ThenBy(c => c.ProviderId).ThenBy(c => c.StreamId)
            : values.OrderBy(c => c.Priority).ThenBy(c => c.ProviderId).ThenByDescending(c => c.Quality).ThenBy(c => c.StreamId);
        // Unknown is only a fallback; not equivalent to satisfying a quality constraint.
        return Sort(known).Concat(group.MinimumQuality == 0 || group.UnknownFallback ? Sort(unknown) : [])
            .Concat(group.LowerFallback ? Sort(lower) : []).ToArray();
    }
    private static bool Contains(string value, string filter) => string.IsNullOrWhiteSpace(filter) || value.Contains(filter.Trim(), StringComparison.OrdinalIgnoreCase);
    private static Candidate[] Eligible(ChannelGroup group, LogicalChannel channel, IEnumerable<Candidate> candidates, bool? manual)
    {
        if (manual == false || (manual is null && !group.Automatic)) return [];
        if (manual is null && !Contains(channel.Name, group.NameContains)) return [];
        var sources = candidates.Where(c => manual == true ||
            ((string.IsNullOrWhiteSpace(group.Country) || group.Country.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Contains(c.Country, StringComparer.OrdinalIgnoreCase))
             && Contains(c.Category, group.CategoryContains)));
        return Rank(group, sources);
    }
    public static async Task<List<Published>> ListAsync(CatalogDb db, int? groupId, CancellationToken ct)
    {
        var groups = await db.ChannelGroups.AsNoTracking().Where(g => g.Enabled && (groupId == null || g.Id == groupId)).OrderBy(g => g.SortOrder).ThenBy(g => g.Id).ToListAsync(ct);
        if (groups.Count == 0) return [];
        var channels = await db.LogicalChannels.AsNoTracking().OrderBy(c => c.Name).ThenBy(c => c.Id).ToListAsync(ct);
        var sources = (await Candidates(db).ToListAsync(ct)).ToLookup(c => c.ChannelId);
        var overrides = (await db.GroupOverrides.AsNoTracking().ToListAsync(ct)).ToDictionary(x => (x.GroupId,x.ChannelId), x => x.Include);
        var result = new List<Published>();
        foreach (var group in groups)
            foreach (var channel in channels)
            {
                bool? manual = overrides.TryGetValue((group.Id,channel.Id), out var include) ? include : null;
                var ranked = Eligible(group, channel, sources[channel.Id], manual);
                if (ranked.Length > 0) result.Add(new(PublicId(group.Id, channel.Id), group.Id, channel.Id, channel.Name, ranked[0]));
            }
        return result;
    }
    public static async Task<Candidate[]> ResolveAsync(CatalogDb db, int publicId, CancellationToken ct)
    {
        if (publicId >= 0 || publicId == int.MinValue || !await IsEnabled(db, ct)) return [];
        var key = -publicId;
        var groupId = key / GroupStride;
        var channelId = key % GroupStride;
        var group = await db.ChannelGroups.AsNoTracking().SingleOrDefaultAsync(x => x.Id == groupId && x.Enabled, ct);
        var channel = await db.LogicalChannels.AsNoTracking().SingleOrDefaultAsync(x => x.Id == channelId, ct);
        if (group is null || channel is null) return [];
        var manual = await db.GroupOverrides.AsNoTracking().Where(x => x.GroupId == groupId && x.ChannelId == channelId).Select(x => (bool?)x.Include).SingleOrDefaultAsync(ct);
        return Eligible(group, channel, await Candidates(db).Where(x => x.ChannelId == channelId).ToListAsync(ct), manual);
    }
}
