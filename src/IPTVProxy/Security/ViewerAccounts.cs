using IPTVProxy.Data;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace IPTVProxy.Security;

// Database-backed viewer configuration. No credentials in returned management records.
public sealed class ViewerAccounts(IDbContextFactory<CatalogDb> factory, IConfiguration config)
{
    private readonly MemoryCache authCache=new(new MemoryCacheOptions{SizeLimit=256});
    private readonly byte[] cacheKey=RandomNumberGenerator.GetBytes(32);
    private readonly object authGate=new();
    private long authGeneration;
    private readonly PasswordHasher<string> hasher = new();
    private readonly string dummyHash = new PasswordHasher<string>().HashPassword("dummy", Guid.NewGuid().ToString("N"));
    public sealed record Account(int Id,string Username,bool Enabled,int MaxStreams,bool AllowRemote,int PlaylistId);
    public sealed record Playlist(int Id,string Name,bool Enabled,bool AllPublished);
    public sealed record Entry(string Kind,int CategoryId,int SortOrder);
    public sealed record Access(Account Account,Playlist Playlist,Entry[] Entries)
    {
        public bool Allows(string kind,int categoryId) => Playlist.AllPublished || Entries.Any(e=>e.Kind==kind && e.CategoryId==categoryId);
    }
    private static SqliteCommand Command(SqliteConnection c,SqliteTransaction? tx,string sql,params object[] args)
    {
        var cmd=c.CreateCommand();cmd.Transaction=tx;cmd.CommandText=sql;
        for(int i=0;i<args.Length;i++)cmd.Parameters.AddWithValue("$"+i,args[i]);
        return cmd;
    }
    private static async Task Execute(SqliteConnection c,SqliteTransaction? tx,string sql,CancellationToken ct,params object[] args)
    { await using var cmd=Command(c,tx,sql,args);await cmd.ExecuteNonQueryAsync(ct); }
    private static async Task<long> Scalar(SqliteConnection c,SqliteTransaction? tx,string sql,CancellationToken ct,params object[] args)
    { await using var cmd=Command(c,tx,sql,args);return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct)); }

    public async Task Bootstrap(CancellationToken ct)
    {
        await using var db=await factory.CreateDbContextAsync(ct);await db.Database.OpenConnectionAsync(ct);
        var c=(SqliteConnection)db.Database.GetDbConnection();using var tx=c.BeginTransaction();
        // Only initialize an empty account store. Never overwrite a changed password or re-enable a disabled account.
        if(await Scalar(c,tx,"SELECT count(*) FROM ViewerAccounts",ct)==0)
        {
            var name=config["Accounts:IptvName"]??"viewer";
            var password=config["Accounts:IptvPassword"]??throw new InvalidOperationException("Missing bootstrap viewer password.");
            var playlist=await Scalar(c,tx,"SELECT Id FROM ViewerPlaylists WHERE Name='Standaard'",ct);
            await Execute(c,tx,"INSERT INTO ViewerAccounts(Username,PasswordHash,AllowRemote) VALUES($0,$1,1)",ct,name,hasher.HashPassword(name,password));
            var id=await Scalar(c,tx,"SELECT last_insert_rowid()",ct);
            await Execute(c,tx,"INSERT INTO ViewerPlaylistAssignments(AccountId,PlaylistId,IsDefault) VALUES($0,$1,1)",ct,id,playlist);
        }
        await tx.CommitAsync(ct);
        lock(authGate){authGeneration++;authCache.Clear();}
    }

    public async Task<Access?> Authenticate(string? name,string? password,CancellationToken ct)
    {
        if(string.IsNullOrEmpty(name)||name.Length>256||string.IsNullOrEmpty(password)||password.Length>512)return null;
        var key=Convert.ToHexString(HMACSHA256.HashData(cacheKey,Encoding.UTF8.GetBytes(name+"\0"+password)));
        long generation;
        lock(authGate)
        {
            if(authCache.TryGetValue<Access>(key,out var cached))return cached;
            generation=authGeneration;
        }
        await using var db=await factory.CreateDbContextAsync(ct);await db.Database.OpenConnectionAsync(ct);
        var c=(SqliteConnection)db.Database.GetDbConnection();
        Account? account=null;Playlist? playlist=null;string hash=dummyHash;
        await using(var cmd=Command(c,null,"SELECT a.Id,a.Username,a.Enabled,a.MaxStreams,a.AllowRemote,a.PasswordHash,p.Id,p.Name,p.Enabled,p.AllPublished FROM ViewerAccounts a LEFT JOIN ViewerPlaylistAssignments x ON x.AccountId=a.Id AND x.IsDefault=1 LEFT JOIN ViewerPlaylists p ON p.Id=x.PlaylistId WHERE a.Username=$0",name))
        await using(var reader=await cmd.ExecuteReaderAsync(ct))
        {
            if(await reader.ReadAsync(ct))
            {
                hash=reader.GetString(5);
                if(!reader.IsDBNull(6))
                {
                    account=new(reader.GetInt32(0),reader.GetString(1),reader.GetBoolean(2),reader.GetInt32(3),reader.GetBoolean(4),reader.GetInt32(6));
                    playlist=new(reader.GetInt32(6),reader.GetString(7),reader.GetBoolean(8),reader.GetBoolean(9));
                }
            }
        }
        var valid=hasher.VerifyHashedPassword(name,hash,password)!=PasswordVerificationResult.Failed;
        if(!valid||account is null||playlist is null||!account.Enabled||!playlist.Enabled)return null;
        var access=new Access(account,playlist,await ReadEntries(c,playlist.Id,ct));
        lock(authGate)
        {
            if(generation==authGeneration)authCache.Set(key,access,new MemoryCacheEntryOptions{Size=1,AbsoluteExpirationRelativeToNow=TimeSpan.FromSeconds(15)});
        }
        return access;
    }

    private static async Task<Entry[]> ReadEntries(SqliteConnection c,int id,CancellationToken ct)
    {
        await using var cmd=Command(c,null,"SELECT Kind,CategoryId,SortOrder FROM ViewerPlaylistEntries WHERE PlaylistId=$0 ORDER BY SortOrder,CategoryId",id);
        await using var reader=await cmd.ExecuteReaderAsync(ct);var rows=new List<Entry>();
        while(await reader.ReadAsync(ct))rows.Add(new(reader.GetString(0),reader.GetInt32(1),reader.GetInt32(2)));
        return rows.ToArray();
    }
    public async Task<Entry[]> Entries(int id,CancellationToken ct)
    {
        await using var db=await factory.CreateDbContextAsync(ct);await db.Database.OpenConnectionAsync(ct);
        return await ReadEntries((SqliteConnection)db.Database.GetDbConnection(),id,ct);
    }
    public async Task<List<Playlist>> Playlists(CancellationToken ct)
    {
        await using var db=await factory.CreateDbContextAsync(ct);await db.Database.OpenConnectionAsync(ct);
        await using var cmd=Command((SqliteConnection)db.Database.GetDbConnection(),null,"SELECT Id,Name,Enabled,AllPublished FROM ViewerPlaylists ORDER BY Name");
        await using var reader=await cmd.ExecuteReaderAsync(ct);var rows=new List<Playlist>();
        while(await reader.ReadAsync(ct))rows.Add(new(reader.GetInt32(0),reader.GetString(1),reader.GetBoolean(2),reader.GetBoolean(3)));
        return rows;
    }
    public async Task<List<Account>> Accounts(CancellationToken ct)
    {
        await using var db=await factory.CreateDbContextAsync(ct);await db.Database.OpenConnectionAsync(ct);
        await using var cmd=Command((SqliteConnection)db.Database.GetDbConnection(),null,"SELECT a.Id,a.Username,a.Enabled,a.MaxStreams,a.AllowRemote,COALESCE(x.PlaylistId,0) FROM ViewerAccounts a LEFT JOIN ViewerPlaylistAssignments x ON a.Id=x.AccountId AND x.IsDefault=1 ORDER BY a.Username");
        await using var reader=await cmd.ExecuteReaderAsync(ct);var rows=new List<Account>();
        while(await reader.ReadAsync(ct))rows.Add(new(reader.GetInt32(0),reader.GetString(1),reader.GetBoolean(2),reader.GetInt32(3),reader.GetBoolean(4),reader.GetInt32(5)));
        return rows;
    }
    public async Task SaveAccount(int id,string name,string? password,bool enabled,int maxStreams,bool allowRemote,int playlistId,CancellationToken ct)
    {
        name=name.Trim();
        if(name.Length is <1 or >128||name.Any(c=>!char.IsAsciiLetterOrDigit(c)&&c!='_'&&c!='-'&&c!='.')||name==(config["Accounts:AdminName"]??"admin")||maxStreams is <1 or >64)
            throw new ArgumentException("Ongeldige gebruikersnaam of streamlimiet.");
        if((id==0||!string.IsNullOrEmpty(password))&&(password is null||password.Length is <8 or >512||password.Any(c=>char.IsWhiteSpace(c)||char.IsControl(c)||"/\\?#%&+=".Contains(c))||password==config["Accounts:AdminPassword"]))
            throw new ArgumentException("Gebruik een afzonderlijk wachtwoord van minimaal 8 tekens, zonder spaties of de URL-tekens / \\ ? # % & + =.");
        await using var db=await factory.CreateDbContextAsync(ct);await db.Database.OpenConnectionAsync(ct);
        var c=(SqliteConnection)db.Database.GetDbConnection();using var tx=c.BeginTransaction();
        if(await Scalar(c,tx,"SELECT count(*) FROM ViewerPlaylists WHERE Id=$0 AND Enabled=1",ct,playlistId)!=1)
            throw new ArgumentException("Selecteer een actieve playlist.");
        if(await Scalar(c,tx,"SELECT count(*) FROM ViewerAccounts WHERE Username=$0 AND Id<>$1",ct,name,id)!=0)
            throw new ArgumentException("Gebruikersnaam bestaat al.");
        if(id==0)
        {
            await Execute(c,tx,"INSERT INTO ViewerAccounts(Username,PasswordHash,Enabled,MaxStreams,AllowRemote) VALUES($0,$1,$2,$3,$4)",ct,name,hasher.HashPassword(name,password!),enabled,maxStreams,allowRemote);
            id=checked((int)await Scalar(c,tx,"SELECT last_insert_rowid()",ct));
        }
        else
        {
            if(await Scalar(c,tx,"SELECT count(*) FROM ViewerAccounts WHERE Id=$0",ct,id)!=1)throw new ArgumentException("Gebruiker bestaat niet.");
            await Execute(c,tx,"UPDATE ViewerAccounts SET Username=$0,Enabled=$1,MaxStreams=$2,AllowRemote=$3 WHERE Id=$4",ct,name,enabled,maxStreams,allowRemote,id);
            if(!string.IsNullOrEmpty(password))await Execute(c,tx,"UPDATE ViewerAccounts SET PasswordHash=$0 WHERE Id=$1",ct,hasher.HashPassword(name,password),id);
        }
        await Execute(c,tx,"UPDATE ViewerPlaylistAssignments SET IsDefault=0 WHERE AccountId=$0",ct,id);
        await Execute(c,tx,"INSERT INTO ViewerPlaylistAssignments(AccountId,PlaylistId,IsDefault) VALUES($0,$1,1) ON CONFLICT(AccountId,PlaylistId) DO UPDATE SET IsDefault=1",ct,id,playlistId);
        await tx.CommitAsync(ct);
        lock(authGate){authGeneration++;authCache.Clear();}
    }
    public async Task SavePlaylist(int id,string name,bool enabled,bool allPublished,IEnumerable<Entry> entries,CancellationToken ct)
    {
        name=name.Trim();var rows=entries.DistinctBy(x=>(x.Kind,x.CategoryId)).ToArray();
        if(name.Length is <1 or >200||rows.Any(x=>x.CategoryId<=0||x.Kind is not ("live" or "raw" or "movie" or "series")))
            throw new ArgumentException("Ongeldige playlist.");
        await using var db=await factory.CreateDbContextAsync(ct);await db.Database.OpenConnectionAsync(ct);
        var c=(SqliteConnection)db.Database.GetDbConnection();using var tx=c.BeginTransaction();
        if(await Scalar(c,tx,"SELECT count(*) FROM ViewerPlaylists WHERE Name=$0 COLLATE NOCASE AND Id<>$1",ct,name,id)!=0)throw new ArgumentException("Playlistnaam bestaat al.");
        if(id==0)
        {
            await Execute(c,tx,"INSERT INTO ViewerPlaylists(Name,Enabled,AllPublished) VALUES($0,$1,$2)",ct,name,enabled,allPublished);
            id=checked((int)await Scalar(c,tx,"SELECT last_insert_rowid()",ct));
        }
        else
        {
            if(await Scalar(c,tx,"SELECT count(*) FROM ViewerPlaylists WHERE Id=$0",ct,id)!=1)throw new ArgumentException("Playlist bestaat niet.");
            await Execute(c,tx,"UPDATE ViewerPlaylists SET Name=$0,Enabled=$1,AllPublished=$2 WHERE Id=$3",ct,name,enabled,allPublished,id);
        }
        await Execute(c,tx,"DELETE FROM ViewerPlaylistEntries WHERE PlaylistId=$0",ct,id);
        foreach(var e in rows)await Execute(c,tx,"INSERT INTO ViewerPlaylistEntries(PlaylistId,Kind,CategoryId,SortOrder) VALUES($0,$1,$2,$3)",ct,id,e.Kind,e.CategoryId,e.SortOrder);
        await tx.CommitAsync(ct);
        lock(authGate){authGeneration++;authCache.Clear();}
    }
}
