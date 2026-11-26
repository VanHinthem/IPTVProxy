using IPTVProxy.Components;
using IPTVProxy.Data;
using IPTVProxy.Endpoints;
using IPTVProxy.Providers;
using IPTVProxy.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.RateLimiting;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.HttpOverrides;
using System.Net;

var builder = WebApplication.CreateBuilder(args);
// Request URLs and upstream exceptions can contain credentials.
builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.None);
// Do not enable framework exception/URL logging; record only circuit lifecycle and safe status codes ourselves.
builder.Logging.AddFilter("Microsoft.EntityFrameworkCore", LogLevel.None);
builder.Logging.AddFilter("System.Net.Http", LogLevel.None);
var dataDir = Path.GetFullPath(builder.Configuration["DataDirectory"] ?? "data");
Directory.CreateDirectory(dataDir);
builder.Services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(dataDir, "keys"))).SetApplicationName("IPTVProxy");
builder.Services.AddDbContextFactory<CatalogDb>(options => options.UseSqlite(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = Path.Combine(dataDir, "iptvproxy.db") }.ToString()));
builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddSingleton<LocalAccounts>();
builder.Services.AddSingleton<ViewerAccounts>();
builder.Services.AddSingleton<ViewerSessions>();
builder.Services.AddSingleton<ClientNetwork>();
var trustedProxies=ClientNetwork.TrustedProxies(builder.Configuration);
builder.Services.Configure<ForwardedHeadersOptions>(options=>{
    options.ForwardedHeaders=ForwardedHeaders.XForwardedFor;
    options.ForwardLimit=1;
    options.KnownIPNetworks.Clear();
    options.KnownProxies.Clear();
    foreach(var ip in trustedProxies)options.KnownProxies.Add(ip);
});
builder.Services.AddSingleton<LiveCatalog>();
builder.Services.AddSingleton<CatalogHistory>();
builder.Services.AddHostedService<CatalogRefresh>();
builder.Services.AddSingleton<LivePlayback>();
builder.Services.AddSingleton<ProviderConnectionCoordinator>();
builder.Services.AddSingleton<VodCatalog>();
builder.Services.AddSingleton<VodPublication>();
builder.Services.AddSingleton<VodPlayback>();
builder.Services.AddSingleton<VodRequestCoordinator>();
builder.Services.AddSingleton<ProviderEpg>();
builder.Services.AddSingleton<ArtworkService>();
builder.Services.AddSingleton<XmlTvGuide>();
builder.Services.AddHostedService<XmlTvRefresh>();
builder.Services.AddHttpClient("provider", c => {
    c.Timeout = Timeout.InfiniteTimeSpan;
    c.MaxResponseContentBufferSize = 64 * 1024 * 1024;
}).RemoveAllLoggers().ConfigurePrimaryHttpMessageHandler(LiveCatalog.CreateHandler);
builder.Services.AddRateLimiter(options => {
    options.RejectionStatusCode = 429;
    options.AddPolicy("xtream", ctx => RateLimitPartition.GetFixedWindowLimiter(
        ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 120, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
    options.AddPolicy("artwork", ctx => RateLimitPartition.GetFixedWindowLimiter(ctx.Connection.RemoteIpAddress?.ToString()??"unknown", _=>new FixedWindowRateLimiterOptions{PermitLimit=1200,Window=TimeSpan.FromMinutes(1),QueueLimit=0}));
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(ctx =>
        ctx.Request.Path.StartsWithSegments("/artwork") ? RateLimitPartition.GetNoLimiter("artwork") : RateLimitPartition.GetFixedWindowLimiter(ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
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
await app.Services.GetRequiredService<ViewerAccounts>().Bootstrap(CancellationToken.None);
if(trustedProxies.Length>0)
{
    app.Use(async (ctx,next)=>{
        var peer=ctx.Connection.RemoteIpAddress;
        if(peer?.IsIPv4MappedToIPv6==true)peer=peer.MapToIPv4();
        if(peer is not null&&trustedProxies.Contains(peer))ctx.Items["OriginalProxy"]=peer;
        await next();
    });
    app.UseForwardedHeaders();
    app.Use(async(ctx,next)=>{
        if(ctx.Items.TryGetValue("OriginalProxy",out var original)&&original is IPAddress peer)
        {
            var ip=ctx.Connection.RemoteIpAddress;
            if(ip?.IsIPv4MappedToIPv6==true)ip=ip.MapToIPv4();
            if(ip is null||ip.Equals(peer)||trustedProxies.Contains(ip))ctx.Items["UnresolvedProxy"]=true;
        }
        await next();
    });
}
app.UseRouting();
app.Use(async (ctx,next)=>{
    if(ctx.Request.Path.StartsWithSegments("/movie")||ctx.Request.Path.StartsWithSegments("/series"))
    {
        try { await next(); }
        finally { app.Logger.LogInformation("VOD HTTP {Method}, matched {Matched}, status {Status}",ctx.Request.Method,ctx.GetEndpoint() is RouteEndpoint,ctx.Response.StatusCode); }
    }
    else await next();
});
app.Use(async (ctx,next)=>{
    if(ctx.Request.Path.StartsWithSegments("/_blazor")){
        try{await next();}finally{if(ctx.Response.StatusCode>=400)app.Logger.LogWarning("Blazor transport rejected: HTTP {Status}",ctx.Response.StatusCode);}
    }else await next();
});
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
