using IPTVProxy.Data;
using IPTVProxy.Security;
using Microsoft.EntityFrameworkCore;

namespace IPTVProxy.Providers;

public sealed class LivePlayback(IHttpClientFactory clients, LiveCatalog catalog, IDbContextFactory<CatalogDb> factory, ViewerAccounts accounts, ILogger<LivePlayback> log, IConfiguration config, ProviderConnectionCoordinator capacity, ViewerSessions viewers)
{
    private readonly object gate = new();
    private readonly Dictionary<int, Session> sessions = new();
    private sealed class Session
    {
        public Session(int id, int providerId) { Id = id; ProviderId = providerId; StopToken = Stop.Token; }
        public int Id { get; }
        public int ProviderId { get; }
        public CancellationTokenSource Stop { get; } = new();
        public CancellationToken StopToken { get; }
        public int Subscribers { get; set; }
        public IDisposable? Lease { get; set; }
        public SharedStreamBuffer Buffer { get; } = new(32 * 1024 * 1024 / 188 * 188);
        public bool Closing { get; set; }
        public TaskCompletionSource Released { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
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
        var access=await accounts.Authenticate(username,password,ctx.RequestAborted);
        if (access is null || (!access.Account.AllowRemote && !ctx.RequestServices.GetRequiredService<ClientNetwork>().IsLocal(ctx))) { ctx.Response.StatusCode = 401; return; }
        var publicId=id;
        var handoff=ViewerSessions.SinglePlayerHandoff(config,access.Account.Username);
        using var viewer=await viewers.Acquire(access.Account.Id,access.Account.MaxStreams,"live",publicId,ctx.RequestAborted,handoff);
        if(viewer is null){log.LogWarning("Live account capacity exhausted for account {AccountId}",access.Account.Id);ctx.Response.StatusCode=503;return;}
        await using var db = await factory.CreateDbContextAsync(ctx.RequestAborted);
        if (id > ManagedPublication.PublicOffset)
        {
            var groupId=await db.PublishedIdentities.AsNoTracking().Where(x=>x.Id==id-ManagedPublication.PublicOffset).Select(x=>x.GroupId).SingleOrDefaultAsync(ctx.RequestAborted);
            if(!access.Allows("live",groupId)){ctx.Response.StatusCode=404;return;}
            var candidates = await ManagedPublication.ResolveAsync(db, id, ctx.RequestAborted);
            if (candidates.Length == 0) { ctx.Response.StatusCode = 404; return; }
            // Select a currently available source; the existing session reservation remains
            // authoritative for connection limits if concurrent requests race.
            lock (gate)
            {
                var selected = candidates.FirstOrDefault(c =>
                    sessions.TryGetValue(c.StreamId, out var active) && !active.Closing && active.Subscribers < 16 ||
                    capacity.HasCapacity(c.ProviderId,c.Limit));
                id = (selected ?? candidates[0]).StreamId;
            }
        }
        else if (await ManagedPublication.IsEnabled(db, ctx.RequestAborted))
        {
            ctx.Response.StatusCode = 404;
            return;
        }
        if(!await ManagedPublication.IsEnabled(db,ctx.RequestAborted))
        {
            var category=await (from s in db.LiveStreams.AsNoTracking() join c in db.LiveCategories.AsNoTracking() on new {s.ProviderId,SourceId=s.CategoryId} equals new {c.ProviderId,c.SourceId} where s.Id==id&&s.Active&&c.Active select c.Id).SingleOrDefaultAsync(ctx.RequestAborted);
            if(category==0||!access.Allows("raw",category)){ctx.Response.StatusCode=404;return;}
        }
        var stream = await db.LiveStreams.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id && x.Active, ctx.RequestAborted);
        if (stream is null) { ctx.Response.StatusCode = 404; return; }
        var provider = await db.Providers.AsNoTracking().SingleOrDefaultAsync(x => x.Id == stream.ProviderId && x.Enabled, ctx.RequestAborted);
        if (provider is null) { ctx.Response.StatusCode = 404; return; }
        viewer.Bind(provider.Id,id);
        Session session;
        bool start = false;
        using var switchTimeout = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted,viewer.Token);
        switchTimeout.CancelAfter(TimeSpan.FromSeconds(5));
        while (true)
        {
            Task? release = null;
            lock (gate)
            {
                if (sessions.TryGetValue(id, out var existing))
                {
                    if (existing.Closing) release = existing.Released.Task;
                    else
                    {
                        if (existing.Subscribers >= 16) { ctx.Response.StatusCode = 503; return; }
                        session = existing;
                        session.Subscribers++;
                        break;
                    }
                }
                else
                {
                    var occupied = sessions.Values.Where(s => s.ProviderId == provider.Id).ToArray();
                    if (occupied.Length < provider.ConnectionLimit)
                    {
                        var lease=capacity.TryReserve(provider.Id,provider.ConnectionLimit);
                        if(lease is not null)
                        {
                            session = new Session(id, provider.Id){Lease=lease};
                            sessions.Add(id, session);
                            session.Subscribers++;
                            start = true;
                            break;
                        }
                    }
                    var previous = occupied.FirstOrDefault(s => s.Closing);
                    if (previous is null)
                    {
                        if(!handoff){log.LogWarning("Provider {ProviderId} connection limit reached", provider.Id);ctx.Response.StatusCode=503;return;}
                        release=Task.Delay(100,switchTimeout.Token);
                    }
                    else release = previous.Released.Task;
                }
            }
            try { await release!.WaitAsync(switchTimeout.Token); }
            catch (OperationCanceledException)
            {
                if (!ctx.RequestAborted.IsCancellationRequested) ctx.Response.StatusCode = 503;
                return;
            }
        }
        log.LogInformation("Stream {StreamId}: new upstream {NewUpstream}", id, start);
        // Start with up to 1 MiB of recent history, without delaying cold starts.
        var cursor = session.Buffer.StartPosition(1024 * 1024);
        if (start) _ = Pump(session, provider, stream.SourceId);
        try
        {
            ctx.Response.ContentType = "video/mp2t";
            ctx.Response.Headers.CacheControl = "no-store";
            ctx.Response.Headers["X-Accel-Buffering"] = "no";
            var output = new byte[188 * 348]; // ~64 KiB, packet aligned
            using var playbackStop = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted, session.StopToken,viewer.Token);
            var ct = playbackStop.Token;
            while (!ct.IsCancellationRequested)
            {
                var read = session.Buffer.Read(cursor, output);
                cursor = read.Next;
                if (read.Skipped > 0)
                    log.LogWarning("Subscriber overrun for stream {StreamId}: skipped {Bytes} bytes", id, read.Skipped);
                if (read.Count == 0)
                {
                    if (read.Completed) break;
                    await read.Changed.WaitAsync(ct);
                    continue;
                }
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(20));
                await ctx.Response.Body.WriteAsync(output.AsMemory(0, read.Count), timeout.Token);
                viewer.AddBytes(read.Count);
            }
            if (!ctx.Response.HasStarted) ctx.Response.StatusCode = 502;
        }
        catch (Exception ex)
        {
            if (!ctx.RequestAborted.IsCancellationRequested && !session.StopToken.IsCancellationRequested && !viewer.Token.IsCancellationRequested)
                log.LogWarning("Subscriber failed for stream {StreamId}: {ErrorType}", id, ex.GetType().Name);
            if (!ctx.Response.HasStarted) ctx.Response.StatusCode = 502;
            else ctx.Abort();
        }
        finally
        {
            lock (gate)
            {
                session.Subscribers--;
                log.LogInformation("Subscriber left stream {StreamId}; remaining {Subscribers}, request aborted {Aborted}", id, session.Subscribers, ctx.RequestAborted.IsCancellationRequested);
                if (session.Subscribers == 0 && !session.Closing)
                {
                    session.Closing = true;
                    session.Stop.Cancel();
                }
                if (session.Subscribers == 0 && session.Released.Task.IsCompleted) session.Stop.Dispose();
            }
        }
    }

    private async Task Pump(Session session, Provider provider, long sourceId)
    {
        var ct = session.Stop.Token;
        await Task.Yield();
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
                if (count == 0) { log.LogInformation("Upstream EOF for stream {StreamId}", session.Id); break; }
                var total = pending + count;
                var length = total / 188 * 188;
                if (length == 0) { pending = total; continue; }
                session.Buffer.Append(buffer.AsSpan(0, length));
                pending = total - length;
                Buffer.BlockCopy(buffer, length, buffer, 0, pending);
                if (first) { log.LogInformation("First upstream data for stream {StreamId}: {Bytes} bytes", session.Id, length); first = false; }

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
                session.Buffer.Complete();
                sessions.Remove(session.Id);
                session.Lease?.Dispose();
                if (session.Subscribers == 0) session.Stop.Dispose();
                session.Released.TrySetResult();
            }
            log.LogInformation("Upstream ended for stream {StreamId}", session.Id);
        }
    }
}
