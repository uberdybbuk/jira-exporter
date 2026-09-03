using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace FourArc.JiraExporter;

public class JiraCustomFieldExtractor
{
    private readonly HttpClient _client;
    private readonly string _baseSearchUrl;
    private const int PageSize = 500;

    public JiraCustomFieldExtractor(string baseApiUrl, string username, string password)
    {
        _baseSearchUrl = baseApiUrl.TrimEnd('/') + "/search";
        _client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        var authToken = Convert.ToBase64String(Encoding.ASCII.GetBytes($"{username}:{password}"));
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", authToken);
    }

    public async Task<HashSet<string>> GetDistinctCustomFieldValuesAsync(string jql, string customFieldId)
    {
        int startAt = 0;
        int total = 0;
        var result = new HashSet<string>();

        do
        {
            var url = $"{_baseSearchUrl}?jql={Uri.EscapeDataString(jql)}&startAt={startAt}&maxResults={PageSize}&fields={customFieldId}";
            var response = await _client.GetAsync(url);

            if (!response.IsSuccessStatusCode)
            {
                Console.WriteLine($"[ERR] API call failed: {response.StatusCode}");
                break;
            }

            var content = await response.Content.ReadAsStringAsync();
            var json = JObject.Parse(content);
            total = json.Value<int>("total");

            var issues = json["issues"];
            foreach (var issue in issues)
            {
                var field = issue["fields"]?[customFieldId];

                if (field is JArray arr)
                {
                    foreach (var item in arr)
                        result.Add(item?.ToString());
                }
                else if (field != null)
                {
                    result.Add(field.ToString());
                }
            }

            startAt += PageSize;
            Console.WriteLine($"[INFO] Retrieved {result.Count} unique values so far...");

        } while (startAt < total);

        return result;
    }
}
