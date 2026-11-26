using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using IPTVProxy.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace IPTVProxy.Providers;

public sealed class VodCatalog(IDbContextFactory<CatalogDb> factory,LiveCatalog live,IHttpClientFactory clients,ArtworkService artwork)
{
    private readonly SemaphoreSlim imports=new(1,1), details=new(1,1);
    public sealed record Category(int Id,string Kind,string Provider,string Name,bool Selected,long Count);
    public sealed record Media(Provider Provider,long SourceId,string Extension);
    private static string Str(JsonObject obj,string key)=>obj[key] is JsonValue v?v.ToString():"";
    private static long Source(JsonObject obj,string key)=>long.TryParse(Str(obj,key),out var id)&&id>0?id:throw new InvalidDataException("Invalid source ID.");
    private static string Ext(string s)=>s.ToLowerInvariant() is "mp4" or "mkv" or "avi" or "mov" or "ts" or "webm" or "m4v" or "mpg" or "mpeg" or "flv" or "wmv" ? s.ToLowerInvariant():"mp4";
    private static SqliteCommand Cmd(SqliteConnection c,SqliteTransaction? tx,string sql,params object[] args)
    {
        var cmd=c.CreateCommand();cmd.Transaction=tx;cmd.CommandText=sql;
        for(int i=0;i<args.Length;i++)cmd.Parameters.AddWithValue("$"+i,args[i]);return cmd;
    }
    private static async Task Exec(SqliteConnection c,SqliteTransaction? tx,string sql,CancellationToken ct,params object[] args)
    {await using var cmd=Cmd(c,tx,sql,args);await cmd.ExecuteNonQueryAsync(ct);}
    private static async Task<long> Scalar(SqliteConnection c,SqliteTransaction? tx,string sql,CancellationToken ct,params object[] args)
    {await using var cmd=Cmd(c,tx,sql,args);return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct),CultureInfo.InvariantCulture);}
    private async Task<JsonNode> Fetch(Provider p,string action,CancellationToken ct)
    {
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct);timeout.CancelAfter(TimeSpan.FromMinutes(3));
        using var client=clients.CreateClient("provider");
        var uri=live.ApiUri(p,action);
        for(int hop=0;;hop++)
        {
            using var response=await client.GetAsync(uri,HttpCompletionOption.ResponseHeadersRead,timeout.Token);
            if((int)response.StatusCode is 301 or 302 or 303 or 307 or 308)
            {
                if(hop>=5||response.Headers.Location is null||!Uri.TryCreate(uri,response.Headers.Location,out var next)||next.Scheme is not ("http" or "https")||next.UserInfo.Length!=0||next.IsLoopback||(uri.Scheme=="https"&&next.Scheme=="http"))throw new InvalidDataException("Invalid redirect.");
                uri=next;continue;
            }
            response.EnsureSuccessStatusCode();
            // Bound response size, even when the provider uses chunked transfer.
            await response.Content.LoadIntoBufferAsync(128L*1024*1024,timeout.Token);
            await using var body=await response.Content.ReadAsStreamAsync(timeout.Token);
            return await JsonNode.ParseAsync(body,cancellationToken:timeout.Token)??throw new InvalidDataException("Empty response.");
        }
    }
    // Preserve only known descriptive fields; never publish upstream playback URLs.
    private static readonly string[] Fields=["name","title","original_name","year","release_date","releaseDate","releasedate","added","last_modified","rating","rating_5based","plot","description","cast","director","genre","duration","duration_secs","episode_run_time","youtube_trailer","tmdb_id","tmdb","imdb_id","country","season","episode_num","air_date","stream_icon","cover","cover_big","movie_image","backdrop_path","container_extension","num","stream_type"];
    private static readonly HashSet<string> Images=new(["stream_icon","cover","cover_big","movie_image","backdrop_path"]);
    private static JsonObject Metadata(JsonObject src)
    {
        var dst=new JsonObject();
        foreach(var key in Fields)
        {
            if(src[key] is JsonValue v)dst[key]=v.DeepClone();
            else if(key=="backdrop_path"&&src[key] is JsonArray a)dst[key]=new JsonArray(a.OfType<JsonValue>().Select(v=>v.DeepClone()).ToArray());
        }
        return dst;
    }
    private JsonObject PublicMetadata(JsonObject src)
    {
        var dst=Metadata(src);
        foreach(var key in Images)
        {
            if(dst[key] is JsonArray a)dst[key]=new JsonArray(a.Select(v=>(JsonNode?)JsonValue.Create(artwork.ProgrammeUrl(v?.ToString()??""))).ToArray());
            else if(dst[key] is JsonValue v)dst[key]=artwork.ProgrammeUrl(v.ToString());
        }
        return dst;
    }
    public async Task<int> ImportAsync(int providerId,string kind,CancellationToken ct)
    {
        if(kind is not ("movie" or "series"))throw new ArgumentException("Invalid content type.");
        await imports.WaitAsync(ct);
        try
        {
            await using var db=await factory.CreateDbContextAsync(ct);
            var p=await db.Providers.AsNoTracking().SingleAsync(p=>p.Id==providerId&&p.Enabled,ct);
            var prefix=kind=="movie"?"vod":"series";
            var categories=await Fetch(p,"get_"+prefix+"_categories",ct) as JsonArray??throw new InvalidDataException("Invalid categories.");
            var rows=await Fetch(p,kind=="movie"?"get_vod_streams":"get_series",ct) as JsonArray??throw new InvalidDataException("Invalid catalog.");
            var cats=new Dictionary<string,string>();
            foreach(var node in categories)
            {
                if(node is not JsonObject c||!cats.TryAdd(Str(c,"category_id"),Str(c,"category_name"))||Str(c,"category_id")==""||Str(c,"category_name")=="")throw new InvalidDataException("Invalid category.");
            }
            var ids=new HashSet<long>();
            foreach(var node in rows)
            {
                if(node is not JsonObject item||!ids.Add(Source(item,kind=="movie"?"stream_id":"series_id"))||Str(item,"name")=="")throw new InvalidDataException("Invalid item.");
                var cat=Str(item,"category_id");cats.TryAdd(cat,cat==""?"Uncategorized":"Category "+cat);
            }
            await db.Database.OpenConnectionAsync(ct);
            var conn=(SqliteConnection)db.Database.GetDbConnection();
            using var tx=conn.BeginTransaction();
            var old=await Scalar(conn,tx,"SELECT count(*) FROM VodItems WHERE ProviderId=$0 AND Kind=$1 AND Active=1",ct,providerId,kind);
            if(rows.Count<old*0.8)throw new InvalidDataException("Suspicious catalog shrinkage.");
            await Exec(conn,tx,"UPDATE VodItems SET Active=0 WHERE ProviderId=$0 AND Kind=$1",ct,providerId,kind);
            await Exec(conn,tx,"UPDATE VodCategories SET Active=0 WHERE ProviderId=$0 AND Kind=$1",ct,providerId,kind);
            var catIds=new Dictionary<string,long>();int order=0;
            foreach(var c in cats)
            {
                await Exec(conn,tx,"INSERT INTO VodCategories(ProviderId,Kind,SourceId,Name,SortOrder) VALUES($0,$1,$2,$3,$4) ON CONFLICT(ProviderId,Kind,SourceId) DO UPDATE SET Name=excluded.Name,Active=1,SortOrder=excluded.SortOrder",ct,providerId,kind,c.Key,c.Value,order++);
                catIds[c.Key]=await Scalar(conn,tx,"SELECT Id FROM VodCategories WHERE ProviderId=$0 AND Kind=$1 AND SourceId=$2",ct,providerId,kind,c.Key);
            }
            order=0;
            await using var upsert=Cmd(conn,tx,"INSERT INTO VodItems(ProviderId,Kind,SourceId,CategoryId,Name,Extension,Metadata,SortOrder) VALUES($0,$1,$2,$3,$4,$5,$6,$7) ON CONFLICT(ProviderId,Kind,SourceId) DO UPDATE SET CategoryId=excluded.CategoryId,Name=excluded.Name,Extension=excluded.Extension,Metadata=excluded.Metadata,Active=1,SortOrder=excluded.SortOrder",providerId,kind,0L,0L,"","","",0);
            upsert.Prepare();
            foreach(JsonObject item in rows)
            {
                upsert.Parameters[2].Value=Source(item,kind=="movie"?"stream_id":"series_id");upsert.Parameters[3].Value=catIds[Str(item,"category_id")];upsert.Parameters[4].Value=Str(item,"name");upsert.Parameters[5].Value=Ext(Str(item,"container_extension"));upsert.Parameters[6].Value=Metadata(item).ToJsonString();upsert.Parameters[7].Value=order++;
                await upsert.ExecuteNonQueryAsync(ct);
            }
            await tx.CommitAsync(ct);return rows.Count;
        }
        finally{imports.Release();}
    }
    public async Task<List<Category>> Categories(CancellationToken ct)
    {
        await using var db=await factory.CreateDbContextAsync(ct);await db.Database.OpenConnectionAsync(ct);
        await using var cmd=Cmd((SqliteConnection)db.Database.GetDbConnection(),null,"SELECT c.Id,c.Kind,p.Name,c.Name,c.Selected,(SELECT count(*) FROM VodItems i WHERE i.CategoryId=c.Id AND i.Active=1) FROM VodCategories c JOIN Providers p ON p.Id=c.ProviderId WHERE c.Active=1 AND p.Enabled=1 ORDER BY p.Priority,p.Id,c.SortOrder");
        await using var reader=await cmd.ExecuteReaderAsync(ct);var result=new List<Category>();
        while(await reader.ReadAsync(ct))result.Add(new(reader.GetInt32(0),reader.GetString(1),reader.GetString(2),reader.GetString(3),reader.GetBoolean(4),reader.GetInt64(5)));return result;
    }
    public async Task Select(int id,bool selected,CancellationToken ct)
    {
        await using var db=await factory.CreateDbContextAsync(ct);await db.Database.OpenConnectionAsync(ct);
        await Exec((SqliteConnection)db.Database.GetDbConnection(),null,"UPDATE VodCategories SET Selected=$0 WHERE Id=$1",ct,selected,id);
    }
    private const string Visible=" i.Active=1 AND c.Active=1 AND c.Selected=1 AND p.Enabled=1 ";
    public async Task<JsonArray> List(string kind,int? category,CancellationToken ct)
    {
        await using var db=await factory.CreateDbContextAsync(ct);await db.Database.OpenConnectionAsync(ct);
        await using var cmd=Cmd((SqliteConnection)db.Database.GetDbConnection(),null,"SELECT i.Id,i.CategoryId,i.Name,i.Extension,i.Metadata FROM VodItems i JOIN VodCategories c ON c.Id=i.CategoryId JOIN Providers p ON p.Id=i.ProviderId WHERE "+Visible+" AND i.Kind=$0 AND ($1=0 OR c.Id=$1) ORDER BY p.Priority,p.Id,c.SortOrder,i.SortOrder",kind,category??0);
        await using var reader=await cmd.ExecuteReaderAsync(ct);var result=new JsonArray();
        while(await reader.ReadAsync(ct))
        {
            var obj=PublicMetadata(JsonNode.Parse(reader.GetString(4))!.AsObject());
            obj[kind=="movie"?"stream_id":"series_id"]=reader.GetInt32(0);obj["category_id"]=reader.GetInt32(1).ToString();obj["name"]=reader.GetString(2);obj["num"]=result.Count+1;
            if(kind=="movie"){obj["container_extension"]=reader.GetString(3);obj["stream_type"]="movie";obj["direct_source"]="";}
            result.Add(obj);
        }
        return result;
    }
    private async Task<(int Provider,long Source,string Extension,string Metadata)?> Find(int id,string kind,CancellationToken ct)
    {
        await using var db=await factory.CreateDbContextAsync(ct);await db.Database.OpenConnectionAsync(ct);
        await using var cmd=Cmd((SqliteConnection)db.Database.GetDbConnection(),null,"SELECT i.ProviderId,i.SourceId,i.Extension,i.Metadata FROM VodItems i JOIN VodCategories c ON c.Id=i.CategoryId JOIN Providers p ON p.Id=i.ProviderId WHERE "+Visible+" AND i.Id=$0 AND i.Kind=$1",id,kind);
        await using var r=await cmd.ExecuteReaderAsync(ct);return await r.ReadAsync(ct)?(r.GetInt32(0),r.GetInt64(1),r.GetString(2),r.GetString(3)):null;
    }
    public async Task<Media?> Resolve(int id,string kind,CancellationToken ct)
    {
        await using var db=await factory.CreateDbContextAsync(ct);
        if(kind=="movie")
        {
            var row=await Find(id,kind,ct);if(row is null)return null;var x=row.Value;
            var p=await db.Providers.AsNoTracking().SingleOrDefaultAsync(p=>p.Id==x.Provider&&p.Enabled,ct);return p is null?null:new(p,x.Source,x.Extension);
        }
        await db.Database.OpenConnectionAsync(ct);
        int provider;long source;string ext;
        await using(var cmd=Cmd((SqliteConnection)db.Database.GetDbConnection(),null,"SELECT i.ProviderId,e.SourceId,e.Extension FROM VodEpisodes e JOIN VodItems i ON i.Id=e.ItemId JOIN VodCategories c ON c.Id=i.CategoryId JOIN Providers p ON p.Id=i.ProviderId WHERE "+Visible+" AND e.Active=1 AND e.Id=$0",id))
        await using(var r=await cmd.ExecuteReaderAsync(ct)){if(!await r.ReadAsync(ct))return null;provider=r.GetInt32(0);source=r.GetInt64(1);ext=r.GetString(2);}
        var pr=await db.Providers.AsNoTracking().SingleOrDefaultAsync(p=>p.Id==provider&&p.Enabled,ct);return pr is null?null:new(pr,source,ext);
    }
    public async Task<JsonObject?> Detail(int id,string kind,CancellationToken ct)
    {
        await details.WaitAsync(ct);
        try
        {
            var found=await Find(id,kind,ct);if(found is null)return null;var item=found.Value;
            await using var db=await factory.CreateDbContextAsync(ct);
            var provider=await db.Providers.AsNoTracking().SingleAsync(p=>p.Id==item.Provider&&p.Enabled,ct);
            await db.Database.OpenConnectionAsync(ct);var conn=(SqliteConnection)db.Database.GetDbConnection();
            string? cached=null;long fetched=0;
            await using(var cmd=Cmd(conn,null,"SELECT Metadata,Fetched FROM VodDetails WHERE ItemId=$0",id))
            await using(var r=await cmd.ExecuteReaderAsync(ct)){if(await r.ReadAsync(ct)){cached=r.GetString(0);fetched=r.GetInt64(1);}}
            if(cached is null||DateTimeOffset.UtcNow.ToUnixTimeSeconds()-fetched>86400)
            {
                try
                {
                    var raw=await Fetch(provider,(kind=="movie"?"get_vod_info&vod_id=":"get_series_info&series_id=")+item.Source,ct) as JsonObject??throw new InvalidDataException("Invalid details.");
                    if(raw["info"] is not JsonObject info)throw new InvalidDataException("Missing info.");
                    var clean=new JsonObject{["info"]=Metadata(info)};
                    using var tx=conn.BeginTransaction();
                    if(kind=="movie")
                    {
                        var movie=raw["movie_data"] as JsonObject??new JsonObject();
                        var m=Metadata(movie);m["stream_id"]=id;m["container_extension"]=item.Extension;m["direct_source"]="";clean["movie_data"]=m;
                    }
                    else
                    {
                        if(raw["episodes"] is not JsonObject episodes)throw new InvalidDataException("Missing episodes.");
                        await Exec(conn,tx,"UPDATE VodEpisodes SET Active=0 WHERE ItemId=$0",ct,id);
                        var all=new JsonObject();var seen=new HashSet<long>();
                        foreach(var season in episodes)
                        {
                            if(season.Value is not JsonArray list)throw new InvalidDataException("Invalid season.");
                            var output=new JsonArray();
                            foreach(var node in list)
                            {
                                if(node is not JsonObject ep)throw new InvalidDataException("Invalid episode.");
                                var source=Source(ep,"id");if(!seen.Add(source))throw new InvalidDataException("Duplicate episode.");
                                var extension=Ext(Str(ep,"container_extension"));
                                await Exec(conn,tx,"INSERT INTO VodEpisodes(ItemId,SourceId,Extension) VALUES($0,$1,$2) ON CONFLICT(ItemId,SourceId) DO UPDATE SET Extension=excluded.Extension,Active=1",ct,id,source,extension);
                                var eid=await Scalar(conn,tx,"SELECT Id FROM VodEpisodes WHERE ItemId=$0 AND SourceId=$1",ct,id,source);
                                var e=Metadata(ep);e["id"]=eid.ToString();e["container_extension"]=extension;e["direct_source"]="";e["info"]=Metadata(ep["info"] as JsonObject??new JsonObject());output.Add(e);
                            }
                            all[season.Key]=output;
                        }
                        clean["episodes"]=all;
                        clean["seasons"]=new JsonArray((raw["seasons"] as JsonArray??new JsonArray()).OfType<JsonObject>().Select(s=>(JsonNode?)Metadata(s)).ToArray());
                    }
                    cached=clean.ToJsonString();
                    await Exec(conn,tx,"INSERT INTO VodDetails(ItemId,Metadata,Fetched) VALUES($0,$1,$2) ON CONFLICT(ItemId) DO UPDATE SET Metadata=excluded.Metadata,Fetched=excluded.Fetched",ct,id,cached,DateTimeOffset.UtcNow.ToUnixTimeSeconds());await tx.CommitAsync(ct);
                }
                catch(Exception) when(cached is not null&&!ct.IsCancellationRequested){}
            }
            var result=JsonNode.Parse(cached!)!.AsObject();
            result["info"]=PublicMetadata(result["info"]!.AsObject());
            if(result["movie_data"] is JsonObject movieData){var m=PublicMetadata(movieData);m["stream_id"]=id;m["direct_source"]="";result["movie_data"]=m;}
            if(result["seasons"] is JsonArray seasons)result["seasons"]=new JsonArray(seasons.OfType<JsonObject>().Select(s=>(JsonNode?)PublicMetadata(s)).ToArray());
            if(result["episodes"] is JsonObject groups)
                foreach(var group in groups.ToList())if(group.Value is JsonArray list)foreach(var node in list)if(node is JsonObject ep){foreach(var key in Images)if(ep[key] is JsonValue v)ep[key]=artwork.ProgrammeUrl(v.ToString());ep["info"]=PublicMetadata(ep["info"]!.AsObject());}
            return result;
        }
        finally{details.Release();}
    }
}
