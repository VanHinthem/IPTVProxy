namespace IPTVProxy.Providers;

// Each active body request owns its lease. Optional single-account handoff
// replaces only that account's request, never another account's viewer.
public sealed class ViewerSessions(ILogger<ViewerSessions>? log=null)
{
    private static readonly byte[] diagnosticSalt=System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
    private readonly object gate=new();
    private readonly Dictionary<Guid,Lease> active=new();
    private TaskCompletionSource changed=new(TaskCreationOptions.RunContinuationsAsynchronously);
    public sealed record Snapshot(Guid Id,int AccountId,string Kind,int AssetId,DateTimeOffset Started,int ProviderId,int SourceId,long Bytes);
    public sealed class Lease : IDisposable
    {
        private readonly ViewerSessions owner;
        internal readonly Guid Id=Guid.NewGuid();
        internal readonly int AccountId,AssetId;
        internal readonly string Kind;
        internal readonly DateTimeOffset Started=DateTimeOffset.UtcNow;
        internal int ProviderId,SourceId;
        internal long Bytes;
        internal readonly CancellationTokenSource Stop=new();
        internal bool Closing;
        internal bool SinglePlayer;
        internal string Client="";
        internal string Network="";
        internal string Agent="";
        internal string GroupClient="";
        internal HashSet<string> Aliases=new(StringComparer.Ordinal);
        public CancellationToken Token {get;}

        internal Lease(ViewerSessions owner,int accountId,string kind,int assetId)
        {this.owner=owner;AccountId=accountId;Kind=kind;AssetId=assetId;Token=Stop.Token;}
        public void Bind(int provider,int source)
        {lock(owner.gate){ProviderId=provider;SourceId=source;}}
        public void AddBytes(int count)=>Interlocked.Add(ref Bytes,count);
        public void Dispose()
        {
            lock(owner.gate)
            {
                if(!owner.active.Remove(Id))return;
                Stop.Dispose();
                var signal=owner.changed;owner.changed=new(TaskCreationOptions.RunContinuationsAsynchronously);signal.TrySetResult();
            }
        }
    }
    // Xtream has no player-session identifier. Use a best-effort client key;
    // never trust arbitrary forwarded headers here.
    public static string ClientKey(HttpContext ctx)
    {
        var ip=ctx.Connection.RemoteIpAddress;
        if(ip?.IsIPv4MappedToIPv6==true)ip=ip.MapToIPv4();
        var agent=ctx.Request.Headers.UserAgent.ToString();
        return Fingerprint((ip?.ToString()??"unknown")+"\n"+agent);
    }
    private static string Fingerprint(string value)=>Convert.ToHexString(System.Security.Cryptography.HMACSHA256.HashData(
        diagnosticSalt,System.Text.Encoding.UTF8.GetBytes(value)));
    private static string Short(string value)=>value[..Math.Min(12,value.Length)];
    public static string NetworkKey(HttpContext ctx)
    {
        var ip=ctx.Connection.RemoteIpAddress;
        if(ip?.IsIPv4MappedToIPv6==true)ip=ip.MapToIPv4();
        return Fingerprint(ip?.ToString()??"unknown");
    }
    // User-Agent is untrusted and may contain arbitrary text. Only report a
    // fingerprint, length, and known product family, never its raw contents.
    public static string AgentInfo(HttpContext ctx)
    {
        var value=ctx.Request.Headers.UserAgent.ToString();
        var family=new[]{"libvlc","VLC","Lavf","FFmpeg","ExoPlayer","Media3","Televizo","Arvio","Nuvio","Mozilla","Dalvik","okhttp","WinHTTP","Windows-Media-Player"}
            .FirstOrDefault(x=>value.Contains(x,StringComparison.OrdinalIgnoreCase))??"other";
        return $"{family}/{Short(Fingerprint(value))}/len:{value.Length}";
    }
    private static object LogicalKey(Lease x)=>x.SinglePlayer&&x.Kind=="live"
        ? (object)(x.GroupClient,x.Kind,x.AssetId) : x.Id;
    public int Count(int accountId){lock(gate)return active.Values.Where(x=>x.AccountId==accountId).Select(LogicalKey).Distinct().Count();}
    public Snapshot[] List()
    {
        lock(gate)return active.Values.Select(x=>new Snapshot(x.Id,x.AccountId,x.Kind,x.AssetId,x.Started,x.ProviderId,x.SourceId,Interlocked.Read(ref x.Bytes))).ToArray();
    }
    public async Task<Lease?> Acquire(int accountId,int limit,string kind,int assetId,CancellationToken ct,bool allowHandoff=false,string client="",string network="",string agent="",bool unresolvedProxy=false,bool hasRange=false)
    {
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        var waitingLogged=false;
        var inheritedAliases=new HashSet<string>(StringComparer.Ordinal){client};
        var groupClient=!unresolvedProxy&&!string.IsNullOrEmpty(network)?network:client;
        while(!timeout.IsCancellationRequested)
        {
            Task wait;
            lock(gate)
            {
                if(timeout.IsCancellationRequested)return null;
                var owned=active.Values.Where(x=>x.AccountId==accountId).ToArray();
                var matching=owned.Where(x=>x.SinglePlayer&&!x.Closing&&(x.Aliases.Contains(client)||
                    (x.Kind==kind&&x.AssetId==assetId&&x.GroupClient==groupClient))).ToArray();
                var liveMatches=allowHandoff&&kind=="live"?matching.Count(x=>x.Kind==kind&&x.AssetId==assetId):0;
                var sameLive=liveMatches==1;
                var used=owned.Select(LogicalKey).Distinct().Count();
                var sameVod=allowHandoff&&kind!="live"&&matching.Any(x=>x.Kind==kind&&x.AssetId==assetId);
                if(!sameVod&&liveMatches<2&&(used<limit||sameLive))
                {
                    foreach(var peer in matching.Where(x=>x.Kind==kind&&x.AssetId==assetId))inheritedAliases.UnionWith(peer.Aliases);
                    var lease=new Lease(this,accountId,kind,assetId){SinglePlayer=allowHandoff,Client=client,Network=network,Agent=agent,GroupClient=groupClient,Aliases=new(inheritedAliases,StringComparer.Ordinal)};
                    foreach(var peer in matching.Where(x=>x.Kind==kind&&x.AssetId==assetId))peer.Aliases.UnionWith(inheritedAliases);
                    active.Add(lease.Id,lease);
                    log?.LogInformation("Viewer admission granted: account {AccountId}, limit {Limit}, used {Used}, kind {Kind}, asset {Asset}, client {Client}, network {Network}, agent {Agent}, unresolved proxy {Unresolved}, range {Range}, overlap {Overlap}",accountId,limit,active.Values.Where(x=>x.AccountId==accountId).Select(LogicalKey).Distinct().Count(),kind,assetId,Short(client),Short(network),agent,unresolvedProxy,hasRange,sameLive);
                    return lease;
                }
                // Capture signal before cancellation, including inline release.
                wait=changed.Task;
                Lease[] replace=[];
                if(allowHandoff)
                {
                    var sameAsset=matching.Where(x=>x.Kind==kind&&x.AssetId==assetId).ToArray();
                    // A same-client seek replaces its old VOD request. At capacity,
                    // only a single unambiguous logical session may be handed off.
                    replace=sameVod?sameAsset:
                        sameAsset.Length==0&&matching.Select(LogicalKey).Distinct().Count()==1?matching:Array.Empty<Lease>();
                }
                if(!waitingLogged)
                {
                    waitingLogged=true;
                    var reason=replace.Length>0?"handoff":liveMatches>=2?"duplicate-limit":!allowHandoff?"strict-limit":matching.Length==0?"client-mismatch-or-closing":"logical-limit";
                    var current=string.Join("; ",owned.Take(16).Select(x=>$"{x.Kind}:{x.AssetId} client={Short(x.Client)} network={Short(x.Network)} agent={x.Agent} closing={x.Closing}"));
                    log?.LogInformation("Viewer admission waiting: account {AccountId}, limit {Limit}, used {Used}, kind {Kind}, asset {Asset}, client {Client}, network {Network}, agent {Agent}, unresolved proxy {Unresolved}, range {Range}, reason {Reason}, active [{Active}]",accountId,limit,used,kind,assetId,Short(client),Short(network),agent,unresolvedProxy,hasRange,reason,current);
                }
                foreach(var previous in replace)
                {
                    inheritedAliases.UnionWith(previous.Aliases);
                    previous.Closing=true;previous.Stop.Cancel();
                }
            }
            try{await wait.WaitAsync(timeout.Token);}catch(OperationCanceledException){break;}
        }
        log?.LogWarning("Viewer admission ended: account {AccountId}, kind {Kind}, asset {Asset}, client {Client}, reason {Reason}",accountId,kind,assetId,Short(client),ct.IsCancellationRequested?"request-cancelled":"capacity-timeout");
        return null;
    }
}
