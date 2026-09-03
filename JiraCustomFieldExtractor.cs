using System.Text;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using System.Net.Http.Headers;

namespace FourArc.JiraExporter;

// Lists the distinct values a single field takes across the issues a query returns.
// Used when working out what a custom field actually holds before wiring it into
// JiraIssue, so it reads one field at a time rather than the whole issue.
public class JiraCustomFieldExtractor
{
    private const int PageSize = 500;

    private readonly ILogger<JiraCustomFieldExtractor> _logger;
    private readonly JiraSettings _settings;
    private readonly HttpClient _client;

    public JiraCustomFieldExtractor(ILogger<JiraCustomFieldExtractor> logger, JiraSettings settings)
    {
        _logger = logger;
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));

        _client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        var authToken = Convert.ToBase64String(Encoding.ASCII.GetBytes($"{settings.Username}:{settings.Password}"));
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", authToken);
    }

    // The field may be given as a logical name from settings or as a raw Jira field id.
    public async Task<HashSet<string>> GetDistinctValuesAsync(string jql, string field)
    {
        var fieldId = _settings.ResolveField(field);
        _logger.LogInformation("Reading distinct values of {Field} (resolved to {FieldId}) for JQL: {Jql}", field, fieldId, jql);

        var searchUrl = _settings.BaseApiUrl.TrimEnd('/') + "/search";
        var result = new HashSet<string>(StringComparer.Ordinal);
        int startAt = 0;
        int total = 0;

        do
        {
            var url = $"{searchUrl}?jql={Uri.EscapeDataString(jql)}&startAt={startAt}&maxResults={PageSize}&fields={Uri.EscapeDataString(fieldId)}";
            var response = await _client.GetAsync(url);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("API call failed: {StatusCode} for {Url}", response.StatusCode, url);
                break;
            }

            var json = JObject.Parse(await response.Content.ReadAsStringAsync());
            total = json.Value<int>("total");

            if (json["issues"] is not JArray issues)
            {
                _logger.LogWarning("No 'issues' array in the response for {Jql}", jql);
                break;
            }

            foreach (var issue in issues)
            {
                var value = issue["fields"]?[fieldId];

                if (value is JArray array)
                {
                    foreach (var item in array)
                    {
                        result.Add(item?.ToString() ?? "");
                    }
                }
                else if (value is not null && value.Type != JTokenType.Null)
                {
                    result.Add(value.ToString());
                }
            }

            startAt += PageSize;
            _logger.LogInformation("{Count} distinct value(s) so far, {StartAt}/{Total} issues read.", result.Count, Math.Min(startAt, total), total);

        } while (startAt < total);

        return result;
    }
}
