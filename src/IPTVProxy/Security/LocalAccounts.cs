using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Identity;

namespace IPTVProxy.Security;

// Bootstrap accounts are supplied by the operator, never committed to Git.
public sealed class LocalAccounts
{
    private readonly PasswordHasher<string> _hasher = new();
    private readonly string _adminHash;
    private readonly string _iptvHash;
    public string AdminName { get; }
    public string IptvName { get; }

    public LocalAccounts(IConfiguration config)
    {
        AdminName = config["Accounts:AdminName"] ?? "admin";
        IptvName = config["Accounts:IptvName"] ?? "viewer";
        var admin = config["Accounts:AdminPassword"];
        var iptv = config["Accounts:IptvPassword"];
        if (string.IsNullOrWhiteSpace(admin) || admin.Length < 12 ||
            string.IsNullOrWhiteSpace(iptv) || iptv.Length < 12 ||
            admin == iptv || AdminName == IptvName)
            throw new InvalidOperationException("Configure distinct admin and IPTV accounts with passwords of at least 12 characters.");
        _adminHash = _hasher.HashPassword(AdminName, admin);
        _iptvHash = _hasher.HashPassword(IptvName, iptv);
    }

    public bool AdminValid(string? name, string? password) =>
        Verify(AdminName, _adminHash, name, password);
    public bool IptvValid(string? name, string? password) =>
        Verify(IptvName, _iptvHash, name, password);

    private bool Verify(string expected, string hash, string? name, string? password)
    {
        if (password is null || password.Length > 512 || name is null || name.Length > 256)
            return false;
        var valid = _hasher.VerifyHashedPassword(expected, hash, password) != PasswordVerificationResult.Failed;
        return valid && string.Equals(expected, name, StringComparison.Ordinal);
    }
}
