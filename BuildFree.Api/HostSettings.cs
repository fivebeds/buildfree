using Microsoft.AspNetCore.WebUtilities;
using Npgsql;

namespace BuildFree.Api;

// Settings that matter once BuildFree runs on a hosting provider. See the
// "Going online" section of the README for how to use them.
public static class HostSettings
{
    // Many hosts hand out the database as one address, for example
    // postgres://user:password@host:5432/buildfree?sslmode=require, in a
    // DATABASE_URL setting. This turns it into the form Npgsql expects.
    // Returns null if the setting is empty or isn't a postgres:// address.
    public static string? FromDatabaseUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme is not ("postgres" or "postgresql")) return null;
        var user = uri.UserInfo.Split(':', 2);
        var b = new NpgsqlConnectionStringBuilder
        {
            Host = uri.Host,
            Port = uri.Port > 0 ? uri.Port : 5432,
            Database = Uri.UnescapeDataString(uri.AbsolutePath.TrimStart('/')),
            Username = Uri.UnescapeDataString(user[0]),
            Password = user.Length > 1 ? Uri.UnescapeDataString(user[1]) : null
        };
        var query = QueryHelpers.ParseQuery(uri.Query);
        if (query.TryGetValue("sslmode", out var mode) &&
            Enum.TryParse<SslMode>(mode.ToString().Replace("-", ""), ignoreCase: true, out var ssl))
            b.SslMode = ssl;
        return b.ConnectionString;
    }

    // The folder the web app is served from, so the app and the API share one
    // address. Web:Path wins if set; otherwise "wwwroot" next to the API (where
    // the Docker image puts it), then the repo's web folder (when running from
    // source). Null if none exists, and then only the API runs.
    public static string? WebFolder(string contentRoot, string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured)) return Path.GetFullPath(configured);
        var published = Path.Combine(contentRoot, "wwwroot");
        if (File.Exists(Path.Combine(published, "index.html"))) return published;
        var source = Path.GetFullPath(Path.Combine(contentRoot, "..", "web"));
        return File.Exists(Path.Combine(source, "index.html")) ? source : null;
    }
}
