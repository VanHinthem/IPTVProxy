using IPTVProxy.Data;
using Microsoft.EntityFrameworkCore;

namespace IPTVProxy.Providers;
public sealed class CatalogRefresh(LiveCatalog catalog, IDbContextFactory<CatalogDb> factory, IConfiguration config, ILogger<CatalogRefresh> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var hours = config.GetValue("Catalog:RefreshHours", 6);
        if (hours <= 0) return;
        using var timer = new PeriodicTimer(TimeSpan.FromHours(Math.Clamp(hours,1,168)));
        while (await timer.WaitForNextTickAsync(ct))
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            var ids = await db.Providers.AsNoTracking().Where(p=>p.Enabled).Select(p=>p.Id).ToListAsync(ct);
            foreach(var id in ids)
            {
                try { await catalog.ImportAsync(id,ct); log.LogInformation("Scheduled catalog import succeeded for provider {ProviderId}",id); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
                catch (Exception ex) { log.LogWarning("Scheduled catalog import failed for provider {ProviderId}: {ErrorType}",id,ex.GetType().Name); }
            }
        }
    }
}
