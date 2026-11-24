using System.Collections.Concurrent;
using IPTVProxy.Data;
using IPTVProxy.Security;
using Microsoft.EntityFrameworkCore;

namespace IPTVProxy.Providers;

public sealed class LivePlayback(IHttpClientFactory clients, LiveCatalog catalog, IDbContextFactory<CatalogDb> factory, LocalAccounts accounts, ILogger<LivePlayback> log)
{
    private readonly object gate = new();
    private readonly Dictionary<int,int> active = new();

    public async Task Play(HttpContext ctx, string username, string password, int id)
    {
        log.LogInformation("Playback requested: stream {StreamId}", id);
        if (!accounts.IptvValid(username, password)) { log.LogWarning("Playback authentication rejected"); ctx.Response.StatusCode = 401; return; }
        await using var db = await factory.CreateDbContextAsync(ctx.RequestAborted);
        var stream = await db.LiveStreams.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id && x.Active, ctx.RequestAborted);
        if (stream is null) { log.LogWarning("Stream {StreamId} unavailable", id); ctx.Response.StatusCode = 404; return; }
        var provider = await db.Providers.AsNoTracking().SingleOrDefaultAsync(x => x.Id == stream.ProviderId && x.Enabled, ctx.RequestAborted);
        if (provider is null) { log.LogWarning("Provider unavailable for stream {StreamId}", id); ctx.Response.StatusCode = 404; return; }
        lock (gate)
        {
            var count = active.GetValueOrDefault(provider.Id);
            if (count >= provider.ConnectionLimit) { log.LogWarning("Provider {ProviderId} connection limit reached", provider.Id); ctx.Response.StatusCode = 503; return; }
            active[provider.Id] = count + 1;
        }
        try
        {
            var credentials = catalog.Credentials(provider);
            var uri = new Uri(LiveCatalog.ValidateBase(provider.BaseUrl),
                "live/" + Uri.EscapeDataString(credentials.Username) + "/" +
                Uri.EscapeDataString(credentials.Password) + "/" + stream.SourceId + ".ts");
            using var client = clients.CreateClient("provider");
            using var startup = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted);
            startup.CancelAfter(TimeSpan.FromSeconds(20));
            using var upstream = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, startup.Token);
            log.LogInformation("Upstream provider {ProviderId}, stream {StreamId}: HTTP {StatusCode}, media {MediaType}, redirect {Redirect}", provider.Id, id, (int)upstream.StatusCode, upstream.Content.Headers.ContentType?.MediaType switch { "video/mp2t" => "video/mp2t", "video/mpeg" => "video/mpeg", "application/octet-stream" => "application/octet-stream", "application/vnd.apple.mpegurl" => "HLS", "text/html" => "text/html", null => "missing", _ => "other" }, upstream.Headers.Location is not null);
            if (!upstream.IsSuccessStatusCode) { ctx.Response.StatusCode = 502; return; }
            var media = upstream.Content.Headers.ContentType?.MediaType;
            if (media is not ("video/mp2t" or "application/octet-stream" or "video/mpeg"))
            { ctx.Response.StatusCode = 502; return; }
            await using var input = await upstream.Content.ReadAsStreamAsync(ctx.RequestAborted);
            var buffer = new byte[64 * 1024];
            ctx.Response.ContentType = "video/mp2t";
            ctx.Response.Headers.CacheControl = "no-store";
            while (!ctx.RequestAborted.IsCancellationRequested)
            {
                using var readTimeout = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted);
                readTimeout.CancelAfter(TimeSpan.FromSeconds(20));
                var count = await input.ReadAsync(buffer, readTimeout.Token);
                if (!ctx.Response.HasStarted) log.LogInformation("First upstream read for stream {StreamId}: {Bytes} bytes", id, count);
                if (count == 0) break;
                await ctx.Response.Body.WriteAsync(buffer.AsMemory(0,count), readTimeout.Token);
                await ctx.Response.Body.FlushAsync(readTimeout.Token);
            }
        }
        catch (Exception ex)
        {
            if (!ctx.RequestAborted.IsCancellationRequested) log.LogWarning("Playback failed for stream {StreamId}: {ErrorType}", id, ex.GetType().Name);
            if (!ctx.Response.HasStarted) ctx.Response.StatusCode = 502;
            else ctx.Abort();
        }
        finally
        {
            log.LogInformation("Playback ended for stream {StreamId}", id);
            lock (gate)
            {
                var count = active[provider.Id] - 1;
                if (count == 0) active.Remove(provider.Id); else active[provider.Id] = count;
            }
        }
    }
}
