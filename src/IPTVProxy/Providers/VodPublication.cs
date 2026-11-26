using IPTVProxy.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
namespace IPTVProxy.Providers;

public sealed class VodPublication(IDbContextFactory<CatalogDb> factory)
{
    public const int Offset=1000000000;
    // A source belongs to at most one custom group, even while disabled.
    // This preserves one stable item ID and one category per Xtream item.
    internal const string Joins=" LEFT JOIN VodGroupSources gs ON gs.CategoryId=c.Id LEFT JOIN VodGroups g ON g.Id=gs.GroupId ";
    internal const string Visible=" i.Active=1 AND c.Active=1 AND p.Enabled=1 AND ((gs.GroupId IS NULL AND c.Selected=1) OR (COALESCE(g.Enabled,0)=1 AND g.Kind=i.Kind)) ";
    internal const string EffectiveId="CASE WHEN g.Id IS NULL THEN c.Id ELSE 1000000000+g.Id END";
    public sealed record Group(int Id,string Kind,string Name,bool Enabled,int[] Sources);
    public sealed record Published(int Id,string Kind,string Name,long Count);
    private static SqliteCommand Cmd(SqliteConnection c,SqliteTransaction? tx,string sql,params object[] args)
    {var cmd=c.CreateCommand();cmd.Transaction=tx;cmd.CommandText=sql;for(int i=0;i<args.Length;i++)cmd.Parameters.AddWithValue("$"+i,args[i]);return cmd;}
    private static async Task Exec(SqliteConnection c,SqliteTransaction? tx,string sql,CancellationToken ct,params object[] args)
    {await using var cmd=Cmd(c,tx,sql,args);await cmd.ExecuteNonQueryAsync(ct);}
    private static async Task<long> Scalar(SqliteConnection c,SqliteTransaction? tx,string sql,CancellationToken ct,params object[] args)
    {await using var cmd=Cmd(c,tx,sql,args);return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct));}
    public async Task<List<Group>> Groups(string kind,CancellationToken ct)
    {
        await using var db=await factory.CreateDbContextAsync(ct);await db.Database.OpenConnectionAsync(ct);
        await using var cmd=Cmd((SqliteConnection)db.Database.GetDbConnection(),null,"SELECT g.Id,g.Kind,g.Name,g.Enabled,s.CategoryId FROM VodGroups g LEFT JOIN VodGroupSources s ON s.GroupId=g.Id WHERE g.Kind=$0 ORDER BY g.SortOrder,g.Id,s.CategoryId",kind);
        await using var r=await cmd.ExecuteReaderAsync(ct);var groups=new Dictionary<int,(string Kind,string Name,bool Enabled,List<int> Sources)>();
        while(await r.ReadAsync(ct)){var id=r.GetInt32(0);if(!groups.TryGetValue(id,out var x)){x=(r.GetString(1),r.GetString(2),r.GetBoolean(3),new());groups.Add(id,x);}if(!r.IsDBNull(4))x.Sources.Add(r.GetInt32(4));}
        return groups.Select(x=>new Group(x.Key,x.Value.Kind,x.Value.Name,x.Value.Enabled,x.Value.Sources.ToArray())).ToList();
    }
    public async Task<List<Published>> List(string kind,CancellationToken ct)
    {
        await using var db=await factory.CreateDbContextAsync(ct);await db.Database.OpenConnectionAsync(ct);
        await using var cmd=Cmd((SqliteConnection)db.Database.GetDbConnection(),null,"""
            WITH categories AS (
                SELECT c.Id AS Id,c.Kind,c.Name AS Name,p.Priority AS Priority,c.SortOrder AS SourceOrder,c.Id AS Tie,
                       (SELECT count(*) FROM VodItems i WHERE i.CategoryId=c.Id AND i.Active=1) AS Count
                FROM VodCategories c JOIN Providers p ON p.Id=c.ProviderId
                WHERE c.Active=1 AND c.Selected=1 AND p.Enabled=1 AND NOT EXISTS(SELECT 1 FROM VodGroupSources s WHERE s.CategoryId=c.Id)
                UNION ALL
                SELECT 1000000000+g.Id,g.Kind,g.Name,0,g.SortOrder,g.Id,
                       (SELECT count(*) FROM VodItems i JOIN VodCategories c ON c.Id=i.CategoryId JOIN Providers p ON p.Id=c.ProviderId JOIN VodGroupSources s ON s.CategoryId=c.Id WHERE s.GroupId=g.Id AND i.Active=1 AND c.Active=1 AND p.Enabled=1)
                FROM VodGroups g WHERE g.Enabled=1
            )
            SELECT a.Id,a.Kind,a.Name,a.Count FROM categories a LEFT JOIN VodPublicationOrder o ON o.PublicId=a.Id AND o.Kind=a.Kind
            WHERE a.Kind=$0 ORDER BY o.SortOrder IS NULL,o.SortOrder,a.Priority,a.SourceOrder,a.Tie,a.Id
            """,kind);
        await using var r=await cmd.ExecuteReaderAsync(ct);var result=new List<Published>();while(await r.ReadAsync(ct))result.Add(new(r.GetInt32(0),r.GetString(1),r.GetString(2),r.GetInt64(3)));return result;
    }
    public async Task Save(int id,string kind,string name,bool enabled,IEnumerable<int> sources,CancellationToken ct)
    {
        name=name.Trim();var ids=sources.Distinct().ToArray();
        if(kind is not ("movie" or "series")||name.Length is 0 or >200||ids.Length==0)throw new ArgumentException("Vul een naam in en selecteer minstens één broncategorie.");
        await using var db=await factory.CreateDbContextAsync(ct);await db.Database.OpenConnectionAsync(ct);
        var conn=(SqliteConnection)db.Database.GetDbConnection();using var tx=conn.BeginTransaction();
        if(id!=0&&await Scalar(conn,tx,"SELECT count(*) FROM VodGroups WHERE Id=$0 AND Kind=$1",ct,id,kind)!=1)throw new ArgumentException("Categorie niet gevonden.");
        foreach(var source in ids)
        {
            if(await Scalar(conn,tx,"SELECT count(*) FROM VodCategories WHERE Id=$0 AND Kind=$1",ct,source,kind)!=1)throw new ArgumentException("Ongeldig contenttype.");
            if(await Scalar(conn,tx,"SELECT count(*) FROM VodGroupSources WHERE CategoryId=$0 AND GroupId<>$1",ct,source,id)!=0)throw new ArgumentException("Een broncategorie zit al in een andere eigen categorie.");
        }
        if(id==0)
        {
            await Exec(conn,tx,"INSERT INTO VodGroups(Kind,Name,Enabled,SortOrder) VALUES($0,$1,$2,(SELECT COALESCE(MAX(SortOrder),-1)+1 FROM VodGroups WHERE Kind=$0))",ct,kind,name,enabled);
            id=checked((int)await Scalar(conn,tx,"SELECT last_insert_rowid()",ct));
            if(id>=Offset)throw new InvalidOperationException("Category ID namespace exhausted.");
        }
        else await Exec(conn,tx,"UPDATE VodGroups SET Name=$0,Enabled=$1 WHERE Id=$2",ct,name,enabled,id);
        await Exec(conn,tx,"DELETE FROM VodGroupSources WHERE GroupId=$0",ct,id);
        foreach(var source in ids)await Exec(conn,tx,"INSERT INTO VodGroupSources(GroupId,CategoryId) VALUES($0,$1)",ct,id,source);
        await tx.CommitAsync(ct);
    }
    public async Task Delete(int id,CancellationToken ct)
    {
        await using var db=await factory.CreateDbContextAsync(ct);await db.Database.OpenConnectionAsync(ct);var c=(SqliteConnection)db.Database.GetDbConnection();using var tx=c.BeginTransaction();
        await Exec(c,tx,"DELETE FROM VodGroupSources WHERE GroupId=$0",ct,id);
        await Exec(c,tx,"DELETE FROM VodPublicationOrder WHERE PublicId=$0",ct,checked(Offset+id));
        await Exec(c,tx,"DELETE FROM VodGroups WHERE Id=$0",ct,id);await tx.CommitAsync(ct);
    }
    public async Task SelectMany(IEnumerable<int> ids,bool selected,CancellationToken ct)
    {
        await using var db=await factory.CreateDbContextAsync(ct);await db.Database.OpenConnectionAsync(ct);var c=(SqliteConnection)db.Database.GetDbConnection();using var tx=c.BeginTransaction();
        foreach(var id in ids.Distinct())await Exec(c,tx,"UPDATE VodCategories SET Selected=$0 WHERE Id=$1 AND NOT EXISTS(SELECT 1 FROM VodGroupSources WHERE CategoryId=$1)",ct,selected,id);
        await tx.CommitAsync(ct);
    }
    public async Task Reorder(string kind,int[] ids,CancellationToken ct)
    {
        var valid=(await List(kind,ct)).Select(x=>x.Id).ToHashSet();
        if(ids.Length!=valid.Count||!valid.SetEquals(ids))throw new ArgumentException("Publicatielijst is gewijzigd; vernieuw het overzicht.");
        await using var db=await factory.CreateDbContextAsync(ct);await db.Database.OpenConnectionAsync(ct);var c=(SqliteConnection)db.Database.GetDbConnection();using var tx=c.BeginTransaction();
        await Exec(c,tx,"DELETE FROM VodPublicationOrder WHERE Kind=$0",ct,kind);
        for(int i=0;i<ids.Length;i++)await Exec(c,tx,"INSERT INTO VodPublicationOrder(PublicId,Kind,SortOrder) VALUES($0,$1,$2)",ct,ids[i],kind,i);
        await tx.CommitAsync(ct);
    }
    public sealed record Title(int Id,string Name,string Provider,string Category,bool Published);
    public async Task<List<Title>> Search(string kind,string search,bool onlyPublished,int skip,CancellationToken ct)
    {
        await using var db=await factory.CreateDbContextAsync(ct);await db.Database.OpenConnectionAsync(ct);
        await using var cmd=Cmd((SqliteConnection)db.Database.GetDbConnection(),null,"SELECT i.Id,i.Name,p.Name,COALESCE(g.Name,c.Name),("+Visible+") FROM VodItems i JOIN VodCategories c ON c.Id=i.CategoryId JOIN Providers p ON p.Id=i.ProviderId "+Joins+" WHERE i.Active=1 AND c.Active=1 AND p.Enabled=1 AND i.Kind=$0 AND instr(lower(i.Name),lower($1))>0 AND ($2=0 OR ("+Visible+")) ORDER BY i.Name COLLATE NOCASE,i.Id LIMIT 51 OFFSET $3",kind,search,onlyPublished,Math.Max(0,skip));
        await using var r=await cmd.ExecuteReaderAsync(ct);var result=new List<Title>();while(await r.ReadAsync(ct))result.Add(new(r.GetInt32(0),r.GetString(1),r.GetString(2),r.GetString(3),r.GetBoolean(4)));return result;
    }
}
