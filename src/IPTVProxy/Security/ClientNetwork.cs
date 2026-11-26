using System.Net;
using Microsoft.AspNetCore.HttpOverrides;

namespace IPTVProxy.Security;

// Only explicit operator-configured networks count as local. Never infer locality
// from RFC1918 addresses: reverse proxies and Docker bridges also use them.
public sealed class ClientNetwork
{
    private readonly System.Net.IPNetwork[] localNetworks;
    public ClientNetwork(IConfiguration config)
    {
        localNetworks=(config["Network:LocalNetworks"]??"127.0.0.0/8,::1/128")
            .Split(',',StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries)
            .Select(System.Net.IPNetwork.Parse).ToArray();
    }
    public bool IsLocal(HttpContext ctx)
    {
        var ip=ctx.Connection.RemoteIpAddress;
        if(ip is null)return false;
        if(ip.IsIPv4MappedToIPv6)ip=ip.MapToIPv4();
        // A configured reverse proxy without a usable forwarded client address
        // must not grant local-only access.
        if(ctx.Items.ContainsKey("UnresolvedProxy"))return false;
        return localNetworks.Any(n=>n.Contains(ip));
    }
    public static IPAddress[] TrustedProxies(IConfiguration config)=>
        (config["Network:TrustedProxies"]??"").Split(',',StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries)
        .Select(IPAddress.Parse).Select(ip=>ip.IsIPv4MappedToIPv6?ip.MapToIPv4():ip).Distinct().ToArray();
}
