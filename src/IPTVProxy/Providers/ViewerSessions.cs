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
    // Legacy bootstrap account is treated as one player unless explicitly overridden.
    public static bool SinglePlayerHandoff(IConfiguration config,string username)=>
        config.GetValue("Playback:SingleViewerChannelSwitch",true) &&
        (config["Playback:SinglePlayerAccounts"]??config["Accounts:IptvName"]??"viewer")
            .Split(',',StringSplitOptions.TrimEntries|StringSplitOptions.RemoveEmptyEntries).Contains(username,StringComparer.Ordinal);
    public int Count(int accountId){lock(gate)return active.Values.Where(x=>x.AccountId==accountId).Select(x=>x.Kind=="live"?$"live:{x.AssetId}":x.Id.ToString()).Distinct().Count();}
    public Snapshot[] List()
    {
        lock(gate)return active.Values.Select(x=>new Snapshot(x.Id,x.AccountId,x.Kind,x.AssetId,x.Started,x.ProviderId,x.SourceId,Interlocked.Read(ref x.Bytes))).ToArray();
    }
    public async Task<Lease?> Acquire(int accountId,int limit,string kind,int assetId,CancellationToken ct,bool allowHandoff=false)
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
                // Count a bounded overlap of same-channel live requests as one logical viewer.
                // Different assets and VOD still require handoff or a free slot.
                var sameLive=allowHandoff&&limit==1&&kind=="live"&&owned.Length<2&&owned.Length>0&&owned.All(x=>!x.Closing&&x.Kind==kind&&x.AssetId==assetId);
                if(owned.Length<limit||sameLive)
                {
                    var lease=new Lease(this,accountId,kind,assetId);active.Add(lease.Id,lease);return lease;
                }
                // Capture the current signal BEFORE cancellation: releasing the old
                // lease may run inline in a cancellation callback.
                wait=changed.Task;
                if(allowHandoff&&limit==1)
                {
                    foreach(var previous in owned.Where(x=>!x.Closing))
                    {
                        // Never restart an active upstream just because a third duplicate arrived.
                        if(kind=="live"&&previous.Kind==kind&&previous.AssetId==assetId)continue;
                        previous.Closing=true;previous.Stop.Cancel();
                    }
                }
            }
            try{await wait.WaitAsync(timeout.Token);}catch(OperationCanceledException){return null;}
        }
        return null;
    }
}
