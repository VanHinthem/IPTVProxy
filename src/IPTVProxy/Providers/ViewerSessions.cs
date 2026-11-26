namespace IPTVProxy.Providers;

// Each active body request owns its lease. No IP-based eviction: an Xtream
// request alone cannot prove that it belongs to the same physical player.
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
        internal Lease(ViewerSessions owner,int accountId,string kind,int assetId)
        {this.owner=owner;AccountId=accountId;Kind=kind;AssetId=assetId;}
        public void Bind(int provider,int source)
        {lock(owner.gate){ProviderId=provider;SourceId=source;}}
        public void AddBytes(int count)=>Interlocked.Add(ref Bytes,count);
        public void Dispose()
        {
            lock(owner.gate)
            {
                if(!owner.active.Remove(Id))return;
                var signal=owner.changed;owner.changed=new(TaskCreationOptions.RunContinuationsAsynchronously);signal.TrySetResult();
            }
        }
    }
    public int Count(int accountId){lock(gate)return active.Values.Count(x=>x.AccountId==accountId);}
    public Snapshot[] List()
    {
        lock(gate)return active.Values.Select(x=>new Snapshot(x.Id,x.AccountId,x.Kind,x.AssetId,x.Started,x.ProviderId,x.SourceId,Interlocked.Read(ref x.Bytes))).ToArray();
    }
    public async Task<Lease?> Acquire(int accountId,int limit,string kind,int assetId,CancellationToken ct)
    {
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        while(!timeout.IsCancellationRequested)
        {
            Task wait;
            lock(gate)
            {
                if(timeout.IsCancellationRequested)return null;
                if(active.Values.Count(x=>x.AccountId==accountId)<limit)
                {
                    var lease=new Lease(this,accountId,kind,assetId);active.Add(lease.Id,lease);return lease;
                }
                wait=changed.Task;
            }
            try{await wait.WaitAsync(timeout.Token);}catch(OperationCanceledException){return null;}
        }
        return null;
    }
}
