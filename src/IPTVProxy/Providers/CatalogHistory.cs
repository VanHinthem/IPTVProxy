using IPTVProxy.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
namespace IPTVProxy.Providers;

public sealed class CatalogHistory(IDbContextFactory<CatalogDb> factory,IConfiguration config)
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<(int,string),long> running=new();
    public long? RunningSince(int provider,string kind)=>running.TryGetValue((provider,kind),out var started)?started:null;
    public bool TryBegin(int provider,string kind)=>running.TryAdd((provider,kind),Now());
    public void End(int provider,string kind)=>running.TryRemove((provider,kind),out _);
    public sealed record State(int ProviderId,string Provider,string Kind,int Hours,long? LastSuccess,long? LastAttempt,bool? Success,int Count,int Categories,int Added,int Removed,string Error);
    public sealed record Run(long Id,string Provider,string Kind,long Started,long Finished,bool Success,int Count,int Categories,int Added,int Removed,string Error);
    public sealed record Change(string Provider,string Kind,string Entity,string Name,string Category,string Action,long Observed);
    public sealed record Entry(string Entity,long Id,string Name,string Category,bool Active);
    public static long Now()=>DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    private static SqliteCommand Cmd(SqliteConnection c,SqliteTransaction? tx,string sql,params object[] args)
    {var cmd=c.CreateCommand();cmd.Transaction=tx;cmd.CommandText=sql;for(int i=0;i<args.Length;i++)cmd.Parameters.AddWithValue("$"+i,args[i]);return cmd;}
    private static async Task Exec(SqliteConnection c,SqliteTransaction? tx,string sql,CancellationToken ct,params object[] args)
    {await using var cmd=Cmd(c,tx,sql,args);await cmd.ExecuteNonQueryAsync(ct);}
    public static async Task<Dictionary<(string,long),Entry>> Snapshot(SqliteConnection c,SqliteTransaction? tx,int provider,string kind,CancellationToken ct)
    {
        // Include inactive rows so returning content is not incorrectly called new.
        var sql=kind=="live"?"""
            SELECT 'category',c.Id,c.Name,'',c.Active FROM LiveCategories c WHERE c.ProviderId=$0
            UNION ALL SELECT 'channel',s.Id,s.Name,COALESCE(c.Name,''),s.Active FROM LiveStreams s LEFT JOIN LiveCategories c ON c.ProviderId=s.ProviderId AND c.SourceId=s.CategoryId WHERE s.ProviderId=$0
            """:"""
            SELECT 'category',c.Id,c.Name,'',c.Active FROM VodCategories c WHERE c.ProviderId=$0 AND c.Kind=$1
            UNION ALL SELECT 'title',i.Id,i.Name,COALESCE(c.Name,''),i.Active FROM VodItems i LEFT JOIN VodCategories c ON c.Id=i.CategoryId WHERE i.ProviderId=$0 AND i.Kind=$1
            """;
        await using var cmd=kind=="live"?Cmd(c,tx,sql,provider):Cmd(c,tx,sql,provider,kind);
        await using var r=await cmd.ExecuteReaderAsync(ct);var result=new Dictionary<(string,long),Entry>();
        while(await r.ReadAsync(ct)){var e=new Entry(r.GetString(0),r.GetInt64(1),r.GetString(2),r.GetString(3),r.GetBoolean(4));result.Add((e.Entity,e.Id),e);}
        return result;
    }
    public static async Task<HashSet<int>> LogicalActive(SqliteConnection c,SqliteTransaction? tx,CancellationToken ct)
    {
        await using var cmd=Cmd(c,tx,"SELECT DISTINCT cs.LogicalChannelId FROM ChannelSources cs JOIN LiveStreams s ON s.Id=cs.LiveStreamId JOIN Providers p ON p.Id=s.ProviderId WHERE s.Active=1 AND p.Enabled=1 AND cs.Separator=0");
        await using var r=await cmd.ExecuteReaderAsync(ct);var result=new HashSet<int>();while(await r.ReadAsync(ct))result.Add(r.GetInt32(0));return result;
    }
    public static IEnumerable<(Entry Entry,string Action)> Diff(Dictionary<(string,long),Entry> before,Dictionary<(string,long),Entry> after)
    {
        foreach(var (key,e) in after)
        {
            if(!before.TryGetValue(key,out var old)){if(e.Active)yield return(e,"added");}
            else if(e.Active&&!old.Active)yield return(e,"returned");
            else if(!e.Active&&old.Active)yield return(e,"removed");
        }
        foreach(var (key,e) in before)if(e.Active&&!after.ContainsKey(key))yield return(e,"removed");
    }
    public static async Task RecordSuccess(SqliteConnection c,SqliteTransaction tx,int provider,string kind,long started,Dictionary<(string,long),Entry> before,HashSet<int>? logicalBefore,CancellationToken ct)
    {
        var after=await Snapshot(c,tx,provider,kind,ct);var delta=Diff(before,after).ToList();
        if(logicalBefore is not null)
        {
            var logicalAfter=await LogicalActive(c,tx,ct);
            await using var names=Cmd(c,tx,"SELECT Id,Name FROM LogicalChannels");
            await using var r=await names.ExecuteReaderAsync(ct);
            while(await r.ReadAsync(ct)){var id=r.GetInt32(0);if(logicalAfter.Contains(id)&&!logicalBefore.Contains(id))delta.Add((new Entry("logical",id,r.GetString(1),"",true),"available"));else if(logicalBefore.Contains(id)&&!logicalAfter.Contains(id))delta.Add((new Entry("logical",id,r.GetString(1),"",false),"removed"));}
        }
        var now=Now();var added=delta.Count(x=>x.Action=="added");var removed=delta.Count(x=>x.Action=="removed"&&x.Entry.Entity!="logical");
        await Exec(c,tx,"INSERT INTO CatalogImportRuns(ProviderId,Kind,Started,Finished,Success,ItemCount,CategoryCount,Added,Removed) VALUES($0,$1,$2,$3,1,$4,$5,$6,$7)",ct,provider,kind,started,now,after.Values.Count(x=>x.Active&&x.Entity!="category"),after.Values.Count(x=>x.Active&&x.Entity=="category"),added,removed);
        await using var idCmd=Cmd(c,tx,"SELECT last_insert_rowid()");var run=Convert.ToInt64(await idCmd.ExecuteScalarAsync(ct));
        await using var insert=Cmd(c,tx,"INSERT INTO CatalogChanges(RunId,Entity,SourceId,Name,Category,Change,Observed) VALUES($0,$1,$2,$3,$4,$5,$6)",run,"",0L,"","","",now);insert.Prepare();
        foreach(var (e,action) in delta)
        {insert.Parameters[1].Value=e.Entity;insert.Parameters[2].Value=e.Id;insert.Parameters[3].Value=e.Name;insert.Parameters[4].Value=e.Category;insert.Parameters[5].Value=action;await insert.ExecuteNonQueryAsync(ct);}
    }
    public async Task RecordFailure(int provider,string kind,long started,string error,CancellationToken ct)
    {
        await using var db=await factory.CreateDbContextAsync(ct);await db.Database.OpenConnectionAsync(ct);var c=(SqliteConnection)db.Database.GetDbConnection();
        await Exec(c,null,"INSERT INTO CatalogImportRuns(ProviderId,Kind,Started,Finished,Success,ItemCount,CategoryCount,Error) VALUES($0,$1,$2,$3,0,0,0,$4)",ct,provider,kind,started,Now(),error);
    }
    public async Task<List<State>> States(CancellationToken ct)
    {
        await using var db=await factory.CreateDbContextAsync(ct);await db.Database.OpenConnectionAsync(ct);var c=(SqliteConnection)db.Database.GetDbConnection();
        await using var cmd=Cmd(c,null,"""
            WITH kinds(Kind) AS (VALUES('live'),('movie'),('series'))
            SELECT p.Id,p.Name,k.Kind,COALESCE(s.Hours,CASE WHEN k.Kind='live' THEN $0 ELSE $1 END),
            (SELECT MAX(Finished) FROM CatalogImportRuns WHERE ProviderId=p.Id AND Kind=k.Kind AND Success=1),
            r.Finished,r.Success,COALESCE(r.ItemCount,0),COALESCE(r.CategoryCount,0),COALESCE(r.Added,0),COALESCE(r.Removed,0),COALESCE(r.Error,'')
            FROM Providers p CROSS JOIN kinds k LEFT JOIN CatalogSchedule s ON s.ProviderId=p.Id AND s.Kind=k.Kind
            LEFT JOIN CatalogImportRuns r ON r.Id=(SELECT MAX(Id) FROM CatalogImportRuns WHERE ProviderId=p.Id AND Kind=k.Kind)
            WHERE p.Enabled=1 ORDER BY p.Priority,p.Id,k.Kind
            """,Math.Clamp(config.GetValue("Catalog:RefreshHours",6),0,168),Math.Clamp(config.GetValue("Catalog:VodRefreshHours",24),0,168));
        await using var r=await cmd.ExecuteReaderAsync(ct);var result=new List<State>();while(await r.ReadAsync(ct))result.Add(new(r.GetInt32(0),r.GetString(1),r.GetString(2),r.GetInt32(3),r.IsDBNull(4)?null:r.GetInt64(4),r.IsDBNull(5)?null:r.GetInt64(5),r.IsDBNull(6)?null:r.GetBoolean(6),r.GetInt32(7),r.GetInt32(8),r.GetInt32(9),r.GetInt32(10),r.GetString(11)));return result;
    }
    public static long Next(State s,long now)=>s.LastAttempt is long attempt?attempt+(s.Success==false?Math.Min(s.Hours*3600L,900):s.Hours*3600L):now;
    public async Task SetHours(int provider,string kind,int hours,CancellationToken ct)
    {
        if(kind is not ("live" or "movie" or "series")||hours is <0 or >168)throw new ArgumentException("Interval moet tussen 0 en 168 uur liggen.");
        await using var db=await factory.CreateDbContextAsync(ct);await db.Database.OpenConnectionAsync(ct);
        await Exec((SqliteConnection)db.Database.GetDbConnection(),null,"INSERT INTO CatalogSchedule(ProviderId,Kind,Hours) VALUES($0,$1,$2) ON CONFLICT(ProviderId,Kind) DO UPDATE SET Hours=excluded.Hours",ct,provider,kind,hours);
    }
    public async Task<List<Run>> Runs(int provider,string kind,int skip,CancellationToken ct)
    {
        await using var db=await factory.CreateDbContextAsync(ct);await db.Database.OpenConnectionAsync(ct);
        await using var cmd=Cmd((SqliteConnection)db.Database.GetDbConnection(),null,"SELECT r.Id,p.Name,r.Kind,r.Started,r.Finished,r.Success,r.ItemCount,r.CategoryCount,r.Added,r.Removed,r.Error FROM CatalogImportRuns r JOIN Providers p ON p.Id=r.ProviderId WHERE ($0=0 OR r.ProviderId=$0) AND ($1='' OR r.Kind=$1) ORDER BY r.Id DESC LIMIT 51 OFFSET $2",provider,kind,skip);
        await using var r=await cmd.ExecuteReaderAsync(ct);var result=new List<Run>();while(await r.ReadAsync(ct))result.Add(new(r.GetInt64(0),r.GetString(1),r.GetString(2),r.GetInt64(3),r.GetInt64(4),r.GetBoolean(5),r.GetInt32(6),r.GetInt32(7),r.GetInt32(8),r.GetInt32(9),r.GetString(10)));return result;
    }
    public async Task<List<Change>> Changes(int provider,string kind,string entity,string action,string search,int skip,CancellationToken ct)
    {
        await using var db=await factory.CreateDbContextAsync(ct);await db.Database.OpenConnectionAsync(ct);
        await using var cmd=Cmd((SqliteConnection)db.Database.GetDbConnection(),null,"SELECT p.Name,r.Kind,c.Entity,c.Name,c.Category,c.Change,c.Observed FROM CatalogChanges c JOIN CatalogImportRuns r ON r.Id=c.RunId JOIN Providers p ON p.Id=r.ProviderId WHERE ($0=0 OR r.ProviderId=$0) AND ($1='' OR r.Kind=$1) AND ($2='' OR c.Entity=$2) AND ($3='' OR c.Change=$3) AND instr(lower(c.Name||' '||c.Category),lower($4))>0 ORDER BY c.Observed DESC,c.Id DESC LIMIT 101 OFFSET $5",provider,kind,entity,action,search,skip);
        await using var r=await cmd.ExecuteReaderAsync(ct);var result=new List<Change>();while(await r.ReadAsync(ct))result.Add(new(r.GetString(0),r.GetString(1),r.GetString(2),r.GetString(3),r.GetString(4),r.GetString(5),r.GetInt64(6)));return result;
    }
}
