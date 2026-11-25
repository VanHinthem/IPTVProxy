using System.Threading.Channels;
using IPTVProxy.Data;
using IPTVProxy.Security;
using Microsoft.EntityFrameworkCore;

namespace IPTVProxy.Providers;

public sealed class LivePlayback(IHttpClientFactory clients, LiveCatalog catalog, IDbContextFactory<CatalogDb> factory, LocalAccounts accounts, ILogger<LivePlayback> log)
{
    private readonly object gate = new();
    private readonly Dictionary<int, Session> sessions = new();
    private sealed class Session(int id, int providerId)
    {
        public int Id { get; } = id;
        public int ProviderId { get; } = providerId;
        public CancellationTokenSource Stop { get; } = new();
        public HashSet<Channel<byte[]>> Subscribers { get; } = new();
        public bool Closing { get; set; }
    }

    private async Task<HttpResponseMessage> OpenStream(HttpClient client, Uri uri, int id, CancellationToken ct)
    {
        const int maxRedirects = 5;
        for (var hop = 0; ; hop++)
        {
            var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
            if ((int)response.StatusCode is not (301 or 302 or 303 or 307 or 308)) return response;
            using (response)
            {
                var location = response.Headers.Location;
                if (hop >= maxRedirects || location is null ||
                    !Uri.TryCreate(uri, location, out var next) ||
                    next.Scheme is not ("http" or "https") || next.UserInfo.Length != 0 || next.IsLoopback ||
                    (uri.Scheme == "https" && next.Scheme == "http"))
                    throw new HttpRequestException("Upstream redirect rejected.");
                log.LogInformation("Following upstream redirect {Hop} for stream {StreamId}: HTTP {StatusCode}", hop + 1, id, (int)response.StatusCode);
                uri = next;
            }
        }
    }


    public async Task Play(HttpContext ctx, string username, string password, int id)
    {
        log.LogInformation("Playback requested: stream {StreamId}", id);
        if (!accounts.IptvValid(username, password)) { ctx.Response.StatusCode = 401; return; }
        await using var db = await factory.CreateDbContextAsync(ctx.RequestAborted);
        var stream = await db.LiveStreams.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id && x.Active, ctx.RequestAborted);
        if (stream is null) { ctx.Response.StatusCode = 404; return; }
        var provider = await db.Providers.AsNoTracking().SingleOrDefaultAsync(x => x.Id == stream.ProviderId && x.Enabled, ctx.RequestAborted);
        if (provider is null) { ctx.Response.StatusCode = 404; return; }
        var subscriber = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(32) {
            SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.Wait
        });
        Session session;
        bool start = false;
        lock (gate)
        {
            if (sessions.TryGetValue(id, out var existing))
            {
                if (existing.Closing || existing.Subscribers.Count >= 16) { ctx.Response.StatusCode = 503; return; }
                session = existing;
            }
            else
            {
                if (sessions.Values.Count(s => s.ProviderId == provider.Id) >= provider.ConnectionLimit)
                { log.LogWarning("Provider {ProviderId} connection limit reached", provider.Id); ctx.Response.StatusCode = 503; return; }
                session = new Session(id, provider.Id);
                sessions.Add(id, session);
                start = true;
            }
            session.Subscribers.Add(subscriber);
            log.LogInformation("Stream {StreamId}: {Subscribers} subscribers, new upstream {NewUpstream}", id, session.Subscribers.Count, start);
        }
        if (start) _ = Pump(session, provider, stream.SourceId);
        try
        {
            ctx.Response.ContentType = "video/mp2t";
            ctx.Response.Headers.CacheControl = "no-store";
            ctx.Response.Headers["X-Accel-Buffering"] = "no";
            await foreach (var chunk in subscriber.Reader.ReadAllAsync(ctx.RequestAborted))
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted);
                timeout.CancelAfter(TimeSpan.FromSeconds(20));
                await ctx.Response.Body.WriteAsync(chunk, timeout.Token);
                await ctx.Response.Body.FlushAsync(timeout.Token);
            }
            if (!ctx.Response.HasStarted) ctx.Response.StatusCode = 502;
        }
        catch (Exception ex)
        {
            if (!ctx.RequestAborted.IsCancellationRequested)
                log.LogWarning("Subscriber failed for stream {StreamId}: {ErrorType}", id, ex.GetType().Name);
            if (!ctx.Response.HasStarted) ctx.Response.StatusCode = 502;
            else ctx.Abort();
        }
        finally
        {
            lock (gate)
            {
                session.Subscribers.Remove(subscriber);
                subscriber.Writer.TryComplete();
                if (session.Subscribers.Count == 0 && !session.Closing)
                {
                    session.Closing = true;
                    session.Stop.Cancel();
                }
            }
        }
    }

    private async Task Pump(Session session, Provider provider, long sourceId)
    {
        var ct = session.Stop.Token;
        try
        {
            var credentials = catalog.Credentials(provider);
            var uri = new Uri(LiveCatalog.ValidateBase(provider.BaseUrl),
                "live/" + Uri.EscapeDataString(credentials.Username) + "/" +
                Uri.EscapeDataString(credentials.Password) + "/" + sourceId + ".ts");
            using var client = clients.CreateClient("provider");
            using var startup = CancellationTokenSource.CreateLinkedTokenSource(ct);
            startup.CancelAfter(TimeSpan.FromSeconds(20));
            using var upstream = await OpenStream(client, uri, session.Id, startup.Token);
            log.LogInformation("Upstream provider {ProviderId}, stream {StreamId}: HTTP {StatusCode}", provider.Id, session.Id, (int)upstream.StatusCode);
            upstream.EnsureSuccessStatusCode();
            if (upstream.Content.Headers.ContentType?.MediaType is not ("video/mp2t" or "application/octet-stream" or "video/mpeg"))
                throw new InvalidDataException("Unsupported media type.");
            await using var input = await upstream.Content.ReadAsStreamAsync(ct);
            // Preserve TS packet boundaries for subscribers joining an active stream.
            var buffer = new byte[188 * 256];
            var pending = 0;
            var first = true;
            while (!ct.IsCancellationRequested)
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(20));
                var count = await input.ReadAsync(buffer.AsMemory(pending), timeout.Token);
                if (count == 0) break;
                var total = pending + count;
                var length = total / 188 * 188;
                if (length == 0) { pending = total; continue; }
                var chunk = buffer.AsSpan(0, length).ToArray();
                pending = total - length;
                Buffer.BlockCopy(buffer, length, buffer, 0, pending);
                if (first) { log.LogInformation("First upstream data for stream {StreamId}: {Bytes} bytes", session.Id, length); first = false; }
                lock (gate)
                {
                    foreach (var sub in session.Subscribers.ToArray())
                    {
                        if (sub.Writer.TryWrite(chunk)) continue;
                        sub.Writer.TryComplete(new IOException("Subscriber too slow."));
                        session.Subscribers.Remove(sub);
                    }
                    if (session.Subscribers.Count == 0 && !session.Closing)
                    { session.Closing = true; session.Stop.Cancel(); }
                }
            }
        }
        catch (Exception ex)
        {
            if (!ct.IsCancellationRequested)
                log.LogWarning("Upstream failed for stream {StreamId}: {ErrorType}", session.Id, ex.GetType().Name);
        }
        finally
        {
            lock (gate)
            {
                session.Closing = true;
                foreach (var sub in session.Subscribers) sub.Writer.TryComplete();
                session.Subscribers.Clear();
                sessions.Remove(session.Id);
                session.Stop.Dispose();
            }
            log.LogInformation("Upstream ended for stream {StreamId}", session.Id);
        }
    }
}
