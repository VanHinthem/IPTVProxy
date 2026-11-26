namespace IPTVProxy.Providers;

// Single-viewer compatibility: a new request for the same asset can replace
// an existing request only when no free provider slot exists. Never evict live.
public sealed class VodRequestCoordinator(ProviderConnectionCoordinator capacity)
{
    private readonly object gate=new();
    private readonly List<Lease> active=[];
    public readonly record struct Key(int Provider,string Kind,int Id,string Account,string Client);
    public sealed class Lease : IDisposable
    {
        private readonly VodRequestCoordinator owner;
        private readonly IDisposable reservation;
        internal readonly CancellationTokenSource Stop=new();
        internal readonly TaskCompletionSource Released=new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool Closing;
        internal Key Identity {get;}
        public CancellationToken Token {get;}
        internal Lease(VodRequestCoordinator owner,Key key,IDisposable reservation)
        {this.owner=owner;Identity=key;this.reservation=reservation;Token=Stop.Token;}
        public void Dispose()
        {
            lock(owner.gate)
            {
                if(!owner.active.Remove(this))return;
                reservation.Dispose();Released.TrySetResult();Stop.Dispose();
            }
        }
    }
    public async Task<Lease?> Acquire(Key key,int limit,bool allowReplacement,CancellationToken ct)
    {
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        while(!timeout.IsCancellationRequested)
        {
            Task release;
            lock(gate)
            {
                var slot=capacity.TryReserve(key.Provider,limit);
                if(slot is not null)
                {
                    var lease=new Lease(this,key,slot);active.Add(lease);return lease;
                }
                var previous=allowReplacement?active.FirstOrDefault(x=>x.Identity==key):null;
                if(previous is null)return null;
                if(!previous.Closing){previous.Closing=true;previous.Stop.Cancel();}
                release=previous.Released.Task;
            }
            try{await release.WaitAsync(timeout.Token);}
            catch(OperationCanceledException){return null;}
        }
        return null;
    }
}
