using IPTVProxy.Security;
namespace IPTVProxy.Providers;
public sealed class VodPlayback(VodCatalog vod,LiveCatalog live,ViewerAccounts accounts,IHttpClientFactory clients,VodRequestCoordinator requests,IConfiguration config,ILogger<VodPlayback> log,ViewerSessions viewers)
{
    public async Task Play(HttpContext ctx,string username,string password,int id,string kind,string extension)
    {
        log.LogInformation("VOD request: {Kind} {Id}, method {Method}, range {HasRange}",kind,id,ctx.Request.Method,ctx.Request.Headers.ContainsKey("Range"));
        var access=await accounts.Authenticate(username,password,ctx.RequestAborted);
        if(access is null || (!access.Account.AllowRemote && !ctx.RequestServices.GetRequiredService<ClientNetwork>().IsLocal(ctx))){log.LogWarning("VOD authentication rejected");ctx.Response.StatusCode=401;return;}
        var ct=ctx.RequestAborted;
        bool replaced=false;
        try
        {
            var media=await vod.Resolve(id,kind,ct,access);
            if(media is null){log.LogWarning("VOD item unavailable: {Kind} {Id}",kind,id);ctx.Response.StatusCode=404;return;}
            if(extension.Length>0&&!string.Equals(extension,media.Extension,StringComparison.OrdinalIgnoreCase))log.LogInformation("VOD requested extension differs; using catalog extension {Extension}",media.Extension);
            using var viewer=await viewers.Acquire(access.Account.Id,access.Account.MaxStreams,kind,id,ct,config.GetValue("Playback:SingleViewerChannelSwitch",true)&&ctx.Request.Method!="HEAD",ViewerSessions.ClientKey(ctx),ViewerSessions.NetworkKey(ctx),ViewerSessions.AgentInfo(ctx),ctx.Items.ContainsKey("UnresolvedProxy"),ctx.Request.Headers.ContainsKey("Range"));
            if(viewer is null){ctx.Response.StatusCode=503;return;}
            viewer.Bind(media.Provider.Id,id);
            var key=new VodRequestCoordinator.Key(media.Provider.Id,kind,id,username,ctx.Connection.RemoteIpAddress?.ToString()??"unknown");
            using var lease=await requests.Acquire(key,media.Provider.ConnectionLimit,false,ct);
            if(lease is null){log.LogWarning("VOD capacity exhausted for provider {ProviderId}, limit {Limit}",media.Provider.Id,media.Provider.ConnectionLimit);ctx.Response.StatusCode=503;return;}
            using var stop=CancellationTokenSource.CreateLinkedTokenSource(ct,lease.Token,viewer.Token);
            using var registration=lease.Token.Register(()=>{replaced=true;});
            using var handoffRegistration=viewer.Token.Register(()=>{replaced=true;});
            var streamCt=stop.Token;
            var credentials=live.Credentials(media.Provider);
            var uri=new Uri(LiveCatalog.ValidateBase(media.Provider.BaseUrl),kind+"/"+Uri.EscapeDataString(credentials.Username)+"/"+Uri.EscapeDataString(credentials.Password)+"/"+media.SourceId+"."+media.Extension);
            using var client=clients.CreateClient("provider");
            using var startup=CancellationTokenSource.CreateLinkedTokenSource(streamCt);startup.CancelAfter(TimeSpan.FromSeconds(30));
            for(int hop=0;;hop++)
            {
                using var req=new HttpRequestMessage(ctx.Request.Method=="HEAD"?HttpMethod.Head:HttpMethod.Get,uri);
                foreach(var header in new[]{"Range","If-Range"})if(ctx.Request.Headers.TryGetValue(header,out var value))req.Headers.TryAddWithoutValidation(header,value.ToArray());
                using var response=await client.SendAsync(req,HttpCompletionOption.ResponseHeadersRead,startup.Token);
                var code=(int)response.StatusCode;
                log.LogInformation("VOD upstream provider {ProviderId}, {Kind} {Id}: HTTP {Status}, hop {Hop}, content length {Length}, range {HasRange}",media.Provider.Id,kind,id,code,hop,response.Content.Headers.ContentLength,response.Content.Headers.ContentRange is not null);
                if(code is 301 or 302 or 303 or 307 or 308)
                {
                    if(hop>=5||response.Headers.Location is null||!Uri.TryCreate(uri,response.Headers.Location,out var next)||next.Scheme is not ("http" or "https")||next.UserInfo.Length!=0||next.IsLoopback||(uri.Scheme=="https"&&next.Scheme=="http"))throw new InvalidDataException("Invalid redirect.");
                    uri=next;continue;
                }
                if(code is not (200 or 206 or 416)){ctx.Response.StatusCode=code is 404 or 410?code:502;return;}
                var type=response.Content.Headers.ContentType?.MediaType;
                if(code!=416&&type is not null&&!type.StartsWith("video/",StringComparison.OrdinalIgnoreCase)&&type!="application/octet-stream"&&type!="application/mp4"&&type!="binary/octet-stream"&&type!="application/x-matroska"&&type!="application/force-download"){log.LogWarning("VOD upstream media type rejected for {Kind} {Id}",kind,id);ctx.Response.StatusCode=502;return;}
                ctx.Response.StatusCode=code;
                ctx.Response.Headers.CacheControl="no-store";ctx.Response.Headers["X-Accel-Buffering"]="no";
                if(response.Content.Headers.ContentRange is {} range)ctx.Response.Headers.ContentRange=range.ToString();
                if(response.Headers.AcceptRanges.Count>0)ctx.Response.Headers.AcceptRanges=string.Join(",",response.Headers.AcceptRanges);
                if(response.Headers.ETag is {} etag)ctx.Response.Headers.ETag=etag.ToString();
                if(response.Content.Headers.LastModified is {} modified)ctx.Response.Headers.LastModified=modified.ToString("R");
                if(code==416){ctx.Response.ContentLength=0;return;}
                ctx.Response.ContentType=type??"application/octet-stream";ctx.Response.ContentLength=response.Content.Headers.ContentLength;
                if(ctx.Request.Method=="HEAD")return;
                await using var stream=await response.Content.ReadAsStreamAsync(streamCt);
                var buffer=new byte[65536];bool first=true;
                while(true)
                {
                    using var timeout=CancellationTokenSource.CreateLinkedTokenSource(streamCt);timeout.CancelAfter(TimeSpan.FromSeconds(30));
                    var n=await stream.ReadAsync(buffer,timeout.Token);if(n==0)break;
                    if(first){log.LogInformation("VOD first bytes: {Kind} {Id}, {Count}",kind,id,n);first=false;}
                    await ctx.Response.Body.WriteAsync(buffer.AsMemory(0,n),timeout.Token);viewer.AddBytes(n);
                }
                return;
            }
        }
        catch(Exception ex)
        {
            if(replaced)log.LogInformation("VOD request superseded by another request for same item: {Kind} {Id}",kind,id);
            else if(!ct.IsCancellationRequested)log.LogWarning("VOD playback failed: {Kind} {Id} {ErrorType}",kind,id,ex.GetType().Name);
            if(!ctx.Response.HasStarted){ctx.Response.ContentLength=null;ctx.Response.Headers.Remove("Content-Range");ctx.Response.StatusCode=502;}else ctx.Abort();
        }
        finally{log.LogInformation("VOD request ended: {Kind} {Id}, HTTP {Status}, aborted {Aborted}",kind,id,ctx.Response.StatusCode,ct.IsCancellationRequested);}
    }
}
