using System.Text;
using System.Text.Json;

namespace FourArc.JiraExporter;

public class MiniHttpCache
{
    private class HttpCacheItem
    {
        public string Url { get; set; }
        public string FileName { get; set; } // MD5 hash of the URL
        public DateTime CachedAt { get; set; }
    }

    private static readonly string s_cacheDirectory = Constants.HttpCacheDirectory;
    private static readonly string s_indexFile = Path.Combine(s_cacheDirectory, "index.json");
    private static readonly TimeSpan s_cacheDuration = TimeSpan.FromDays(1);

    private readonly Dictionary<string, HttpCacheItem> _cache = [];

    public MiniHttpCache()
    {
        if (!Directory.Exists(s_cacheDirectory))
        {
            Directory.CreateDirectory(s_cacheDirectory);
        }

        if (File.Exists(s_indexFile))
        {
            LoadIndex(s_indexFile);
        }
    }

    private void SaveIndex()
    {
        var list = _cache.Values.ToList();
        list.SaveAsJson(s_indexFile);
    }

    private void LoadIndex(string indexPath)
    {
        var text = File.ReadAllText(indexPath);
        var list = JsonSerializer.Deserialize<List<HttpCacheItem>>(text);
        foreach (var item in list)
        {
            _cache[item.FileName] = item;
        }
    }

    private string UrlToFileName(string url)
    {
        var hashBytes = System.Security.Cryptography.MD5.HashData(Encoding.UTF8.GetBytes(url));
        return Convert.ToHexStringLower(hashBytes);
    }

    public void AddToCache(string url, string response)
    {
        var fileName = UrlToFileName(url);

        var cacheItem = new HttpCacheItem
        {
            Url = url,
            FileName = fileName,
            CachedAt = DateTime.Now
        };

        var filePath = Path.Combine(s_cacheDirectory, $"{cacheItem.FileName}.json");
        File.WriteAllText(filePath, response);

        _cache[cacheItem.FileName] = cacheItem;
        SaveIndex();
    }

    public bool TryGetFromCache(string url, out string response)
    {
        var fileName = UrlToFileName(url);
        response = null;

        if (_cache.TryGetValue(fileName, out var cacheItem))
        {
            if (DateTime.Now - cacheItem.CachedAt < s_cacheDuration)
            {
                var filePath = Path.Combine(s_cacheDirectory, $"{cacheItem.FileName}.json");
                if (File.Exists(filePath))
                {
                    response = File.ReadAllText(filePath);
                    return true;
                }
            }
            else
            {
                // Cache expired
                _cache.Remove(fileName);
                var filePath = Path.Combine(s_cacheDirectory, $"{cacheItem.FileName}.json");
                if (File.Exists(filePath))
                {
                    File.Delete(filePath);
                }
                SaveIndex();
            }
        }

        return false;
    }
}
