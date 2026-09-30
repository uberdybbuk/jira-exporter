using System.Net;
using System.Text;
using System.Text.Json;

namespace FourArc.JiraExporter;

public class MiniHttpCache
{
    private class HttpCacheItem
    {
        public string Url { get; set; }
        public string Key { get; set; } // MD5 hash of the URL (identity / lookup key)
        public string FileName { get; set; } // Human-readable file name (slug + short hash) inside the entries directory
        public DateTime CachedAt { get; set; } // UTC
    }

    private static readonly string s_cacheDirectory = Constants.HttpCacheDirectory;
    private static readonly string s_entriesDirectory = Path.Combine(s_cacheDirectory, "entries");
    private static readonly string s_indexFile = Path.Combine(s_cacheDirectory, "index.json");
    private static readonly string s_indexHtmlFile = Path.Combine(s_cacheDirectory, "index.html");
    private static readonly TimeSpan s_cacheDuration = TimeSpan.FromDays(1);

    private readonly Dictionary<string, HttpCacheItem> _cache = [];
    private readonly object _lock = new();

    public MiniHttpCache()
    {
        if (!Directory.Exists(s_cacheDirectory))
        {
            Directory.CreateDirectory(s_cacheDirectory);
        }

        if (!Directory.Exists(s_entriesDirectory))
        {
            Directory.CreateDirectory(s_entriesDirectory);
        }

        if (File.Exists(s_indexFile))
        {
            LoadIndex(s_indexFile);
        }
    }

    private void SaveIndex()
    {
        var list = _cache.Values.OrderByDescending(i => i.CachedAt).ToList();
        list.SaveAsJson(s_indexFile);
        SaveIndexHtml(list);
    }

    private void LoadIndex(string indexPath)
    {
        var text = File.ReadAllText(indexPath);
        var list = JsonSerializer.Deserialize<List<HttpCacheItem>>(text);
        if (list == null)
        {
            return;
        }

        foreach (var item in list)
        {
            // Backward compatibility: older index files did not store Key.
            if (string.IsNullOrEmpty(item.Key))
            {
                item.Key = UrlToKey(item.Url ?? string.Empty);
            }

            _cache[item.Key] = item;
        }
    }

    private static string UrlToKey(string url)
    {
        var hashBytes = System.Security.Cryptography.MD5.HashData(Encoding.UTF8.GetBytes(url));
        return Convert.ToHexStringLower(hashBytes);
    }

    private static string UrlToFileName(string url, string key)
    {
        var slugSource = url;
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            // Move the most specific path segment (e.g. the issue key) to the front while
            // keeping the remaining segments in their natural order (so "rest/api/2" stays intact).
            var segments = uri.AbsolutePath
                .Split('/', StringSplitOptions.RemoveEmptyEntries)
                .ToList();

            if (segments.Count > 1)
            {
                var last = segments[^1];
                segments.RemoveAt(segments.Count - 1);
                segments.Insert(0, last);
            }

            slugSource = string.Join("-", segments);

            if (!string.IsNullOrEmpty(uri.Query))
            {
                slugSource += "-" + uri.Query;
            }
        }

        var sb = new StringBuilder();
        foreach (var c in slugSource)
        {
            sb.Append(char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : '-');
        }

        var slug = sb.ToString().Trim('-');
        while (slug.Contains("--"))
        {
            slug = slug.Replace("--", "-");
        }

        if (slug.Length > 80)
        {
            slug = slug[..80].Trim('-');
        }

        if (slug.Length == 0)
        {
            slug = "cache";
        }

        var shortHash = key.Length >= 8 ? key[..8] : key;
        return $"{slug}_{shortHash}.json";
    }

    public void AddToCache(string url, string response)
    {
        var key = UrlToKey(url);

        var cacheItem = new HttpCacheItem
        {
            Url = url,
            Key = key,
            FileName = UrlToFileName(url, key),
            CachedAt = DateTime.UtcNow
        };

        var filePath = Path.Combine(s_entriesDirectory, cacheItem.FileName);

        lock (_lock)
        {
            File.WriteAllText(filePath, response);
            _cache[cacheItem.Key] = cacheItem;
            SaveIndex();
        }
    }

    public bool TryGetFromCache(string url, out string response)
    {
        var key = UrlToKey(url);
        response = null;

        lock (_lock)
        {
            if (_cache.TryGetValue(key, out var cacheItem))
            {
                var filePath = Path.Combine(s_entriesDirectory, cacheItem.FileName);

                if (DateTime.UtcNow - cacheItem.CachedAt < s_cacheDuration)
                {
                    if (File.Exists(filePath))
                    {
                        try
                        {
                            response = File.ReadAllText(filePath);
                            return true;
                        }
                        catch (IOException)
                        {
                            // Corrupt or unreadable cache file: treat as a miss.
                            response = null;
                        }
                    }
                }
                else
                {
                    // Cache expired
                    _cache.Remove(key);
                    if (File.Exists(filePath))
                    {
                        File.Delete(filePath);
                    }
                    SaveIndex();
                }
            }
        }

        return false;
    }

    private void SaveIndexHtml(List<HttpCacheItem> list)
    {
        var now = DateTime.UtcNow;

        var sb = new StringBuilder();
        sb.AppendLine("<!DOCTYPE html>");
        sb.AppendLine("<html lang=\"en\">");
        sb.AppendLine("<head>");
        sb.AppendLine("<meta charset=\"utf-8\">");
        sb.AppendLine("<title>MiniHttpCache index</title>");
        sb.AppendLine("<style>");
        sb.AppendLine("body{font-family:Segoe UI,Arial,sans-serif;margin:1.5rem;color:#222;}");
        sb.AppendLine("h1{font-size:1.2rem;} .meta{color:#666;margin-bottom:1rem;}");
        sb.AppendLine("table{border-collapse:collapse;width:100%;font-size:0.85rem;}");
        sb.AppendLine("th,td{border:1px solid #ddd;padding:6px 8px;text-align:left;vertical-align:top;}");
        sb.AppendLine("th{background:#f4f4f4;position:sticky;top:0;}");
        sb.AppendLine("tr:nth-child(even){background:#fafafa;}");
        sb.AppendLine(".expired{color:#b00;font-weight:600;} .valid{color:#080;}");
        sb.AppendLine("code{word-break:break-all;}");
        sb.AppendLine("</style>");
        sb.AppendLine("</head>");
        sb.AppendLine("<body>");
        sb.AppendLine("<h1>MiniHttpCache index</h1>");
        sb.AppendLine($"<div class=\"meta\">Generated: {HtmlEncode(now.ToString("u"))} &middot; Entries: {list.Count} &middot; TTL: {s_cacheDuration.TotalHours:0} h</div>");
        sb.AppendLine("<table>");
        sb.AppendLine("<thead><tr><th>#</th><th>URL</th><th>File</th><th>Cached At (UTC)</th><th>Status</th></tr></thead>");
        sb.AppendLine("<tbody>");

        var i = 1;
        foreach (var item in list)
        {
            var age = now - item.CachedAt;
            var expired = age >= s_cacheDuration;
            string status = expired
                ? "<span class=\"expired\">expired</span>"
                : $"<span class=\"valid\">valid ({(s_cacheDuration - age).TotalHours:0.0} h left)</span>";

            var href = $"entries/{Uri.EscapeDataString(item.FileName ?? string.Empty)}";

            sb.AppendLine("<tr>");
            sb.AppendLine($"<td>{i}</td>");
            sb.AppendLine($"<td><code>{HtmlEncode(item.Url ?? string.Empty)}</code></td>");
            sb.AppendLine($"<td><a href=\"{HtmlEncode(href)}\">{HtmlEncode(item.FileName ?? string.Empty)}</a></td>");
            sb.AppendLine($"<td>{HtmlEncode(item.CachedAt.ToString("u"))}</td>");
            sb.AppendLine($"<td>{status}</td>");
            sb.AppendLine("</tr>");
            i++;
        }

        sb.AppendLine("</tbody>");
        sb.AppendLine("</table>");
        sb.AppendLine("</body>");
        sb.AppendLine("</html>");

        File.WriteAllText(s_indexHtmlFile, sb.ToString());
    }

    private static string HtmlEncode(string value) => WebUtility.HtmlEncode(value);
}
