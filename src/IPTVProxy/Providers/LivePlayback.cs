using IPTVProxy.Data;
using System.Diagnostics;
using IPTVProxy.Security;
using Microsoft.EntityFrameworkCore;

namespace IPTVProxy.Providers;

public sealed class LivePlayback(IHttpClientFactory clients, LiveCatalog catalog, IDbContextFactory<CatalogDb> factory, ViewerAccounts accounts, ILogger<LivePlayback> log, IConfiguration config, ProviderConnectionCoordinator capacity, ViewerSessions viewers)
{
    private readonly object gate = new();
    private readonly Dictionary<string, Session> sessions = new();
    private sealed record Source(int Id, Provider Provider, long SourceId);
    private sealed class Session
    {
        public Session(string key, Source source) { Key = key; Current = source; StopToken = Stop.Token; }
        public string Key { get; }
        public Source Current { get; set; }
        public int Id => Current.Id;
        public int ProviderId => Current.Provider.Id;
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
        var clock=Stopwatch.StartNew();
        log.LogInformation("Playback requested: stream {StreamId}", id);
        var access=await accounts.Authenticate(username,password,ctx.RequestAborted);
        if (access is null || (!access.Account.AllowRemote && !ctx.RequestServices.GetRequiredService<ClientNetwork>().IsLocal(ctx))) { ctx.Response.StatusCode = 401; return; }
        var authMs=clock.ElapsedMilliseconds;
        var publicId=id;
        var handoff=config.GetValue("Playback:SingleViewerChannelSwitch",true);
        using var viewer=await viewers.Acquire(access.Account.Id,access.Account.MaxStreams,"live",publicId,ctx.RequestAborted,handoff,ViewerSessions.ClientKey(ctx),ViewerSessions.NetworkKey(ctx),ViewerSessions.AgentInfo(ctx),ctx.Items.ContainsKey("UnresolvedProxy"),ctx.Request.Headers.ContainsKey("Range"));
        if(viewer is null){log.LogWarning("Live account capacity exhausted for account {AccountId}",access.Account.Id);ctx.Response.StatusCode=503;return;}
        var viewerMs=clock.ElapsedMilliseconds;
        await using var db = await factory.CreateDbContextAsync(ctx.RequestAborted);
        int[] sourceIds = [id];
        if (id > ManagedPublication.PublicOffset)
        {
            var groupId=await db.PublishedIdentities.AsNoTracking().Where(x=>x.Id==id-ManagedPublication.PublicOffset).Select(x=>x.GroupId).SingleOrDefaultAsync(ctx.RequestAborted);
            if(!access.Allows("live",groupId)){ctx.Response.StatusCode=404;return;}
            var candidates = await ManagedPublication.ResolveAsync(db, id, ctx.RequestAborted);
            if (candidates.Length == 0) { ctx.Response.StatusCode = 404; return; }
            sourceIds = candidates.Select(c => c.StreamId).Distinct().ToArray();
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
        var sourceRows = await (from stream in db.LiveStreams.AsNoTracking()
            join provider in db.Providers.AsNoTracking() on stream.ProviderId equals provider.Id
            where sourceIds.Contains(stream.Id) && stream.Active && provider.Enabled
            select new { stream.Id, Provider = provider, stream.SourceId }).ToListAsync(ctx.RequestAborted);
        var byId = sourceRows.ToDictionary(x => x.Id, x => new Source(x.Id, x.Provider, x.SourceId));
        var sources = sourceIds.Where(byId.ContainsKey).Select(x => byId[x]).ToArray();
        if (sources.Length == 0) { ctx.Response.StatusCode = 404; return; }
        // Only share recovery sessions with identical ordered eligible source lists.
        // This prevents failover into a source excluded by another subscriber's group.
        var key = string.Join(",", sources.Select(x => x.Id));
        var resolvedMs=clock.ElapsedMilliseconds;
        Session session;
        bool start = false;
        using var switchTimeout = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted,viewer.Token);
        switchTimeout.CancelAfter(TimeSpan.FromSeconds(5));
        while (true)
        {
            Task? release = null;
            lock (gate)
            {
                if (sessions.TryGetValue(key, out var existing))
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
                    foreach (var source in sources)
                    {
                        var lease = capacity.TryReserve(source.Provider.Id, source.Provider.ConnectionLimit);
                        if (lease is null) continue;
                        session = new Session(key, source) { Lease = lease, Subscribers = 1 };
                        sessions.Add(key, session);
                        start = true;
                        goto admitted;
                    }
                    if (!handoff) { ctx.Response.StatusCode = 503; return; }
                    release = Task.Delay(100, switchTimeout.Token);
                }
            }
            try { await release!.WaitAsync(switchTimeout.Token); }
            catch (OperationCanceledException)
            {
                if (!ctx.RequestAborted.IsCancellationRequested) ctx.Response.StatusCode = 503;
                return;
            }
        }
        admitted:
        lock (gate) { id = session.Id; viewer.Bind(session.ProviderId, id); }
        var readyMs=clock.ElapsedMilliseconds;
        log.LogInformation("Stream {StreamId}: new upstream {NewUpstream}; auth {AuthMs} ms, viewer {ViewerMs} ms, resolve {ResolveMs} ms, capacity {CapacityMs} ms", id,start,authMs,viewerMs-authMs,resolvedMs-viewerMs,readyMs-resolvedMs);
        // Start with up to 1 MiB of recent history, without delaying cold starts.
        var cursor = session.Buffer.DecoderStartPosition(1024 * 1024);
        bool firstDownstream=true;
        if (start) _ = Pump(session, sources);
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
                lock (gate) viewer.Bind(session.ProviderId, session.Id);
                viewer.AddBytes(read.Count);
                if(firstDownstream){firstDownstream=false;log.LogInformation("First downstream data for stream {StreamId}: {ElapsedMs} ms total, {ReadyMs} ms after reservation",id,clock.ElapsedMilliseconds,clock.ElapsedMilliseconds-readyMs);}
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

    private async Task Pump(Session session, Source[] sources)
    {
        var ct = session.StopToken;
        await Task.Yield();
        try
        {
            var current = session.Current;
            var ordered = new[] { current }.Concat(sources.Where(x => x.Id != current.Id)).ToArray();
            foreach (var source in ordered)
            {
                ct.ThrowIfCancellationRequested();
                lock (gate)
                {
                    if (session.Current.Id != source.Id)
                    {
                        session.Lease?.Dispose();
                        session.Lease = capacity.TryReserve(source.Provider.Id, source.Provider.ConnectionLimit);
                        if (session.Lease is null) continue;
                        session.Current = source;
                        log.LogInformation("Live failover to provider {ProviderId}, stream {StreamId}", source.Provider.Id, source.Id);
                    }
                }
                for (var attempt = 0; attempt < 3; attempt++)
                {
                    ct.ThrowIfCancellationRequested();
                    if (attempt > 0)
                    {
                        log.LogInformation("Live reconnect attempt {Attempt} for stream {StreamId}", attempt, source.Id);
                        await Task.Delay(TimeSpan.FromSeconds(1), ct);
                    }
                    var dataClock = Stopwatch.StartNew();
                    var received = false;
                    try { await PumpAttempt(session, source, ct, () => { received = true; dataClock.Restart(); }); }
                    catch (Exception ex) when (!ct.IsCancellationRequested)
                    { log.LogWarning("Upstream failed for stream {StreamId}: {ErrorType}", source.Id, ex.GetType().Name); }
                    // A stable connection earns a fresh retry budget; short flapping
                    // connections do not reset it.
                    if (received && dataClock.Elapsed >= TimeSpan.FromSeconds(60)) attempt = -1;
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex) { log.LogWarning("Live recovery failed: {ErrorType}", ex.GetType().Name); }
        finally
        {
            lock (gate)
            {
                session.Closing = true;
                session.Buffer.Complete();
                sessions.Remove(session.Key);
                session.Lease?.Dispose();
                if (session.Subscribers == 0) session.Stop.Dispose();
                session.Released.TrySetResult();
            }
            log.LogInformation("Upstream ended for stream {StreamId}", session.Id);
        }
    }

    private async Task PumpAttempt(Session session, Source source, CancellationToken ct, Action onData)
    {
        var clock = Stopwatch.StartNew();
            var provider = source.Provider;
            var credentials = catalog.Credentials(provider);
            var uri = new Uri(LiveCatalog.ValidateBase(provider.BaseUrl),
                "live/" + Uri.EscapeDataString(credentials.Username) + "/" +
                Uri.EscapeDataString(credentials.Password) + "/" + source.SourceId + ".ts");
            using var client = clients.CreateClient("provider");
            using var startup = CancellationTokenSource.CreateLinkedTokenSource(ct);
            startup.CancelAfter(TimeSpan.FromSeconds(10));
            using var upstream = await OpenStream(client, uri, source.Id, startup.Token);
            log.LogInformation("Upstream provider {ProviderId}, stream {StreamId}: HTTP {StatusCode}", provider.Id, source.Id, (int)upstream.StatusCode);
            upstream.EnsureSuccessStatusCode();
            if (upstream.Content.Headers.ContentType?.MediaType is not ("video/mp2t" or "application/octet-stream" or "video/mpeg"))
                throw new InvalidDataException("Unsupported media type.");
            await using var input = await upstream.Content.ReadAsStreamAsync(ct);
            // Preserve TS packet boundaries for subscribers joining an active stream.
            var buffer = new byte[188 * 256 + 376];
            var pending = 0;
            var synchronized=false;
            var first = true;
            var lastValid = Stopwatch.StartNew();
            while (!ct.IsCancellationRequested)
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(20));
                var count = await input.ReadAsync(buffer.AsMemory(pending), timeout.Token);
                if (count == 0) { log.LogInformation("Upstream EOF for stream {StreamId}", source.Id); break; }
                var total = pending + count;
                var offset=0;
                if(!synchronized)
                {
                    offset=SharedStreamBuffer.FindSync(buffer.AsSpan(0,total));
                    if(offset<0)
                    {
                        pending=Math.Min(total,376);
                        Buffer.BlockCopy(buffer,total-pending,buffer,0,pending);
                        continue;
                    }
                    synchronized=true;
                }
                var length=0;
                while(offset+length+188<=total && buffer[offset+length]==0x47)length+=188;
                if(length>0) { session.Buffer.Append(buffer.AsSpan(offset,length)); lastValid.Restart(); }
                pending=total-offset-length;
                if(pending>=188)
                {
                    synchronized=false;
                    log.LogWarning("TS sync lost for stream {StreamId}",source.Id);
                    // Preserve a bounded tail for resynchronization across reads.
                    pending=Math.Min(pending,376);
                    Buffer.BlockCopy(buffer,total-pending,buffer,0,pending);
                }
                else Buffer.BlockCopy(buffer,offset+length,buffer,0,pending);
                if(length==0)continue;
                if (first) { log.LogInformation("First upstream data for stream {StreamId}: {Bytes} bytes after {ElapsedMs} ms", source.Id, length,clock.ElapsedMilliseconds); first = false; onData(); }

            }
    }
}
