using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using IPTVProxy.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace IPTVProxy.Providers;
public sealed class XmlTvGuide(IDbContextFactory<CatalogDb> factory,LiveCatalog catalog,IHttpClientFactory clients,ILogger<XmlTvGuide> log)
{
    private readonly SemaphoreSlim gate=new(1,1);
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
        using var response=await client.GetAsync(uri,HttpCompletionOption.ResponseHeadersRead,token);
        response.EnsureSuccessStatusCode();
        await using var body=await response.Content.ReadAsStreamAsync(token);
        // Bound compressed and uncompressed input; no DTD/network entity resolution.
        using var input=new MemoryStream();
        var buffer=new byte[65536];int n;
        while((n=await body.ReadAsync(buffer,token))>0){if(input.Length+n>128L*1024*1024)throw new InvalidDataException("XMLTV input too large");input.Write(buffer,0,n);}
        input.Position=0;
        using var decoded=input.Length>=2&&input.GetBuffer()[0]==0x1f&&input.GetBuffer()[1]==0x8b?(Stream)new GZipStream(input,CompressionMode.Decompress):input;
        using var reader=XmlReader.Create(decoded,new XmlReaderSettings{DtdProcessing=DtdProcessing.Ignore,XmlResolver=null,MaxCharactersInDocument=256L*1024*1024});
        var entries=new List<(string Id,string Start,string Stop,string Title,string Description)>();
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
                if(id.Length is >0 and <=512&&start is not null&&stop is not null&&stop>start&&stop>now.AddDays(-1)&&start<now.AddDays(8)&&title.Length>0)
                {
                    entries.Add((id,start.Value.ToString("O"),stop.Value.ToString("O"),title[..Math.Min(title.Length,2048)],desc[..Math.Min(desc.Length,16384)]));
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
        cmd.CommandText="INSERT OR REPLACE INTO EpgPrograms VALUES($p,$id,$start,$stop,$title,$desc)";
        foreach(var key in new[]{"$id","$start","$stop","$title","$desc"})cmd.Parameters.AddWithValue(key,"");
        foreach(var e in entries)
        {
            token.ThrowIfCancellationRequested();cmd.Parameters["$id"].Value=e.Id;cmd.Parameters["$start"].Value=e.Start;cmd.Parameters["$stop"].Value=e.Stop;cmd.Parameters["$title"].Value=e.Title;cmd.Parameters["$desc"].Value=e.Description;
            await cmd.ExecuteNonQueryAsync(token);
        }
        cmd.Parameters.Clear();cmd.CommandText="INSERT OR REPLACE INTO EpgRefreshState VALUES($p,$updated,$count)";
        cmd.Parameters.AddWithValue("$p",p.Id);cmd.Parameters.AddWithValue("$updated",now.ToString("O"));cmd.Parameters.AddWithValue("$count",entries.Count);await cmd.ExecuteNonQueryAsync(token);
        await tx.CommitAsync(token);
        log.LogInformation("XMLTV imported for provider {ProviderId}: {Count} programmes",p.Id,entries.Count);
    }
    public static DateTimeOffset? Time(string? value)
    {
        if(string.IsNullOrWhiteSpace(value))return null;
        var s=value.Trim();
        if(s.Length==20&&(s[15]=='+'||s[15]=='-'))s=s.Insert(18,":");
        return DateTimeOffset.TryParseExact(s,new[]{"yyyyMMddHHmmss zzz","yyyyMMddHHmmss"},CultureInfo.InvariantCulture,DateTimeStyles.AssumeUniversal,out var dt)?dt.ToUniversalTime():null;
    }
    public async Task WriteAsync(HttpContext ctx)
    {
        var ct=ctx.RequestAborted;
        await using var db=await factory.CreateDbContextAsync(ct);
        var managed=await ManagedPublication.IsEnabled(db,ct);
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
        await db.Database.OpenConnectionAsync(ct);
        var connection=(SqliteConnection)db.Database.GetDbConnection();
        await using var command=connection.CreateCommand();
        command.CommandText="SELECT Start,Stop,Title,Description FROM EpgPrograms WHERE ProviderId=$p AND EpgId=$id AND Stop>$now ORDER BY Start";
        command.Parameters.AddWithValue("$p",0);command.Parameters.AddWithValue("$id","");command.Parameters.AddWithValue("$now",DateTimeOffset.UtcNow.AddHours(-6).ToString("O"));
        ctx.Response.ContentType="application/xml; charset=utf-8";
        using var writer=XmlWriter.Create(ctx.Response.Body,new XmlWriterSettings{Async=true,Encoding=new UTF8Encoding(false),CloseOutput=false});
        await writer.WriteStartDocumentAsync();await writer.WriteStartElementAsync(null,"tv",null);
        foreach(var item in map)
        {
            await writer.WriteStartElementAsync(null,"channel",null);await writer.WriteAttributeStringAsync(null,"id",null,item.Id);await writer.WriteElementStringAsync(null,"display-name",null,item.Name);await writer.WriteEndElementAsync();
        }
        foreach(var item in map)
        {
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
                    await writer.WriteElementStringAsync(null,"desc",null,rows.GetString(3));await writer.WriteEndElementAsync();
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
