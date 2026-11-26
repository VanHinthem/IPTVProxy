using IPTVProxy.Data;
namespace IPTVProxy.Providers;
public sealed class CatalogRefresh(LiveCatalog live,VodCatalog vod,CatalogHistory history,ILogger<CatalogRefresh> log):BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var timer=new PeriodicTimer(TimeSpan.FromMinutes(1));
        while(await timer.WaitForNextTickAsync(ct))
        {
            try
            {
                var states=await history.States(ct);var now=CatalogHistory.Now();
                foreach(var s in states.Where(s=>s.Hours>0&&CatalogHistory.Next(s,now)<=now))
                {
                    try
                    {
                        if(s.Kind=="live")await live.ImportAsync(s.ProviderId,ct);else await vod.ImportAsync(s.ProviderId,s.Kind,ct);
                        log.LogInformation("Scheduled catalog import succeeded: provider {ProviderId}, {Kind}",s.ProviderId,s.Kind);
                    }
                    catch(OperationCanceledException) when(ct.IsCancellationRequested){return;}
                    catch(Exception ex){log.LogWarning("Scheduled catalog import failed: provider {ProviderId}, {Kind}, {ErrorType}",s.ProviderId,s.Kind,ex.GetType().Name);}
                }
            }
            catch(OperationCanceledException) when(ct.IsCancellationRequested){return;}
            catch(Exception ex){log.LogWarning("Catalog scheduling failed: {ErrorType}",ex.GetType().Name);}
        }
    }
}