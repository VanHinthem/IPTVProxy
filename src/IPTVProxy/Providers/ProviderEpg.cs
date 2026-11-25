using System.Text.Json;
using IPTVProxy.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace IPTVProxy.Providers;
public sealed class ProviderEpg(IDbContextFactory<CatalogDb> factory, LiveCatalog catalog, IHttpClientFactory clients, ILogger<ProviderEpg> log) : IDisposable
{
    private readonly MemoryCache cache = new(new MemoryCacheOptions { SizeLimit = 2048 });
    private readonly SemaphoreSlim fetchGate = new(4,4);
    public sealed record Listing(string id, string title, string description, string start, string end, string start_timestamp, string stop_timestamp, string lang);
    public async Task<Listing[]> GetAsync(int[] sourceIds, int limit, CancellationToken ct)
    {
        limit = Math.Clamp(limit,1,100);
        foreach(var id in sourceIds.Distinct().Take(8))
        {
            if(cache.TryGetValue<Listing[]>(id, out var cached) && cached is not null)
            {
                if(cached.Length>0)return cached.Take(limit).ToArray();
                continue;
            }
            await fetchGate.WaitAsync(ct);
            try
            {
                if(cache.TryGetValue<Listing[]>(id,out cached) && cached is not null)
                {
                    if(cached.Length>0)return cached.Take(limit).ToArray();
                    continue;
                }
                await using var db=await factory.CreateDbContextAsync(ct);
                var source=await db.LiveStreams.AsNoTracking().SingleOrDefaultAsync(x=>x.Id==id && x.Active,ct);
                if(source is null)continue;
                var provider=await db.Providers.AsNoTracking().SingleOrDefaultAsync(x=>x.Id==source.ProviderId && x.Enabled,ct);
                if(provider is null)continue;
                using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(10));
                using var client=clients.CreateClient("provider");
                var uri=new Uri(catalog.ApiUri(provider,"get_short_epg").AbsoluteUri+"&stream_id="+source.SourceId+"&limit=100");
                using var response=await client.GetAsync(uri,HttpCompletionOption.ResponseHeadersRead,timeout.Token);
                response.EnsureSuccessStatusCode();
                await using var body=await response.Content.ReadAsStreamAsync(timeout.Token);
                using var bytes=new MemoryStream();
                var buffer=new byte[8192];
                int count;
                while((count=await body.ReadAsync(buffer,timeout.Token))>0)
                {
                    if(bytes.Length+count>2*1024*1024)throw new InvalidDataException("EPG response too large");
                    bytes.Write(buffer,0,count);
                }
                using var doc=JsonDocument.Parse(bytes.ToArray());
                var listings=Parse(doc.RootElement);
                cache.Set(id,listings,new MemoryCacheEntryOptions {Size=1,AbsoluteExpirationRelativeToNow=listings.Length>0?TimeSpan.FromMinutes(15):TimeSpan.FromMinutes(2)});
                if(listings.Length>0)return listings.Take(limit).ToArray();
            }
            catch(OperationCanceledException) when(ct.IsCancellationRequested){throw;}
            catch(Exception ex){log.LogWarning("EPG request failed for source {SourceId}: {ErrorType}",id,ex.GetType().Name);cache.Set(id,Array.Empty<Listing>(),new MemoryCacheEntryOptions{Size=1,AbsoluteExpirationRelativeToNow=TimeSpan.FromMinutes(2)});}
            finally{fetchGate.Release();}
        }
        return [];
    }
    public static Listing[] Parse(JsonElement root)
    {
        if(root.ValueKind!=JsonValueKind.Object || !root.TryGetProperty("epg_listings",out var list)||list.ValueKind!=JsonValueKind.Array)return [];
        static string Read(JsonElement e,string key)=>e.TryGetProperty(key,out var v)&&v.ValueKind is JsonValueKind.String or JsonValueKind.Number?v.ToString():"";
        return list.EnumerateArray().Where(e=>e.ValueKind==JsonValueKind.Object).Take(100)
            .Select(e=>new Listing(Read(e,"id"),Read(e,"title"),Read(e,"description"),Read(e,"start"),Read(e,"end"),Read(e,"start_timestamp"),Read(e,"stop_timestamp"),Read(e,"lang")))
            .Where(e=>e.title.Length>0 && e.title.Length<=16384 && e.description.Length<=65536).ToArray();
    }
    public void Dispose(){cache.Dispose();fetchGate.Dispose();}
}
