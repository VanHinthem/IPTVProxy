using IPTVProxy.Components;
using IPTVProxy.Data;
using IPTVProxy.Endpoints;
using IPTVProxy.Providers;
using IPTVProxy.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.RateLimiting;
using System.Threading.RateLimiting;

var builder = WebApplication.CreateBuilder(args);
// Request URLs and upstream exceptions can contain credentials.
builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.None);
builder.Logging.AddFilter("Microsoft.EntityFrameworkCore", LogLevel.None);
builder.Logging.AddFilter("System.Net.Http", LogLevel.None);
var dataDir = Path.GetFullPath(builder.Configuration["DataDirectory"] ?? "data");
Directory.CreateDirectory(dataDir);
builder.Services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(dataDir, "keys"))).SetApplicationName("IPTVProxy");
builder.Services.AddDbContextFactory<CatalogDb>(options => options.UseSqlite(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = Path.Combine(dataDir, "iptvproxy.db") }.ToString()));
builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddSingleton<LocalAccounts>();
builder.Services.AddSingleton<LiveCatalog>();
builder.Services.AddHostedService<CatalogRefresh>();
builder.Services.AddSingleton<LivePlayback>();
builder.Services.AddSingleton<ProviderEpg>();
builder.Services.AddHttpClient("provider", c => {
    c.Timeout = Timeout.InfiniteTimeSpan;
    c.MaxResponseContentBufferSize = 64 * 1024 * 1024;
}).RemoveAllLoggers().ConfigurePrimaryHttpMessageHandler(LiveCatalog.CreateHandler);
builder.Services.AddRateLimiter(options => {
    options.RejectionStatusCode = 429;
    options.AddPolicy("xtream", ctx => RateLimitPartition.GetFixedWindowLimiter(
        ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 120, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(ctx =>
        RateLimitPartition.GetFixedWindowLimiter(ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions { PermitLimit = 300, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
});
var app = builder.Build();
_ = app.Services.GetRequiredService<LocalAccounts>();
var origin = new Uri(builder.Configuration["PublicBaseUrl"] ?? "http://localhost:8080");
if (origin.Scheme is not ("http" or "https") || origin.UserInfo.Length != 0 || origin.AbsolutePath != "/" || origin.Query.Length != 0 || origin.Fragment.Length != 0)
    throw new InvalidOperationException("PublicBaseUrl must be an HTTP(S) origin.");
await using (var scope = app.Services.CreateAsyncScope())
{
    var db = scope.ServiceProvider.GetRequiredService<CatalogDb>();
    var pending = await db.Database.GetPendingMigrationsAsync();
    var rematch = pending.Contains("202611250005_SourceMetadata") || pending.Contains("202611250006_SourceClassification") || pending.Contains("202611250007_ClassificationRefinement");
    await db.Database.MigrateAsync();
    await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;");
    if (rematch && !await ManagedPublication.IsEnabled(db, CancellationToken.None))
    {
        await using var tx = await db.Database.BeginTransactionAsync();
        await ChannelMatching.RematchAsync(db, CancellationToken.None);
        await tx.CommitAsync();
    }
    if (await db.LiveStreams.AnyAsync(x => x.Active && !db.ChannelSources.Any(l => l.LiveStreamId == x.Id)))
    {
        await using var transaction = await db.Database.BeginTransactionAsync();
        await ChannelMatching.ReconcileAsync(db, CancellationToken.None);
        await transaction.CommitAsync();
    }
}
app.UseRouting();
app.UseRateLimiter();
app.Use(async (ctx, next) => await AdminGate.Invoke(ctx, next, app.Services.GetRequiredService<LocalAccounts>()));
app.UseAntiforgery();
app.UseStaticFiles();
app.MapStaticAssets();
app.MapGet("/health", async (IDbContextFactory<CatalogDb> factory, CancellationToken ct) =>
{
    await using var db = await factory.CreateDbContextAsync(ct);
    return await db.Database.CanConnectAsync(ct) ? Results.Ok(new { status = "ok" }) : Results.StatusCode(503);
});
app.MapXtream();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();
app.Run();
