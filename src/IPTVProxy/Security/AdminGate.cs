using System.Text;

namespace IPTVProxy.Security;

public static class AdminGate
{
    public static async Task Invoke(HttpContext ctx, Func<Task> next, LocalAccounts accounts)
    {
        var publicOrigin = new Uri(ctx.RequestServices.GetRequiredService<IConfiguration>()["PublicBaseUrl"] ?? "http://localhost:8080");
        // Only the mapped read-only Xtream routes and health may bypass admin auth.
        var endpoint = ctx.GetEndpoint() as RouteEndpoint;
        var route = endpoint?.RoutePattern.RawText;
        if (route is "/health" or "/player_api.php" or "/xmltv.php" or "/live/{username}/{password}/{id:int}.ts"
            or "/artwork/{kind}/{id:int}/{signature}" or "/artwork/programme/{token}" or "/live/{username}/{password}/{id:int}" or "/{username}/{password}/{id:int}")
        { await next(); return; }

        ctx.Response.Headers.CacheControl = "no-store";
        // Reject cross-origin Blazor negotiation and WebSocket connections.
        if (ctx.Request.Headers.TryGetValue("Origin", out var origin) &&
            (!Uri.TryCreate(origin.ToString(), UriKind.Absolute, out var uri) ||
             !string.Equals(uri.Authority, publicOrigin.Authority, StringComparison.OrdinalIgnoreCase) ||
             uri.Scheme != publicOrigin.Scheme))
        { ctx.Response.StatusCode = 403; return; }
        var header = ctx.Request.Headers.Authorization.ToString();
        if (header.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase) && header.Length < 4096)
        {
            try
            {
                var pair = Encoding.UTF8.GetString(Convert.FromBase64String(header[6..])).Split(':', 2);
                if (pair.Length == 2 && accounts.AdminValid(pair[0], pair[1]))
                { await next(); return; }
            }
            catch (FormatException) { }
        }
        ctx.Response.StatusCode = 401;
        ctx.Response.Headers.WWWAuthenticate = "Basic realm=\"IPTVProxy\", charset=\"UTF-8\"";
    }
}
