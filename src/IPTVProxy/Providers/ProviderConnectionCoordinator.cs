namespace IPTVProxy.Providers;
public sealed class ProviderConnectionCoordinator
{
    private readonly object gate=new();
    private readonly Dictionary<int,int> counts=new();
    public IDisposable? TryReserve(int provider,int limit)
    {
        lock(gate)
        {
            if(counts.GetValueOrDefault(provider)>=limit)return null;
            counts[provider]=counts.GetValueOrDefault(provider)+1;
            return new Lease(this,provider);
        }
    }
    private sealed class Lease(ProviderConnectionCoordinator owner,int provider):IDisposable
    {
        private int disposed;
        public void Dispose()
        {
            if(Interlocked.Exchange(ref disposed,1)!=0)return;
            lock(owner.gate){if(--owner.counts[provider]==0)owner.counts.Remove(provider);}
        }
    }
}
