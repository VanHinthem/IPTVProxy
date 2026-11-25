using IPTVProxy.Data;
using Microsoft.EntityFrameworkCore;

namespace IPTVProxy.Providers;

public static class ChannelMatching
{
    // Explicit rematch before managed publication: protect all configured/public identities
    // and any channel with manual sources. Retain unreferenced old rows for audit.
    public static async Task RematchAsync(CatalogDb db, CancellationToken ct)
    {
        if (await db.PublicationSettings.AnyAsync(x => x.Managed, ct))
            throw new InvalidOperationException("Disable managed publication before rematching.");
        var protectedIds = (await db.LogicalChannels.Where(c => c.Manual || c.PreferredStreamId != 0).Select(c => c.Id).ToListAsync(ct)).ToHashSet();
        protectedIds.UnionWith(await db.ChannelSources.Where(l => l.Manual).Select(l => l.LogicalChannelId).Distinct().ToListAsync(ct));
        protectedIds.UnionWith(await db.GroupOverrides.Select(x => x.ChannelId).Distinct().ToListAsync(ct));
        protectedIds.UnionWith(await db.PublishedIdentities.Select(x => x.ChannelId).Distinct().ToListAsync(ct));
        var categories=(await db.LiveCategories.AsNoTracking().ToListAsync(ct)).ToDictionary(x=>(x.ProviderId,x.SourceId),x=>x.Name);
        var streams=await db.LiveStreams.AsNoTracking().Where(x=>x.Active).OrderBy(x=>x.Id).ToListAsync(ct);
        var channels=(await db.LogicalChannels.ToListAsync(ct)).ToDictionary(x=>x.Id);
        var links=(await db.ChannelSources.ToListAsync(ct)).ToDictionary(x=>x.LiveStreamId);
        var claimed=new HashSet<int>(protectedIds);
        var targets=new Dictionary<string,LogicalChannel>(StringComparer.Ordinal);
        var blocked=channels.Values.Where(c=>protectedIds.Contains(c.Id)&&c.MatchKey!="").Select(c=>c.MatchKey).ToHashSet(StringComparer.Ordinal);
        foreach(var stream in streams)
        {
            if(!links.TryGetValue(stream.Id,out var link))continue;
            var meta=ChannelName.Parse(stream.Name,categories.GetValueOrDefault((stream.ProviderId,stream.CategoryId),""));
            link.Package=meta.Package;link.Tags=meta.Tags;link.Separator=meta.Separator;
            link.ChannelType=meta.ChannelType;link.QualityOrigin=meta.QualityOrigin;link.FrameRate=meta.FrameRate;link.CatchupHint=meta.CatchupHint;
            if(protectedIds.Contains(link.LogicalChannelId))continue;
            link.NormalizedName=meta.Name;link.Country=meta.Country;link.Quality=meta.Quality;link.Codec=meta.Codec;
            var key=blocked.Contains(meta.MatchKey)?"":meta.MatchKey;
            if(key=="" || !targets.TryGetValue(key,out var target))
            {
                if(claimed.Add(link.LogicalChannelId))target=channels[link.LogicalChannelId];
                else {target=new LogicalChannel();db.LogicalChannels.Add(target);}
                target.Name=meta.Name;target.Country=meta.Country;target.MatchKey=key;
                if(key!="")targets.Add(key,target);
            }
            link.LogicalChannel=target;
            link.ReviewRequired=key=="";
        }
        await db.SaveChangesAsync(ct);
        // Empty automatic identities must not participate in future matching.
        var empty=await db.LogicalChannels.Where(c=>!c.Manual && !db.ChannelSources.Any(l=>l.LogicalChannelId==c.Id)).ToListAsync(ct);
        foreach(var channel in empty)if(!protectedIds.Contains(channel.Id))channel.MatchKey="";
        await db.SaveChangesAsync(ct);
    }
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
                if (!link.Manual && channel is not null && (channel.MatchKey != metadata.MatchKey || (metadata.MatchKey == "" && !string.Equals(link.NormalizedName, metadata.Name, StringComparison.OrdinalIgnoreCase))))
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
            link.Package = metadata.Package; link.Tags = metadata.Tags; link.Separator = metadata.Separator;
            link.ChannelType=metadata.ChannelType;link.QualityOrigin=metadata.QualityOrigin;link.FrameRate=metadata.FrameRate;link.CatchupHint=metadata.CatchupHint;
        }
        await db.SaveChangesAsync(ct);
    }
}
