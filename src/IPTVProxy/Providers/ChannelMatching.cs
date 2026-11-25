using IPTVProxy.Data;
using Microsoft.EntityFrameworkCore;

namespace IPTVProxy.Providers;

public static class ChannelMatching
{
    // Called under the catalog import gate and within its database transaction.
    public static async Task ReconcileAsync(CatalogDb db, CancellationToken ct)
    {
        var categories = (await db.LiveCategories.AsNoTracking().ToListAsync(ct))
            .ToDictionary(x => (x.ProviderId, x.SourceId), x => x.Name);
        var streams = await db.LiveStreams.AsNoTracking().Where(x => x.Active).ToListAsync(ct);
        var channels = await db.LogicalChannels.ToListAsync(ct);
        var byId = channels.ToDictionary(x => x.Id);
        var links = (await db.ChannelSources.ToListAsync(ct)).ToDictionary(x => x.LiveStreamId);
        // Multiple channels with the same key (e.g. manual splits) disable automatic assignment.
        var byKey = channels.Where(x => x.MatchKey != "").GroupBy(x => x.MatchKey)
            .ToDictionary(x => x.Key, x => x.ToList(), StringComparer.Ordinal);
        foreach (var stream in streams)
        {
            var metadata = ChannelName.Parse(stream.Name, categories.GetValueOrDefault((stream.ProviderId, stream.CategoryId), ""));
            if (links.TryGetValue(stream.Id, out var link))
            {
                // Never move an existing source automatically; a changed identity needs review.
                var channel = byId.GetValueOrDefault(link.LogicalChannelId);
                if (!link.Manual && channel is not null && channel.MatchKey != metadata.MatchKey)
                    link.ReviewRequired = true;
            }
            else
            {
                LogicalChannel? target = null;
                var candidates = metadata.MatchKey == "" ? null : byKey.GetValueOrDefault(metadata.MatchKey);
                if (candidates is { Count: 1 } && !candidates[0].Manual) target = candidates[0];
                if (target is null)
                {
                    target = new LogicalChannel { Name = metadata.Name, Country = metadata.Country, MatchKey = metadata.MatchKey };
                    db.LogicalChannels.Add(target);
                    if (metadata.MatchKey != "")
                    {
                        if (candidates is null) byKey[metadata.MatchKey] = new() { target };
                        else candidates.Add(target);
                    }
                }
                link = new ChannelSource { LiveStreamId = stream.Id, LogicalChannel = target, ReviewRequired = metadata.MatchKey == "" || candidates is { Count: > 1 } };
                db.ChannelSources.Add(link);
            }
            link.NormalizedName = metadata.Name;
            link.Country = metadata.Country;
            link.Quality = metadata.Quality;
            link.Codec = metadata.Codec;
        }
        await db.SaveChangesAsync(ct);
    }
}
