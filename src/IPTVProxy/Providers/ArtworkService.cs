using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using IPTVProxy.Data;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace IPTVProxy.Providers;

public sealed class ArtworkService
{
    private readonly IDbContextFactory<CatalogDb> factory;
    private readonly IHttpClientFactory clients;
    private readonly ILogger<ArtworkService> log;
    private readonly IDataProtector protector;
    private readonly string directory, origin;
    private readonly byte[] signingKey;
    private readonly long maximumBytes;
    private readonly SemaphoreSlim downloads=new(4,4), disk=new(1,1);
    private readonly MemoryCache failures=new(new MemoryCacheOptions{SizeLimit=4096});
    private const int MaxImage=4*1024*1024;
    public sealed record Image(byte[] Bytes,string ContentType);
    private static readonly Image Placeholder=new(Encoding.UTF8.GetBytes("""<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 160 100"><rect width="160" height="100" rx="10" fill="#e5e7eb"/><rect x="37" y="22" width="86" height="54" rx="6" fill="none" stroke="#64748b" stroke-width="5"/><path d="M66 86h28" stroke="#64748b" stroke-width="5" stroke-linecap="round"/></svg>"""),"image/svg+xml");

    public ArtworkService(IDbContextFactory<CatalogDb> factory,IHttpClientFactory clients,IDataProtectionProvider protection,IConfiguration config,ILogger<ArtworkService> log)
    {
        this.factory=factory;this.clients=clients;this.log=log;
        protector=protection.CreateProtector("iptvproxy-artwork-v1");
        origin=(config["PublicBaseUrl"]??"http://localhost:8080").TrimEnd('/');
        var data=Path.GetFullPath(config["DataDirectory"]??"data");
        directory=Path.Combine(data,"artwork-cache");Directory.CreateDirectory(directory);
        var keyPath=Path.Combine(data,"artwork-signing.key");
        if(!File.Exists(keyPath))
        {
            var temp=keyPath+"."+Guid.NewGuid().ToString("N");
            File.WriteAllBytes(temp,RandomNumberGenerator.GetBytes(32));
            try{File.Move(temp,keyPath,false);}catch(IOException) when(File.Exists(keyPath)){File.Delete(temp);}
        }
        signingKey=File.ReadAllBytes(keyPath);
        if(signingKey.Length!=32)throw new InvalidOperationException("Invalid artwork signing key.");
        maximumBytes=Math.Clamp(config.GetValue("Artwork:CacheMegabytes",256),16,2048)*1024L*1024;
    }
    public static bool ValidUrl(string? value)=>value is {Length:>0 and <=2048}&&Uri.TryCreate(value,UriKind.Absolute,out var uri)&&uri.Scheme is "http" or "https"&&uri.UserInfo.Length==0&&!uri.IsLoopback&&uri.Fragment.Length==0;
    private string Sign(string value)=>Convert.ToHexString(HMACSHA256.HashData(signingKey,Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    public string ChannelUrl(int id,bool raw=false)
    {
        var kind=raw?"raw":"channel";var value=kind+"/"+id.ToString(CultureInfo.InvariantCulture);
        return origin+"/artwork/"+value+"/"+Sign(value);
    }
    public string ProgrammeUrl(string url)=>ValidUrl(url)?origin+"/artwork/programme/"+protector.Protect(url):"";
    public async Task<string> GetOverrideAsync(int id,CancellationToken ct)
    {
        await using var db=await factory.CreateDbContextAsync(ct);await db.Database.OpenConnectionAsync(ct);
        await using var cmd=((SqliteConnection)db.Database.GetDbConnection()).CreateCommand();
        cmd.CommandText="SELECT Url FROM ChannelArtwork WHERE ChannelId=$id";cmd.Parameters.AddWithValue("$id",id);
        return await cmd.ExecuteScalarAsync(ct) as string??"";
    }
    public async Task SetOverrideAsync(int id,string url,CancellationToken ct)
    {
        url=url.Trim();if(url!=""&&!ValidUrl(url))throw new ArgumentException("Gebruik een geldige openbare HTTP(S)-afbeeldings-URL.");
        await using var db=await factory.CreateDbContextAsync(ct);await db.Database.OpenConnectionAsync(ct);
        await using var cmd=((SqliteConnection)db.Database.GetDbConnection()).CreateCommand();
        cmd.CommandText=url==""?"DELETE FROM ChannelArtwork WHERE ChannelId=$id":"INSERT OR REPLACE INTO ChannelArtwork(ChannelId,Url) VALUES($id,$url)";
        cmd.Parameters.AddWithValue("$id",id);if(url!="")cmd.Parameters.AddWithValue("$url",url);
        await cmd.ExecuteNonQueryAsync(ct);
    }
    public async Task<Image?> ChannelAsync(string kind,int id,string signature,CancellationToken ct)
    {
        if(kind is not ("channel" or "raw")||id<=0||signature.Length!=64||!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(signature),Encoding.ASCII.GetBytes(Sign(kind+"/"+id.ToString(CultureInfo.InvariantCulture)))))return null;
        await using var db=await factory.CreateDbContextAsync(ct);
        var urls=new List<string>();
        if(kind=="channel")
        {
            var channel=await db.LogicalChannels.AsNoTracking().SingleOrDefaultAsync(c=>c.Id==id,ct);
            if(channel is null)return null;
            var sources=await ManagedPublication.Candidates(db,id).ToListAsync(ct);
            if(sources.Count==0)return null;
            urls.Add(await GetOverrideAsync(id,ct));
            urls.AddRange(sources.OrderByDescending(s=>s.StreamId==channel.PreferredStreamId).ThenBy(s=>s.Priority).ThenBy(s=>s.ProviderId).ThenByDescending(s=>s.Quality).ThenBy(s=>s.StreamId).Select(s=>s.Logo));
        }
        else
        {
            var url=await (from s in db.LiveStreams.AsNoTracking() join p in db.Providers.AsNoTracking() on s.ProviderId equals p.Id
                join c in db.LiveCategories.AsNoTracking() on new{s.ProviderId,SourceId=s.CategoryId} equals new{c.ProviderId,c.SourceId}
                where s.Id==id&&s.Active&&p.Enabled&&c.Active select s.Logo).SingleOrDefaultAsync(ct);
            if(url is null)return null;urls.Add(url);
        }
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct);timeout.CancelAfter(TimeSpan.FromSeconds(18));
        try
        {
            foreach(var url in urls.Where(ValidUrl).Distinct(StringComparer.Ordinal).Take(5))
            {
                var image=await FetchAsync(url,timeout.Token);if(image is not null)return image;
            }
        }
        catch(OperationCanceledException) when(!ct.IsCancellationRequested){}
        return Placeholder;
    }
    public async Task<Image?> ProgrammeAsync(string token,CancellationToken ct)
    {
        if(token.Length>4096)return null;
        string url;try{url=protector.Unprotect(token);}catch(CryptographicException){return null;}catch(FormatException){return null;}
        if(!ValidUrl(url))return null;
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct);timeout.CancelAfter(TimeSpan.FromSeconds(12));
        try{return await FetchAsync(url,timeout.Token)??Placeholder;}catch(OperationCanceledException) when(!ct.IsCancellationRequested){return Placeholder;}
    }
    private static string? Format(byte[] b)
    {
        if(b.Length>=8&&b.AsSpan(0,8).SequenceEqual(new byte[]{137,80,78,71,13,10,26,10}))return "image/png";
        if(b.Length>=3&&b[0]==255&&b[1]==216&&b[2]==255)return "image/jpeg";
        if(b.Length>=6&&(b.AsSpan(0,6).SequenceEqual("GIF87a"u8)||b.AsSpan(0,6).SequenceEqual("GIF89a"u8)))return "image/gif";
        if(b.Length>=12&&b.AsSpan(0,4).SequenceEqual("RIFF"u8)&&b.AsSpan(8,4).SequenceEqual("WEBP"u8))return "image/webp";
        return null;
    }
    private async Task<Image?> ReadAsync(string path,bool fresh,CancellationToken ct)
    {
        await disk.WaitAsync(ct);
        try
        {
            var info=new FileInfo(path);
            if(!info.Exists||info.Length>MaxImage||info.Length==0||DateTime.UtcNow-info.LastWriteTimeUtc>TimeSpan.FromDays(fresh?7:30))return null;
            var data=await File.ReadAllBytesAsync(path,ct);var format=Format(data);
            if(format is null)return null;
            File.SetLastAccessTimeUtc(path,DateTime.UtcNow);return new Image(data,format);
        }
        catch(IOException){return null;}catch(UnauthorizedAccessException){return null;}
        finally{disk.Release();}
    }
    private async Task<Image?> FetchAsync(string url,CancellationToken ct)
    {
        var key=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url)));
        var path=Path.Combine(directory,key+".img");
        var cached=await ReadAsync(path,true,ct);if(cached is not null)return cached;
        if(failures.TryGetValue(key,out _))return await ReadAsync(path,false,ct);
        await downloads.WaitAsync(ct);
        try
        {
            cached=await ReadAsync(path,true,ct);if(cached is not null)return cached;
            if(failures.TryGetValue(key,out _))return await ReadAsync(path,false,ct);
            using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct);timeout.CancelAfter(TimeSpan.FromSeconds(5));
            try
            {
                var uri=new Uri(url);using var client=clients.CreateClient("provider");
                for(var hop=0;hop<=3;hop++)
                {
                    using var response=await client.GetAsync(uri,HttpCompletionOption.ResponseHeadersRead,timeout.Token);
                    if((int)response.StatusCode is 301 or 302 or 303 or 307 or 308)
                    {
                        if(hop==3||response.Headers.Location is null)break;
                        var next=new Uri(uri,response.Headers.Location);
                        if(!ValidUrl(next.AbsoluteUri)||(uri.Scheme=="https"&&next.Scheme!="https"))break;
                        uri=next;continue;
                    }
                    response.EnsureSuccessStatusCode();
                    if(response.Content.Headers.ContentLength>MaxImage)break;
                    await using var stream=await response.Content.ReadAsStreamAsync(timeout.Token);
                    using var bytes=new MemoryStream();var buffer=new byte[16384];int n;
                    while((n=await stream.ReadAsync(buffer,timeout.Token))>0){if(bytes.Length+n>MaxImage)throw new InvalidDataException("Artwork size limit.");bytes.Write(buffer,0,n);}
                    var data=bytes.ToArray();var type=Format(data);if(type is null)break;
                    await StoreAsync(path,data,ct);return new Image(data,type);
                }
            }
            catch(OperationCanceledException) when(ct.IsCancellationRequested){throw;}
            catch(Exception ex){log.LogDebug("Artwork fetch failed: {ErrorType}",ex.GetType().Name);}
            failures.Set(key,true,new MemoryCacheEntryOptions{Size=1,AbsoluteExpirationRelativeToNow=TimeSpan.FromMinutes(10)});
            return await ReadAsync(path,false,ct);
        }
        finally{downloads.Release();}
    }
    private async Task StoreAsync(string path,byte[] bytes,CancellationToken ct)
    {
        await disk.WaitAsync(ct);var temp=path+"."+Guid.NewGuid().ToString("N")+".tmp";
        try
        {
            await File.WriteAllBytesAsync(temp,bytes,ct);File.Move(temp,path,true);
            var files=new DirectoryInfo(directory).GetFiles("*.img").OrderBy(x=>x.LastAccessTimeUtc).ToArray();
            var total=files.Sum(x=>x.Length);
            foreach(var file in files)
            {
                if(total<=maximumBytes&&DateTime.UtcNow-file.LastWriteTimeUtc<=TimeSpan.FromDays(30))continue;
                var length=file.Length;file.Delete();total-=length;
            }
        }
        catch(IOException){log.LogWarning("Artwork cache write failed");}catch(UnauthorizedAccessException){log.LogWarning("Artwork cache write denied");}
        finally{try{File.Delete(temp);}catch(IOException){}catch(UnauthorizedAccessException){}disk.Release();}
    }
}
