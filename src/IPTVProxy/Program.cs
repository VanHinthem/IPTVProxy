using IPTVProxy.Components;
using IPTVProxy.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.DataProtection;

var builder = WebApplication.CreateBuilder(args);
var dataDir = Path.GetFullPath(builder.Configuration["DataDirectory"] ?? "data");
Directory.CreateDirectory(dataDir);
builder.Services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(dataDir, "keys"))).SetApplicationName("IPTVProxy");
builder.Services.AddDbContextFactory<CatalogDb>(options => options.UseSqlite(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = Path.Combine(dataDir, "iptvproxy.db") }.ToString()));
builder.Services.AddRazorComponents().AddInteractiveServerComponents();
var app = builder.Build();
await using (var scope = app.Services.CreateAsyncScope())
{
    var db = scope.ServiceProvider.GetRequiredService<CatalogDb>();
    await db.Database.EnsureCreatedAsync();
    await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;");
}
app.UseAntiforgery();
app.MapStaticAssets();
app.MapGet("/health", async (IDbContextFactory<CatalogDb> factory, CancellationToken ct) =>
{
    await using var db = await factory.CreateDbContextAsync(ct);
    return await db.Database.CanConnectAsync(ct) ? Results.Ok(new { status = "ok" }) : Results.StatusCode(503);
});
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();
app.Run();
