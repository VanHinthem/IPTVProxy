using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using IPTVProxy.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace IPTVProxy.Providers;
public sealed class XmlTvGuide(IDbContextFactory<CatalogDb> factory,LiveCatalog catalog,IHttpClientFactory clients,ILogger<XmlTvGuide> log,IConfiguration config,ArtworkService artwork)
{
    private readonly SemaphoreSlim gate=new(1,1);
    private readonly SemaphoreSlim publicationGate=new(1,1);
    private long generation;
    private string? cachedSignature;
    private long cachedGeneration=-1;
    private DateTimeOffset cachedAt;
    private readonly string cacheDirectory=Path.Combine(Path.GetFullPath(config["DataDirectory"]??"data"),"xmltv-cache");
    public void Invalidate()=>Interlocked.Increment(ref generation);
    public async Task RefreshAsync(CancellationToken ct)
    {
        if(!await gate.WaitAsync(0,ct))return;
        try
        {
            await using var db=await factory.CreateDbContextAsync(ct);
            var providers=await db.Providers.AsNoTracking().Where(p=>p.Enabled).ToListAsync(ct);
            foreach(var p in providers)
            {
                try{await ImportAsync(p,ct);}
                catch(OperationCanceledException) when(ct.IsCancellationRequested){throw;}
                catch(HttpRequestException ex){log.LogWarning("XMLTV import failed for provider {ProviderId}: HTTP {Status}, {ErrorType}",p.Id,ex.StatusCode is null?"unknown":((int)ex.StatusCode.Value).ToString(),ex.HttpRequestError);}
                catch(Exception ex){log.LogWarning("XMLTV import failed for provider {ProviderId}: {ErrorType}",p.Id,ex.GetType().Name);}
            }
        }
        finally{gate.Release();}
    }
    private async Task ImportAsync(Provider p,CancellationToken ct)
    {
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(5));var token=timeout.Token;
        var credentials=catalog.Credentials(p);
        var uri=new Uri(LiveCatalog.ValidateBase(p.BaseUrl),"xmltv.php?username="+Uri.EscapeDataString(credentials.Username)+"&password="+Uri.EscapeDataString(credentials.Password));
        using var client=clients.CreateClient("provider");
        using var response=await FetchAsync(client,uri,p.Id,token);
        response.EnsureSuccessStatusCode();
        await using var body=await response.Content.ReadAsStreamAsync(token);
        // Bound compressed and uncompressed input; no DTD/network entity resolution.
        using var input=new MemoryStream();
        var buffer=new byte[65536];int n;
        while((n=await body.ReadAsync(buffer,token))>0){if(input.Length+n>128L*1024*1024)throw new InvalidDataException("XMLTV input too large");input.Write(buffer,0,n);}
        input.Position=0;
        using var decoded=input.Length>=2&&input.GetBuffer()[0]==0x1f&&input.GetBuffer()[1]==0x8b?(Stream)new GZipStream(input,CompressionMode.Decompress):input;
        using var reader=XmlReader.Create(decoded,new XmlReaderSettings{DtdProcessing=DtdProcessing.Ignore,XmlResolver=null,MaxCharactersInDocument=256L*1024*1024});
        var entries=new List<(string Id,string Start,string Stop,string Title,string Description,string Artwork)>();
        var now=DateTimeOffset.UtcNow;
        reader.MoveToContent();if(reader.LocalName!="tv")throw new InvalidDataException("Invalid XMLTV");
        while(!reader.EOF)
        {
            token.ThrowIfCancellationRequested();
            if(reader.NodeType==XmlNodeType.Element&&reader.LocalName=="programme")
            {
                var e=(XElement)XNode.ReadFrom(reader);
                var id=(string?)e.Attribute("channel")??"";
                var start=Time((string?)e.Attribute("start"));var stop=Time((string?)e.Attribute("stop"));
                var title=e.Elements().FirstOrDefault(x=>x.Name.LocalName=="title")?.Value??"";
                var desc=e.Elements().FirstOrDefault(x=>x.Name.LocalName=="desc")?.Value??"";
                var image=e.Elements().Where(x=>x.Name.LocalName is "icon" or "image").Select(x=>(string?)x.Attribute("src")??x.Value).FirstOrDefault(ArtworkService.ValidUrl)??"";
                if(id.Length is >0 and <=512&&start is not null&&stop is not null&&stop>start&&stop>now.AddDays(-1)&&start<now.AddDays(8)&&title.Length>0)
                {
                    entries.Add((id,start.Value.ToString("O"),stop.Value.ToString("O"),title[..Math.Min(title.Length,2048)],desc[..Math.Min(desc.Length,16384)],image));
                    if(entries.Count>500000)throw new InvalidDataException("XMLTV programme limit exceeded");
                }
                continue;
            }
            reader.Read();
        }
        if(entries.Count==0)throw new InvalidDataException("Empty XMLTV guide; previous data retained");
        await using var db=await factory.CreateDbContextAsync(token);
        await db.Database.OpenConnectionAsync(token);
        var connection=(SqliteConnection)db.Database.GetDbConnection();
        await using var tx=(SqliteTransaction)await connection.BeginTransactionAsync(token);
        await using var cmd=connection.CreateCommand();cmd.Transaction=tx;
        cmd.CommandText="DELETE FROM EpgPrograms WHERE ProviderId=$p";cmd.Parameters.AddWithValue("$p",p.Id);await cmd.ExecuteNonQueryAsync(token);
        cmd.CommandText="INSERT OR REPLACE INTO EpgPrograms(ProviderId,EpgId,Start,Stop,Title,Description,Artwork) VALUES($p,$id,$start,$stop,$title,$desc,$art)";
        foreach(var key in new[]{"$id","$start","$stop","$title","$desc","$art"})cmd.Parameters.AddWithValue(key,"");
        foreach(var e in entries)
        {
            token.ThrowIfCancellationRequested();cmd.Parameters["$id"].Value=e.Id;cmd.Parameters["$start"].Value=e.Start;cmd.Parameters["$stop"].Value=e.Stop;cmd.Parameters["$title"].Value=e.Title;cmd.Parameters["$desc"].Value=e.Description;cmd.Parameters["$art"].Value=e.Artwork;
            await cmd.ExecuteNonQueryAsync(token);
        }
        cmd.Parameters.Clear();cmd.CommandText="INSERT OR REPLACE INTO EpgRefreshState VALUES($p,$updated,$count)";
        cmd.Parameters.AddWithValue("$p",p.Id);cmd.Parameters.AddWithValue("$updated",now.ToString("O"));cmd.Parameters.AddWithValue("$count",entries.Count);await cmd.ExecuteNonQueryAsync(token);
        await tx.CommitAsync(token);
        Invalidate();
        log.LogInformation("XMLTV imported for provider {ProviderId}: {Count} programmes, {ArtworkCount} with artwork",p.Id,entries.Count,entries.Count(e=>e.Artwork!=""));
    }
    private async Task<HttpResponseMessage> FetchAsync(HttpClient client,Uri uri,int providerId,CancellationToken ct)
    {
        for(var hop=0;hop<=5;hop++)
        {
            var response=await client.GetAsync(uri,HttpCompletionOption.ResponseHeadersRead,ct);
            var status=(int)response.StatusCode;
            if(status is not (301 or 302 or 303 or 307 or 308))return response;
            using(response)
            {
                var location=response.Headers.Location;
                if(location is null||hop==5)throw new HttpRequestException("XMLTV redirect missing or limit exceeded",null,response.StatusCode);
                var next=new Uri(uri,location);
                if(next.Scheme is not ("http" or "https") || next.UserInfo.Length!=0 || next.IsLoopback ||
                    (uri.Scheme=="https"&&next.Scheme!="https"))
                    throw new HttpRequestException("Unsafe XMLTV redirect",null,response.StatusCode);
                // Do not copy credentials or headers to a redirected URL. The provider supplies Location.
                // All actual connections still use the public-address-only provider handler.
                log.LogInformation("XMLTV redirect {Hop} for provider {ProviderId}: HTTP {Status}",hop+1,providerId,status);
                uri=next;
            }
        }
        throw new InvalidOperationException("Unreachable redirect state");
    }
    public async Task<ProviderEpg.Listing[]> GetListingsAsync(int[] sourceIds,int limit,bool full,CancellationToken ct)
    {
        if(sourceIds.Length==0)return [];
        await using var db=await factory.CreateDbContextAsync(ct);
        var sources=await (from s in db.LiveStreams.AsNoTracking()
            join p in db.Providers.AsNoTracking() on s.ProviderId equals p.Id
            where sourceIds.Contains(s.Id)&&s.Active&&p.Enabled&&s.EpgId!=""
            orderby p.Priority,p.Id,s.Id
            select new {s.ProviderId,s.EpgId}).Distinct().ToListAsync(ct);
        await db.Database.OpenConnectionAsync(ct);
        var connection=(SqliteConnection)db.Database.GetDbConnection();
        await using var cmd=connection.CreateCommand();
        cmd.CommandText="SELECT Start,Stop,Title,Description FROM EpgPrograms WHERE ProviderId=$p AND EpgId=$id AND Stop>$now ORDER BY Start LIMIT $limit";
        cmd.Parameters.AddWithValue("$p",0);cmd.Parameters.AddWithValue("$id","");
        var now=DateTimeOffset.UtcNow;
        cmd.Parameters.AddWithValue("$now",(full?now.AddHours(-6):now).ToString("O"));
        cmd.Parameters.AddWithValue("$limit",Math.Clamp(limit,1,100));
        foreach(var source in sources)
        {
            cmd.Parameters["$p"].Value=source.ProviderId;cmd.Parameters["$id"].Value=source.EpgId;
            await using var rows=await cmd.ExecuteReaderAsync(ct);
            var result=new List<ProviderEpg.Listing>();
            while(await rows.ReadAsync(ct))
            {
                var start=DateTimeOffset.Parse(rows.GetString(0),CultureInfo.InvariantCulture).ToUniversalTime();
                var stop=DateTimeOffset.Parse(rows.GetString(1),CultureInfo.InvariantCulture).ToUniversalTime();
                result.Add(new ProviderEpg.Listing(start.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture),
                    Convert.ToBase64String(Encoding.UTF8.GetBytes(rows.GetString(2))),
                    Convert.ToBase64String(Encoding.UTF8.GetBytes(rows.GetString(3))),
                    start.ToString("yyyy-MM-dd HH:mm:ss",CultureInfo.InvariantCulture),
                    stop.ToString("yyyy-MM-dd HH:mm:ss",CultureInfo.InvariantCulture),
                    start.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture),stop.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture),""));
            }
            if(result.Count>0)return result.ToArray();
        }
        return [];
    }
    public static DateTimeOffset? Time(string? value)
    {
        if(string.IsNullOrWhiteSpace(value))return null;
        var s=value.Trim();
        if(s.Length==20&&(s[15]=='+'||s[15]=='-'))s=s.Insert(18,":");
        return DateTimeOffset.TryParseExact(s,new[]{"yyyyMMddHHmmss zzz","yyyyMMddHHmmss"},CultureInfo.InvariantCulture,DateTimeStyles.AssumeUniversal,out var dt)?dt.ToUniversalTime():null;
    }
    // Open a stable file handle before releasing the gate; later atomic replacement
    // cannot change the bytes already being downloaded by another client.
    public async Task<FileStream?> OpenGuideAsync(CancellationToken ct)
    {
        await publicationGate.WaitAsync(ct);
        string? temporary=null;
        try
        {
            Directory.CreateDirectory(cacheDirectory);
            await using var db=await factory.CreateDbContextAsync(ct);
            var managed=await ManagedPublication.IsEnabled(db,ct);
            var path=Path.Combine(cacheDirectory,managed?"managed.xml":"raw.xml");
            // Include membership and EPG mapping, not only the selected playback source.
            // Hash the relevant catalog metadata to detect changes without relying on UI hooks.
            var map=await BuildMapAsync(db,managed,ct);
            using var hash=System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
            void Add(string value)
            {
                var bytes=Encoding.UTF8.GetBytes(value);
                hash.AppendData(BitConverter.GetBytes(bytes.Length));hash.AppendData(bytes);
            }
            Add(managed.ToString());var currentGeneration=Interlocked.Read(ref generation);
            foreach(var item in map)
            {
                Add(item.Id);Add(item.Name);
                foreach(var candidate in item.Candidates){Add(candidate.ProviderId.ToString(CultureInfo.InvariantCulture));Add(candidate.EpgId);}
            }
            var signature=Convert.ToHexString(hash.GetHashAndReset());
            if(signature!=cachedSignature || currentGeneration!=cachedGeneration || DateTimeOffset.UtcNow-cachedAt>TimeSpan.FromMinutes(30) || !File.Exists(path))
            {
                temporary=Path.Combine(cacheDirectory,Guid.NewGuid().ToString("N")+".tmp");
                try
                {
                    await using(var output=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None,65536,FileOptions.Asynchronous))
                    {
                        await GenerateAsync(db,map,output,ct);
                        await output.FlushAsync(ct);
                    }
                    File.Move(temporary,path,true);temporary=null;
                    cachedSignature=signature;cachedGeneration=currentGeneration;cachedAt=DateTimeOffset.UtcNow;
                    log.LogInformation("XMLTV cache generated: {Channels} channels, {Bytes} bytes",map.Count,new FileInfo(path).Length);
                }
                catch(OperationCanceledException) when(ct.IsCancellationRequested){throw;}
                catch(Exception ex)
                {
                    log.LogWarning("XMLTV generation failed: {ErrorType}",ex.GetType().Name);
                    // Never serve a previous catalog with different membership or mode.
                    if(cachedSignature!=signature || !File.Exists(path))return null;
                }
            }
            return new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read|FileShare.Delete,65536,FileOptions.Asynchronous|FileOptions.SequentialScan);
        }
        catch(OperationCanceledException) when(ct.IsCancellationRequested){throw;}
        catch(Exception ex){log.LogWarning("XMLTV cache unavailable: {ErrorType}",ex.GetType().Name);return null;}
        finally
        {
            if(temporary is not null){try{File.Delete(temporary);}catch(IOException){}catch(UnauthorizedAccessException){}}
            publicationGate.Release();
        }
    }
    public async Task<(int Covered,int Total)> CoverageAsync(int groupId,CancellationToken ct)
    {
        await using var db=await factory.CreateDbContextAsync(ct);
        var published=await ManagedPublication.ListAsync(db,groupId,ct,false);
        var sources=(await ManagedPublication.Candidates(db).ToListAsync(ct)).ToLookup(x=>x.ChannelId);
        await db.Database.OpenConnectionAsync(ct);
        var connection=(SqliteConnection)db.Database.GetDbConnection();
        await using var cmd=connection.CreateCommand();
        cmd.CommandText="SELECT DISTINCT ProviderId,EpgId FROM EpgPrograms WHERE Stop>$now";
        cmd.Parameters.AddWithValue("$now",DateTimeOffset.UtcNow.ToString("O"));
        var available=new HashSet<(int,string)>();
        await using(var reader=await cmd.ExecuteReaderAsync(ct))while(await reader.ReadAsync(ct))available.Add((reader.GetInt32(0),reader.GetString(1)));
        return (published.Count(x=>sources[x.ChannelId].Any(s=>available.Contains((s.ProviderId,s.EpgId)))),published.Count);
    }
    private static async Task<List<(string Id,string Name,ManagedPublication.Candidate[] Candidates)>> BuildMapAsync(CatalogDb db,bool managed,CancellationToken ct)
    {
        var sources=await ManagedPublication.Candidates(db).ToListAsync(ct);
        var map=new List<(string Id,string Name,ManagedPublication.Candidate[] Candidates)>();
        if(managed)
        {
            var published=await ManagedPublication.ListAsync(db,null,ct,false);
            var lookup=sources.ToLookup(s=>s.ChannelId);
            foreach(var row in published.DistinctBy(x=>x.ChannelId))
                map.Add(("iptvproxy."+row.ChannelId,row.Name,lookup[row.ChannelId].Where(s=>s.EpgId!="").OrderBy(s=>s.Priority).ThenBy(s=>s.ProviderId).ToArray()));
        }
        else
        {
            foreach(var s in sources.Where(s=>s.EpgId!=""))map.Add(("iptvproxy.raw."+s.StreamId,s.Name,[s]));
        }
        return map;
    }
    private async Task GenerateAsync(CatalogDb db,List<(string Id,string Name,ManagedPublication.Candidate[] Candidates)> map,Stream output,CancellationToken ct)
    {
        await db.Database.OpenConnectionAsync(ct);
        var connection=(SqliteConnection)db.Database.GetDbConnection();
        await using var command=connection.CreateCommand();
        command.CommandText="SELECT Start,Stop,Title,Description,Artwork FROM EpgPrograms WHERE ProviderId=$p AND EpgId=$id AND Stop>$now ORDER BY Start";
        command.Parameters.AddWithValue("$p",0);command.Parameters.AddWithValue("$id","");command.Parameters.AddWithValue("$now",DateTimeOffset.UtcNow.AddHours(-6).ToString("O"));
        using var writer=XmlWriter.Create(output,new XmlWriterSettings{Async=true,Encoding=new UTF8Encoding(false),CloseOutput=false});
        await writer.WriteStartDocumentAsync();await writer.WriteStartElementAsync(null,"tv",null);
        foreach(var item in map)
        {
            await writer.WriteStartElementAsync(null,"channel",null);await writer.WriteAttributeStringAsync(null,"id",null,item.Id);await writer.WriteElementStringAsync(null,"display-name",null,item.Name);
            var raw=item.Id.StartsWith("iptvproxy.raw.",StringComparison.Ordinal);
            if(int.TryParse(item.Id.AsSpan(raw?14:10),out var id)){await writer.WriteStartElementAsync(null,"icon",null);await writer.WriteAttributeStringAsync(null,"src",null,artwork.ChannelUrl(id,raw));await writer.WriteEndElementAsync();}
            await writer.WriteEndElementAsync();
        }
        foreach(var item in map)
        {
            ct.ThrowIfCancellationRequested();
            foreach(var candidate in item.Candidates.DistinctBy(s=>(s.ProviderId,s.EpgId)))
            {
                command.Parameters["$p"].Value=candidate.ProviderId;command.Parameters["$id"].Value=candidate.EpgId;
                await using var rows=await command.ExecuteReaderAsync(ct);bool found=false;
                while(await rows.ReadAsync(ct))
                {
                    found=true;await writer.WriteStartElementAsync(null,"programme",null);
                    await writer.WriteAttributeStringAsync(null,"channel",null,item.Id);
                    await writer.WriteAttributeStringAsync(null,"start",null,DateTimeOffset.Parse(rows.GetString(0),CultureInfo.InvariantCulture).ToUniversalTime().ToString("yyyyMMddHHmmss '+0000'"));
                    await writer.WriteAttributeStringAsync(null,"stop",null,DateTimeOffset.Parse(rows.GetString(1),CultureInfo.InvariantCulture).ToUniversalTime().ToString("yyyyMMddHHmmss '+0000'"));
                    await writer.WriteElementStringAsync(null,"title",null,rows.GetString(2));
                    await writer.WriteElementStringAsync(null,"desc",null,rows.GetString(3));
                    var image=artwork.ProgrammeUrl(rows.GetString(4));if(image!=""){await writer.WriteStartElementAsync(null,"icon",null);await writer.WriteAttributeStringAsync(null,"src",null,image);await writer.WriteEndElementAsync();}
                    await writer.WriteEndElementAsync();
                }
                if(found)break;
            }
        }
        await writer.WriteEndElementAsync();await writer.WriteEndDocumentAsync();await writer.FlushAsync();
    }
}
public sealed class XmlTvRefresh(XmlTvGuide guide,IConfiguration config):BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var hours=config.GetValue("Epg:RefreshHours",6);if(hours<=0)return;
        await guide.RefreshAsync(ct);
        using var timer=new PeriodicTimer(TimeSpan.FromHours(Math.Clamp(hours,1,168)));
        while(await timer.WaitForNextTickAsync(ct))await guide.RefreshAsync(ct);
    }
}
