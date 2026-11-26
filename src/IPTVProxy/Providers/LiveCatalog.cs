using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using IPTVProxy.Data;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace IPTVProxy.Providers;

public record ProviderCredentials(string Username, string Password);
public sealed class LiveCatalog(IDbContextFactory<CatalogDb> factory, IDataProtectionProvider protection, IHttpClientFactory clients,CatalogHistory history,ILogger<LiveCatalog> log)
{
    private static readonly SemaphoreSlim ImportGate = new(1, 1);
    private readonly IDataProtector protector = protection.CreateProtector("provider-credentials-v1");

    public static Uri ValidateBase(string value)
    {
        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https") || uri.UserInfo.Length != 0 ||
            uri.Query.Length != 0 || uri.Fragment.Length != 0 ||
            uri.AbsolutePath != "/" || uri.IsLoopback)
            throw new ArgumentException("Gebruik een HTTP- of HTTPS-serveradres zonder extra pad, inloggegevens of queryparameters.");
        return uri;
    }

    // DNS resolution and actual connection use the same validated public IP.
    public static SocketsHttpHandler CreateHandler() => new()
    {
        AllowAutoRedirect = false, UseProxy = false, UseCookies = false,
        ConnectTimeout = TimeSpan.FromSeconds(10),
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        ConnectCallback = async (ctx, ct) =>
        {
            var addresses = await Dns.GetHostAddressesAsync(ctx.DnsEndPoint.Host, ct);
            foreach (var ip in addresses)
            {
                if (ip.AddressFamily != AddressFamily.InterNetwork) continue;
                var b = ip.GetAddressBytes();
                if (b[0] is 0 or 10 or 127 || b[0] >= 224 ||
                    (b[0] == 169 && b[1] == 254) ||
                    (b[0] == 172 && b[1] >= 16 && b[1] <= 31) ||
                    (b[0] == 192 && b[1] is 0 or 168) ||
                    (b[0] == 100 && b[1] >= 64 && b[1] <= 127) ||
                    (b[0] == 198 && b[1] is 18 or 19)) continue;
                var socket = new Socket(ip.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                try
                {
                    await socket.ConnectAsync(new IPEndPoint(ip, ctx.DnsEndPoint.Port), ct);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch { socket.Dispose(); ct.ThrowIfCancellationRequested(); }
            }
            throw new HttpRequestException("No permitted upstream address.");
        }
    };

    public async Task AddAsync(string name, string origin, string username, string password, int limit, CancellationToken ct)
    {
        var uri = ValidateBase(origin);
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(username) ||
            string.IsNullOrWhiteSpace(password) || limit < 1 || limit > 100)
            throw new ArgumentException("Vul naam, gebruikersnaam en wachtwoord in en kies een verbindingslimiet tussen 1 en 100.");
        await using var db = await factory.CreateDbContextAsync(ct);
        db.Providers.Add(new Provider { Name = name.Trim(), BaseUrl = uri.AbsoluteUri,
            ProtectedCredentials = protector.Protect(JsonSerializer.Serialize(new ProviderCredentials(username, password))),
            ConnectionLimit = limit });
        await db.SaveChangesAsync(ct);
    }
    public ProviderCredentials Credentials(Provider provider) =>
        JsonSerializer.Deserialize<ProviderCredentials>(protector.Unprotect(provider.ProtectedCredentials))!;
    public Uri ApiUri(Provider provider, string action)
    {
        var c = Credentials(provider);
        return new Uri(ValidateBase(provider.BaseUrl),
            "player_api.php?username=" + Uri.EscapeDataString(c.Username) +
            "&password=" + Uri.EscapeDataString(c.Password) + "&action=" + action);
    }
    public async Task<int> ImportAsync(int providerId, CancellationToken ct)
    {
        await ImportGate.WaitAsync(ct);
        var started=CatalogHistory.Now();
        try
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            var provider = await db.Providers.SingleAsync(p => p.Id == providerId && p.Enabled, ct);
            using var client = clients.CreateClient("provider");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromMinutes(2));
            using var categoryResponse = await client.GetAsync(ApiUri(provider, "get_live_categories"), timeout.Token);
            categoryResponse.EnsureSuccessStatusCode();
            using var categoryDoc = JsonDocument.Parse(await categoryResponse.Content.ReadAsByteArrayAsync(timeout.Token));
            if (categoryDoc.RootElement.ValueKind != JsonValueKind.Array) throw new InvalidDataException("Invalid categories.");
            var categories = new Dictionary<string, string>(StringComparer.Ordinal);
            var categoryOrder = new Dictionary<string,int>(StringComparer.Ordinal);
            foreach (var item in categoryDoc.RootElement.EnumerateArray())
            {
                var categoryId = item.GetProperty("category_id").ToString();
                var categoryName = item.GetProperty("category_name").GetString();
                if (string.IsNullOrWhiteSpace(categoryId) || string.IsNullOrWhiteSpace(categoryName) ||
                    !categories.TryAdd(categoryId, categoryName))
                    throw new InvalidDataException("Invalid or duplicate category.");
                categoryOrder[categoryId]=categoryOrder.Count;
            }
            using var response = await client.GetAsync(ApiUri(provider, "get_live_streams"), timeout.Token);
            response.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(timeout.Token));
            if (doc.RootElement.ValueKind != JsonValueKind.Array) throw new InvalidDataException("Invalid catalog.");
            var incoming = new Dictionary<long, (string Name, string Category, string EpgId, string Logo, int Order)>();
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                if (!long.TryParse(item.GetProperty("stream_id").ToString(), out var id) || id <= 0)
                    throw new InvalidDataException("Invalid stream ID.");
                var name = item.GetProperty("name").GetString();
                if (string.IsNullOrWhiteSpace(name)) throw new InvalidDataException("Invalid stream name.");
                var cat = item.TryGetProperty("category_id", out var category) ? category.ToString() : "";
                if (!categories.ContainsKey(cat)) categories[cat] = string.IsNullOrEmpty(cat) ? "Uncategorized" : "Category " + cat;
                var epg = item.TryGetProperty("epg_channel_id", out var epgValue) && epgValue.ValueKind == JsonValueKind.String ? epgValue.GetString() ?? "" : "";
                var logo = item.TryGetProperty("stream_icon", out var logoValue) && logoValue.ValueKind == JsonValueKind.String ? logoValue.GetString() ?? "" : "";
                if (!incoming.TryAdd(id, (name, cat, epg, logo, incoming.Count))) throw new InvalidDataException("Duplicate stream ID.");
            }
            var existing = await db.LiveStreams.Where(s => s.ProviderId == providerId).ToListAsync(ct);
            var activeCount = existing.Count(s => s.Active);
            if (incoming.Count == 0 || incoming.Count < activeCount * 0.8)
                throw new InvalidDataException("Empty or suspiciously reduced catalog; previous data retained.");
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            var conn=(Microsoft.Data.Sqlite.SqliteConnection)db.Database.GetDbConnection();
            var sqlTx=(Microsoft.Data.Sqlite.SqliteTransaction)Microsoft.EntityFrameworkCore.Storage.DbContextTransactionExtensions.GetDbTransaction(transaction);
            var before=await CatalogHistory.Snapshot(conn,sqlTx,providerId,"live",ct);
            var logicalBefore=await CatalogHistory.LogicalActive(conn,sqlTx,ct);
            var existingCategories = await db.LiveCategories.Where(c => c.ProviderId == providerId).ToListAsync(ct);
            foreach (var row in existingCategories)
            {
                row.Active = categories.Remove(row.SourceId, out var categoryName);
                if (row.Active) {row.Name = categoryName!;row.SourceOrder=categoryOrder.GetValueOrDefault(row.SourceId,int.MaxValue);}
            }
            foreach (var (sourceId, categoryName) in categories)
                db.LiveCategories.Add(new LiveCategory { ProviderId = providerId, SourceId = sourceId, Name = categoryName, SourceOrder=categoryOrder.GetValueOrDefault(sourceId,int.MaxValue) });
            foreach (var row in existing)
            {
                row.Active = incoming.Remove(row.SourceId, out var value);
                if (row.Active) { row.Name = value.Name; row.CategoryId = value.Category; row.EpgId = value.EpgId; row.Logo = value.Logo; row.SourceOrder=value.Order; }
            }
            foreach (var (id, value) in incoming)
                db.LiveStreams.Add(new LiveStream { ProviderId = providerId, SourceId = id, Name = value.Name, CategoryId = value.Category, EpgId = value.EpgId, Logo = value.Logo, SourceOrder=value.Order });
            await db.SaveChangesAsync(ct);
            await ChannelMatching.ReconcileAsync(db, ct);
            await CatalogHistory.RecordSuccess(conn,sqlTx,providerId,"live",started,before,logicalBefore,ct);
            await transaction.CommitAsync(ct);
            return existing.Count(s => s.Active) + incoming.Count;
        }
        catch(Exception ex) when(!ct.IsCancellationRequested)
        {try{await history.RecordFailure(providerId,"live",started,ex.GetType().Name,ct);}catch{log.LogWarning("Live import history write failed for provider {ProviderId}",providerId);}throw;}
        finally { ImportGate.Release(); }
    }
}
