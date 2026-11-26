namespace IPTVProxy.Providers;

// Each active body request owns its lease. Optional single-account handoff
// replaces only that account's request, never another account's viewer.
public sealed class ViewerSessions
{
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
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes((ip?.ToString()??"unknown")+"\n"+agent)));
    }
    private static object LogicalKey(Lease x)=>x.SinglePlayer&&x.Kind=="live"
        ? (object)(x.Client,x.Kind,x.AssetId) : x.Id;
    public int Count(int accountId){lock(gate)return active.Values.Where(x=>x.AccountId==accountId).Select(LogicalKey).Distinct().Count();}
    public Snapshot[] List()
    {
        lock(gate)return active.Values.Select(x=>new Snapshot(x.Id,x.AccountId,x.Kind,x.AssetId,x.Started,x.ProviderId,x.SourceId,Interlocked.Read(ref x.Bytes))).ToArray();
    }
    public async Task<Lease?> Acquire(int accountId,int limit,string kind,int assetId,CancellationToken ct,bool allowHandoff=false,string client="")
    {
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        while(!timeout.IsCancellationRequested)
        {
            Task wait;
            lock(gate)
            {
                if(timeout.IsCancellationRequested)return null;
                var owned=active.Values.Where(x=>x.AccountId==accountId).ToArray();
                var matching=owned.Where(x=>x.SinglePlayer&&x.Client==client&&!x.Closing).ToArray();
                var sameLive=allowHandoff&&kind=="live"&&matching.Count(x=>x.Kind==kind&&x.AssetId==assetId) is >0 and <2;
                var used=owned.Select(LogicalKey).Distinct().Count();
                var sameVod=allowHandoff&&kind!="live"&&matching.Any(x=>x.Kind==kind&&x.AssetId==assetId);
                if(!sameVod&&(used<limit||sameLive))
                {
                    var lease=new Lease(this,accountId,kind,assetId){SinglePlayer=allowHandoff,Client=client};active.Add(lease.Id,lease);return lease;
                }
                // Capture signal before cancellation, including inline release.
                wait=changed.Task;
                if(allowHandoff)
                {
                    var sameAsset=matching.Where(x=>x.Kind==kind&&x.AssetId==assetId).ToArray();
                    // A same-client seek replaces its old VOD request. At capacity,
                    // only a single unambiguous logical session may be handed off.
                    var replace=sameVod?sameAsset:
                        sameAsset.Length==0&&matching.Select(LogicalKey).Distinct().Count()==1?matching:Array.Empty<Lease>();
                    foreach(var previous in replace)
                    {
                        previous.Closing=true;previous.Stop.Cancel();
                    }
                }
            }
            try{await wait.WaitAsync(timeout.Token);}catch(OperationCanceledException){return null;}
        }
        return null;
    }
}
