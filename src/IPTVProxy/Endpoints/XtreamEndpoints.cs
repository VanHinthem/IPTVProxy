using IPTVProxy.Data;
using IPTVProxy.Providers;
using IPTVProxy.Security;
using Microsoft.EntityFrameworkCore;

namespace IPTVProxy.Endpoints;

public static class XtreamEndpoints
{
    public static void MapXtream(this WebApplication app)
    {
        app.MapGet("/xmltv.php", XmlTv).RequireRateLimiting("xtream");
        app.MapGet("/player_api.php", Api).RequireRateLimiting("xtream");
        app.MapGet("/live/{username}/{password}/{id:int}.ts", (HttpContext ctx, string username, string password, int id, LivePlayback playback) => playback.Play(ctx, username, password, id)).RequireRateLimiting("xtream");
        app.MapGet("/live/{username}/{password}/{id:int}", (HttpContext ctx, string username, string password, int id, LivePlayback playback) => playback.Play(ctx, username, password, id)).RequireRateLimiting("xtream");
        app.MapGet("/{username}/{password}/{id:int}", (HttpContext ctx, string username, string password, int id, LivePlayback playback) => playback.Play(ctx, username, password, id)).RequireRateLimiting("xtream");
    }

    private static async Task<IResult> XmlTv(HttpContext ctx,LocalAccounts accounts,XmlTvGuide guide)
    {
        var q=ctx.Request.Query;
        if(!accounts.IptvValid(q["username"].ToString(),q["password"].ToString()))return Results.Unauthorized();
        ctx.Response.Headers.CacheControl="no-store";
        var file=await guide.OpenGuideAsync(ctx.RequestAborted);
        if(file is null)return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        return Results.File(file,"application/xml; charset=utf-8",enableRangeProcessing:false);
    }
    private static async Task<IResult> Api(HttpContext ctx, LocalAccounts accounts, IDbContextFactory<CatalogDb> factory, IConfiguration config, ProviderEpg epg, XmlTvGuide guide)
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
                      join c in db.LiveCategories.AsNoTracking() on new { s.ProviderId, SourceId = s.CategoryId } equals new { c.ProviderId, c.SourceId }
                      where s.Active && c.Active
                      select new { s, p, c };
        var managed = await ManagedPublication.IsEnabled(db, ct);
        if (managed)
        {
            if (q["action"] == "get_live_categories")
                return Results.Json(await db.ChannelGroups.AsNoTracking().Where(g => g.Enabled).OrderBy(g => g.SortOrder).ThenBy(g => g.Id)
                    .Select(g => new { category_id = g.Id.ToString(), category_name = g.Name, parent_id = 0 }).ToListAsync(ct));
            if (q["action"] == "get_live_streams")
            {
                int? groupId = null;
                if (!string.IsNullOrEmpty(q["category_id"]) && q["category_id"] != "0")
                {
                    if (!int.TryParse(q["category_id"], out var parsed)) return Results.BadRequest();
                    groupId = parsed;
                }
                var published = await ManagedPublication.ListAsync(db, groupId, ct);
                return Results.Json(published.Select((x,i) => new {
                    num = i + 1, name = x.Name, stream_type = "live", stream_id = x.Id,
                    stream_icon = "", epg_channel_id = "iptvproxy." + x.ChannelId, category_id = x.GroupId.ToString(),
                    tv_archive = 0, tv_archive_duration = 0, direct_source = ""
                }));
            }
        }
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
                return Results.Json(await (from c in db.LiveCategories.AsNoTracking()
                    join p in providers on c.ProviderId equals p.Id
                    where c.Active
                    orderby p.Priority, p.Id, c.SourceOrder, c.Id
                    select new { category_id = c.Id.ToString(), category_name = p.Name + " - " + c.Name, parent_id = 0 }).ToListAsync(ct));
            case "get_live_streams":
                if (!string.IsNullOrEmpty(q["category_id"]) && q["category_id"] != "0")
                {
                    if (!int.TryParse(q["category_id"], out var cat)) return Results.BadRequest();
                    streams = streams.Where(x => x.c.Id == cat);
                }
                return Results.Json(await streams.OrderBy(x => x.p.Priority).ThenBy(x=>x.p.Id).ThenBy(x=>x.c.SourceOrder).ThenBy(x=>x.s.SourceOrder).ThenBy(x => x.s.Id).Select(x => new {
                    num = x.s.Id, name = x.s.Name, stream_type = "live", stream_id = x.s.Id,
                    stream_icon = "", epg_channel_id = "iptvproxy.raw." + x.s.Id, category_id = x.c.Id.ToString(),
                    tv_archive = 0, tv_archive_duration = 0, direct_source = ""
                }).ToListAsync(ct));
            case "get_vod_categories": case "get_vod_streams": case "get_series_categories": case "get_series":
                return Results.Json(Array.Empty<object>());
            case "get_short_epg": case "get_simple_data_table":
                if(!int.TryParse(q["stream_id"],out var requested))return Results.Json(new {epg_listings=Array.Empty<object>()});
                var limit=int.TryParse(q["limit"],out var count)?Math.Clamp(count,1,100):q["action"]=="get_simple_data_table"?100:10;
                int[] sourceIds;
                if(managed)
                {
                    var candidates=await ManagedPublication.ResolveAsync(db,requested,ct);
                    sourceIds=candidates.Length==0?[]:(await ManagedPublication.Candidates(db,candidates[0].ChannelId).ToListAsync(ct)).OrderByDescending(c=>!string.IsNullOrWhiteSpace(c.EpgId)).ThenBy(c=>c.Priority).ThenBy(c=>c.ProviderId).Select(c=>c.StreamId).ToArray();
                }
                else sourceIds=await streams.Where(x=>x.s.Id==requested).Select(x=>x.s.Id).ToArrayAsync(ct);
                var listings=await guide.GetListingsAsync(sourceIds,limit,q["action"]=="get_simple_data_table",ct);
                if(listings.Length==0)listings=await epg.GetAsync(sourceIds,limit,ct);
                return Results.Json(new {epg_listings=listings});
            default:
                return Results.BadRequest();
        }
    }
}
