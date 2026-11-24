using IPTVProxy.Data;
using IPTVProxy.Providers;
using IPTVProxy.Security;
using Microsoft.EntityFrameworkCore;

namespace IPTVProxy.Endpoints;

public static class XtreamEndpoints
{
    public static void MapXtream(this WebApplication app)
    {
        app.MapGet("/player_api.php", Api).RequireRateLimiting("xtream");
        app.MapGet("/live/{username}/{password}/{id:int}.ts", (HttpContext ctx, string username, string password, int id, LivePlayback playback) => playback.Play(ctx, username, password, id)).RequireRateLimiting("xtream");
        app.MapGet("/live/{username}/{password}/{id:int}", (HttpContext ctx, string username, string password, int id, LivePlayback playback) => playback.Play(ctx, username, password, id)).RequireRateLimiting("xtream");
        app.MapGet("/{username}/{password}/{id:int}", (HttpContext ctx, string username, string password, int id, LivePlayback playback) => playback.Play(ctx, username, password, id)).RequireRateLimiting("xtream");
    }

    private static async Task<IResult> Api(HttpContext ctx, LocalAccounts accounts, IDbContextFactory<CatalogDb> factory, IConfiguration config)
    {
        ctx.Response.Headers.CacheControl = "no-store";
        var q = ctx.Request.Query;
        if (!accounts.IptvValid(q["username"].ToString(), q["password"].ToString()))
            return Results.Json(new { user_info = new { auth = 0, status = "Disabled" } }, statusCode: 401);
        var ct = ctx.RequestAborted;
        await using var db = await factory.CreateDbContextAsync(ct);
        var providers = db.Providers.AsNoTracking().Where(p => p.Enabled);
        var streams = from s in db.LiveStreams.AsNoTracking()
                      join p in providers on s.ProviderId equals p.Id
                      where s.Active
                      select new { s, p };
        switch (q["action"].ToString())
        {
            case "":
                var origin = new Uri(config["PublicBaseUrl"] ?? "http://localhost:8080");
                return Results.Json(new {
                    user_info = new { username = accounts.IptvName, password = q["password"].ToString(),
                        auth = 1, status = "Active", exp_date = (string?)null, is_trial = "0",
                        active_cons = "0", max_connections = "1", allowed_output_formats = new[] { "ts" } },
                    server_info = new { url = origin.Host, port = origin.Port.ToString(),
                        https_port = origin.Scheme == "https" ? origin.Port.ToString() : "",
                        server_protocol = origin.Scheme, timezone = "UTC",
                        timestamp_now = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                        time_now = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss") }
                });
            case "get_live_categories":
                return Results.Json(await providers.OrderBy(p => p.Id).Select(p => new {
                    category_id = p.Id.ToString(), category_name = p.Name, parent_id = 0
                }).ToListAsync(ct));
            case "get_live_streams":
                if (!string.IsNullOrEmpty(q["category_id"]) && q["category_id"] != "0")
                {
                    if (!int.TryParse(q["category_id"], out var cat)) return Results.BadRequest();
                    streams = streams.Where(x => x.p.Id == cat);
                }
                return Results.Json(await streams.OrderBy(x => x.s.Id).Select(x => new {
                    num = x.s.Id, name = x.s.Name, stream_type = "live", stream_id = x.s.Id,
                    stream_icon = "", epg_channel_id = "", category_id = x.p.Id.ToString(),
                    tv_archive = 0, tv_archive_duration = 0, direct_source = ""
                }).ToListAsync(ct));
            case "get_vod_categories": case "get_vod_streams": case "get_series_categories": case "get_series":
                return Results.Json(Array.Empty<object>());
            case "get_short_epg": case "get_simple_data_table":
                return Results.Json(new { epg_listings = Array.Empty<object>() });
            default:
                return Results.BadRequest();
        }
    }
}
