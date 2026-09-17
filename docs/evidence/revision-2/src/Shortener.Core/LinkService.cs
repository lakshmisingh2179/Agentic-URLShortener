using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace Shortener.Core;

public sealed partial class LinkService(JsonStore store)
{
    [GeneratedRegex("^[A-Za-z0-9_-]{4,32}$", RegexOptions.CultureInvariant)]
    private static partial Regex CodePattern();
    public Link Create(string url, string? alias = null, DateTimeOffset? expiresAt = null)
    {
        if (string.IsNullOrWhiteSpace(url) || url.Length > 4096 || url.Any(char.IsControl) ||
            !Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https") || string.IsNullOrEmpty(uri.Host) || !string.IsNullOrEmpty(uri.UserInfo))
            throw new ArgumentException("A valid HTTP(S) URL without credentials is required");
        if (expiresAt <= DateTimeOffset.UtcNow) throw new ArgumentException("Expiry must be in the future");
        if (alias is not null && !CodePattern().IsMatch(alias)) throw new ArgumentException("Alias must be 4–32 URL-safe characters");
        if (alias is not null && new[] { "admin", "health" }.Contains(alias, StringComparer.OrdinalIgnoreCase))
            throw new ArgumentException("Alias is reserved");
        return store.Change(db =>
        {
            var code = alias ?? Convert.ToHexString(RandomNumberGenerator.GetBytes(6)).ToLowerInvariant();
            while (db.Links.ContainsKey(code))
            {
                if (alias is not null) throw new Conflict("Alias already exists");
                code = Convert.ToHexString(RandomNumberGenerator.GetBytes(6)).ToLowerInvariant();
            }
            var link = new Link { Code = code, Url = uri.AbsoluteUri, ExpiresAt = expiresAt };
            db.Links.Add(code, link);
            db.Log("links", "operator", "link-created", code);
            return link;
        });
    }
    public Link? Resolve(string code) => store.Change(db =>
    {
        if (!db.Links.TryGetValue(code, out var link) || link.ExpiresAt <= DateTimeOffset.UtcNow) return null;
        link.Clicks++; return link;
    });
}
