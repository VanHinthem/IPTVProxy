using IPTVProxy.Security;
namespace IPTVProxy.Providers;
public sealed class VodPlayback(VodCatalog vod,LiveCatalog live,LocalAccounts accounts,IHttpClientFactory clients,ProviderConnectionCoordinator capacity,ILogger<VodPlayback> log)
{
    public async Task Play(HttpContext ctx,string username,string password,int id,string kind,string extension)
    {
        if(!accounts.IptvValid(username,password)){ctx.Response.StatusCode=401;return;}
        var ct=ctx.RequestAborted;
        try
        {
            var media=await vod.Resolve(id,kind,ct);
            if(media is null||!string.Equals(extension,media.Extension,StringComparison.OrdinalIgnoreCase)){ctx.Response.StatusCode=404;return;}
            using var lease=capacity.TryReserve(media.Provider.Id,media.Provider.ConnectionLimit);
            if(lease is null){ctx.Response.StatusCode=503;return;}
            var credentials=live.Credentials(media.Provider);
            var uri=new Uri(LiveCatalog.ValidateBase(media.Provider.BaseUrl),kind+"/"+Uri.EscapeDataString(credentials.Username)+"/"+Uri.EscapeDataString(credentials.Password)+"/"+media.SourceId+"."+media.Extension);
            using var client=clients.CreateClient("provider");
            using var startup=CancellationTokenSource.CreateLinkedTokenSource(ct);startup.CancelAfter(TimeSpan.FromSeconds(30));
            for(int hop=0;;hop++)
            {
                using var req=new HttpRequestMessage(ctx.Request.Method=="HEAD"?HttpMethod.Head:HttpMethod.Get,uri);
                foreach(var header in new[]{"Range","If-Range"})if(ctx.Request.Headers.TryGetValue(header,out var value))req.Headers.TryAddWithoutValidation(header,value.ToArray());
                using var response=await client.SendAsync(req,HttpCompletionOption.ResponseHeadersRead,startup.Token);
                var code=(int)response.StatusCode;
                if(code is 301 or 302 or 303 or 307 or 308)
                {
                    if(hop>=5||response.Headers.Location is null||!Uri.TryCreate(uri,response.Headers.Location,out var next)||next.Scheme is not ("http" or "https")||next.UserInfo.Length!=0||next.IsLoopback||(uri.Scheme=="https"&&next.Scheme=="http"))throw new InvalidDataException("Invalid redirect.");
                    uri=next;continue;
                }
                if(code is not (200 or 206 or 416)){ctx.Response.StatusCode=code is 404 or 410?code:502;return;}
                var type=response.Content.Headers.ContentType?.MediaType;
                if(code!=416&&type is not null&&!type.StartsWith("video/",StringComparison.OrdinalIgnoreCase)&&type!="application/octet-stream"&&type!="application/mp4"){ctx.Response.StatusCode=502;return;}
                ctx.Response.StatusCode=code;
                ctx.Response.Headers.CacheControl="no-store";ctx.Response.Headers["X-Accel-Buffering"]="no";
                if(response.Content.Headers.ContentRange is {} range)ctx.Response.Headers.ContentRange=range.ToString();
                if(response.Headers.AcceptRanges.Count>0)ctx.Response.Headers.AcceptRanges=string.Join(",",response.Headers.AcceptRanges);
                if(response.Headers.ETag is {} etag)ctx.Response.Headers.ETag=etag.ToString();
                if(response.Content.Headers.LastModified is {} modified)ctx.Response.Headers.LastModified=modified.ToString("R");
                if(code==416){ctx.Response.ContentLength=0;return;}
                ctx.Response.ContentType=type??"application/octet-stream";ctx.Response.ContentLength=response.Content.Headers.ContentLength;
                if(ctx.Request.Method=="HEAD")return;
                await using var stream=await response.Content.ReadAsStreamAsync(ct);
                var buffer=new byte[65536];
                while(true)
                {
                    using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct);timeout.CancelAfter(TimeSpan.FromSeconds(30));
                    var n=await stream.ReadAsync(buffer,timeout.Token);if(n==0)break;
                    await ctx.Response.Body.WriteAsync(buffer.AsMemory(0,n),timeout.Token);
                }
                return;
            }
        }
        catch(Exception ex)
        {
            if(!ct.IsCancellationRequested)log.LogWarning("VOD playback failed: {Kind} {Id} {ErrorType}",kind,id,ex.GetType().Name);
            if(!ctx.Response.HasStarted)ctx.Response.StatusCode=502;else ctx.Abort();
        }
    }
}
