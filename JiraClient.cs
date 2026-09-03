using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using System.Text.Json.Serialization;

namespace FourArc.JiraExporter;

public class JiraClient
{
    private readonly ILogger<JiraClient> _logger;
    private readonly JiraSettings _settings;

    private const int PageSize = 500;
    private readonly MiniHttpCache _httpCache;

    public JiraClient(ILogger<JiraClient> logger, JiraSettings settings)
    {
        _logger = logger;
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _httpCache = new MiniHttpCache();
    }

    private HttpClient CreateHttpClientWithAuthHeaders(int timeout)
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(timeout) };
        var authToken = Convert.ToBase64String(Encoding.ASCII.GetBytes($"{_settings.Username}:{_settings.Password}"));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", authToken);
        return client;
    }

    private string CreateJiraEndpointUrl(string endpoint, object parameters)
    {
        var url = $"{_settings.BaseApiUrl}/{endpoint}";

        if (parameters != null)
        {
            var queryParams = parameters.GetType().GetProperties()
                .Select(p => $"{Uri.EscapeDataString(p.Name)}={Uri.EscapeDataString(p.GetValue(parameters)?.ToString() ?? string.Empty)}");
            url += $"?{string.Join("&", queryParams)}";
        }

        return url;
    }

    private async Task<JObject> CallJiraApi(string endpoint, object parameters = null, int timeout = 20, bool useCache = true)
    {
        string url = CreateJiraEndpointUrl(endpoint, parameters);

        if (useCache)
        {
            if (_httpCache.TryGetFromCache(url, out string cachedResponse))
            {
                _logger.LogDebug("Cache hit for URL: {Url}", url);
                return JObject.Parse(cachedResponse);
            }
            else
            {
                _logger.LogDebug("Cache miss for URL: {Url}", url);
            }
        }
        else
        {
            _logger.LogDebug("Cache bypassed for URL: {Url}", url);
        }

        _logger.LogInformation("Calling Jira API: {Url}", url);
        using HttpClient client = CreateHttpClientWithAuthHeaders(timeout);
        using HttpResponseMessage response = await client.GetAsync(url);

        string content = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError($"API call failed: {{Url}}. Status: {{StatusCode}} {{Content}}", url, response.StatusCode, content);
            throw new Exception($"API call failed: {response.StatusCode} - {content}");
        }

        if (response.Content?.Headers?.ContentType?.MediaType != "application/json")
        {
            _logger.LogError($"Expected JSON response, but got: {{ContentType}} from {{Url}}", response.Content?.Headers?.ContentType?.MediaType, url);
            _logger.LogError("Response content: {Content}", content);
            throw new Exception($"Expected JSON response, but got: {response.Content?.Headers?.ContentType?.MediaType}");
        }

        JObject json = null;
        try
        {
            json = JObject.Parse(content);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to parse JSON response from {Url}. Content: {Content}", url, content);
            throw new Exception("Failed to parse JSON response", ex);
        }

        if (json == null) // Should be caught by the try-catch above, but as a safeguard.
        {
            _logger.LogError("Parsed JSON response is null from {Url}. Content: {Content}", url, content);
            throw new Exception("Failed to parse JSON response (resulted in null)");
        }

        _httpCache.AddToCache(url, content);
        return json;
    }

    private string SafeGetValue(JiraIssue issue, JToken token, string propertyName, string subPropertyName = null) // token and subPropertyName can be null
    {
        try
        {
            var value = token?[propertyName];

            if (value == null)
            {
                return "";
            }

            if (subPropertyName != null)
            {
                if (value.Type == JTokenType.Null)
                {
                    return ""; // a null value has no sub-property to read
                }

                if (value.Type == JTokenType.Object)
                {
                    return value[subPropertyName]?.ToString() ?? "";
                }

                throw new Exception($"Key={issue.Key} Property={propertyName} SubProperty={subPropertyName} is neither null nor an object; type = {value.Type}");
            }

            if (value.Type == JTokenType.Array)
            {
                // Arrays are collapsed to their first element. TODO: handle multi-value fields properly.
                return value.FirstOrDefault()?.ToString() ?? "";
            }

            return value.ToString();
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Error in SafeGetValue for property '{jiraIssueKey}' '{PropertyName}' (subProperty: '{SubPropertyName}')", issue.Key, propertyName, subPropertyName);
            return "";
        }
    }

    private JiraIssue MapJiraIssue(JiraIssue issue, JToken fieldsJson, string[] customFieldsToInclude = null)
    {
        typeof(JiraIssue).GetProperties()
            .Where(p => p.GetCustomAttributes(typeof(JiraFieldInfoAttribute), false).Length > 0)
            .Where(p => !_settings.IsMappedField(((JiraFieldInfoAttribute)p.GetCustomAttributes(typeof(JiraFieldInfoAttribute), false)[0]).FieldName)
                        || (customFieldsToInclude != null
                            && customFieldsToInclude.Contains(((JiraFieldInfoAttribute)p.GetCustomAttributes(typeof(JiraFieldInfoAttribute), false)[0]).FieldName)))
            .ToList()
            .ForEach(pi =>
            {
                var attr = (JiraFieldInfoAttribute)pi.GetCustomAttributes(typeof(JiraFieldInfoAttribute), false)[0];
                // The attribute holds a logical name; settings resolve it to the real Jira field.
                var value = SafeGetValue(issue, fieldsJson, _settings.ResolveField(attr.FieldName), attr.SubFieldName);
                if (pi.PropertyType == typeof(DateTime))
                {
                    pi.SetValue(issue, DateTime.Parse(value));
                }
                else if (pi.PropertyType == typeof(DateOnly?)) // only the types actually used are handled
                {
                    if (string.IsNullOrEmpty(value))
                    {
                        pi.SetValue(issue, null);
                    }
                    else if (DateOnly.TryParse(value, out var dateOnlyValue))
                    {
                        pi.SetValue(issue, dateOnlyValue);
                    }
                }
                else if (pi.PropertyType == typeof(decimal))
                {
                    if (decimal.TryParse(value, out var decimalValue))
                    {
                        pi.SetValue(issue, decimalValue);
                    }
                    else
                    {
                        _logger.LogWarning("Failed to parse decimal for issue {Key}, field {FieldName}: '{Value}'", issue.Key, attr.FieldName, value);
                    }
                }
                else if (pi.PropertyType == typeof(decimal?))
                {
                    if (string.IsNullOrEmpty(value)) // treat an empty string as null
                    {
                        pi.SetValue(issue, null);
                    }
                    else if (decimal.TryParse(value, out var decimalValue))
                    {
                        pi.SetValue(issue, (decimal?)decimalValue);
                    }
                    else
                    {
                        _logger.LogWarning("Failed to parse nullable decimal for issue {Key}, field {FieldName}: '{Value}'", issue.Key, attr.FieldName, value);
                        pi.SetValue(issue, null);
                    }
                }
                else
                {
                    pi.SetValue(issue, value);
                }
            });

        // Present only when the field was requested.
        if (fieldsJson["parent"] != null)
        {
            issue.ParentKey = fieldsJson["parent"]["key"]?.ToString();
        }

        // Present only when issuelinks was requested.
        if (fieldsJson["issuelinks"] is JArray issueLinks)
        {
            foreach (var link in issueLinks)
            {
                var inwardIssue = link["inwardIssue"];
                if (inwardIssue != null)
                {
                    var key = inwardIssue["key"]?.ToString();
                    if (!string.IsNullOrEmpty(key))
                    {
                        issue.InwardLinkedIssueKeys.Add(key);
                    }
                }
            }
        }

        return issue;
    }

    // Builds the fields= parameter. Attributes carry logical names; the query gets real ones.
    public string GetJiraIssueFields(string[] customFieldsToInclude = null)
    {
        var standardFields = typeof(JiraIssue).GetProperties()
            .Where(p => p.GetCustomAttributes(typeof(JiraFieldInfoAttribute), false).Length > 0)
            .Select(p => (JiraFieldInfoAttribute)p.GetCustomAttributes(typeof(JiraFieldInfoAttribute), false)[0])
            .Where(attr => !_settings.IsMappedField(attr.FieldName) || (customFieldsToInclude != null && customFieldsToInclude.Contains(attr.FieldName)))
            .Select(attr => _settings.ResolveField(attr.FieldName))
            .Distinct()
            .ToList();

        if (!standardFields.Contains("key"))
        {
            standardFields.Insert(0, "key");
        }

        return string.Join(",", standardFields);
    }

    public async Task<List<JiraIssue>> Search(string jql, int timeout = 5, string[] customFieldsToInclude = null)
    {
        _logger.LogInformation("Starting Jira search with JQL: {Jql}", jql);
        int startAt = 0;
        int total = 0;
        var issues = new List<JiraIssue>();

        do
        {
            _logger.LogDebug("Fetching page starting at {StartAt} for JQL: {Jql}", startAt, jql);
            JObject json = await CallJiraApi("search",
                new
                {
                    jql = jql,
                    fields = GetJiraIssueFields(customFieldsToInclude),
                    startAt,
                    maxResults = PageSize
                },
                timeout,
                useCache: false); // since this is search no cache is required

            total = json.Value<int>("total");
            _logger.LogInformation("Total issues found: {TotalIssues}", total);

            // The response is an array; guard against null.
            if (json["issues"] is not JArray issueList)
            {
                _logger.LogWarning("No 'issues' array found in the response for JQL: {Jql}, page starting at {StartAt}", jql, startAt);
                break;
            }

            foreach (var item in issueList)
            {
                if (item == null) continue; // Skip null issues

                var fields = item["fields"];
                if (fields == null) continue; // Skip issues without fields

                var issue = new JiraIssue(item["key"].ToString());
                issue = MapJiraIssue(issue, fields, customFieldsToInclude);
                issues.Add(issue);
            }

            startAt += PageSize;
        } while (startAt < total);

        _logger.LogInformation("Finished Jira search. Retrieved {IssueCount} issues for JQL: {Jql}", issues.Count, jql);
        return issues;
    }

    public async Task<List<string>> GetAllFields()
    {
        _logger.LogInformation("Fetching all fields from Jira API");
        var json = await CallJiraApi("field", null);
        return json.ToObject<List<string>>();
    }

    // Returns null when the issue has no fields, which is how a missing issue shows up here.
    public async Task<JiraIssue> GetIssue(string issueKey, string[] customFieldsToInclude = null, bool useCache = true)
    {
        _logger.LogInformation("Fetching issue: {IssueKey} ...", issueKey);

        try
        {
            JObject json = await CallJiraApi($"issue/{issueKey}",
                new
                {
                    fields = GetJiraIssueFields(customFieldsToInclude) + ",issuelinks"
                },
                useCache: useCache);

            var fields = json["fields"];
            if (fields == null)
            {
                _logger.LogWarning("No 'fields' object found in the response for issue: {IssueKey}", issueKey);
                return null;
            }

            var issue = new JiraIssue(json["key"].ToString());
            issue = MapJiraIssue(issue, fields, customFieldsToInclude);

            var parentString = string.IsNullOrEmpty(issue.ParentKey) ? "<No parent>" : issue.ParentKey;
            _logger.LogInformation("Successfully fetched issue {IssueKey} Parent: {ParentString} {Summary}", issueKey, parentString, issue.Summary);
            return issue;
        }
        catch (Exception ex)
        {
            throw new Exception($"Failed to fetch issue {issueKey}: {ex.Message}", ex);
        }
    }

    // TODO: not called from anywhere yet. Kept for looking at how an issue reached its
    // current state, e.g. when a status moved or an estimate was revised.
    public async Task<ChangeLogResponse> DownloadIssueHistoryAsync(string issueKey, int startAt = 0, int maxResults = 100)
    {
        var url = $"{_settings.BaseApiUrl}/issue/{issueKey}?expand=changelog&fields=none";
        _logger.LogInformation("Downloading changelog for issue {IssueKey} from URL: {Url}", issueKey, url);

        using var _httpClient = CreateHttpClientWithAuthHeaders(30);
        using var response = await _httpClient.GetAsync(url);
        response.EnsureSuccessStatusCode();

        string json = await response.Content.ReadAsStringAsync();
        _logger.LogDebug("Received changelog response for issue {IssueKey}: {Json}", issueKey, json);

        var result = JsonSerializer.Deserialize<ChangeLogResponse>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                 ?? throw new InvalidOperationException("Failed to deserialize changelog response.");

        return result;
    }

    public class ChangeLogResponse
    {
        public string Expand { get; set; }
        public string Id { get; set; }
        public string Self { get; set; }
        public string Key { get; set; }
        public ChangeLog Changelog { get; set; }
    }

    public sealed class ChangeLog
    {
        public int StartAt { get; set; }
        public int MaxResults { get; set; }
        public int Total { get; set; }
        public bool IsLast { get; set; }
        public List<ChangelogEntry> Histories { get; set; } = new();
    }

    public sealed class ChangelogEntry
    {
        public string Id { get; set; } = "";
        public JiraUser Author { get; set; }
        
        [JsonConverter(typeof(JiraDateTimeConverter))]
        public DateTimeOffset Created { get; set; }
        
        public List<ChangelogItem> Items { get; set; } = new();
    }

    public sealed class ChangelogItem
    {
        public string Field { get; set; } = "";
        public string FieldType { get; set; }
        public string From { get; set; }
        public string FromString { get; set; }
        public string To { get; set; }

        [JsonPropertyName("toString")]
        public string ToStringValue { get; set; }
    }

    public sealed class JiraUser
    {
        public string AccountId { get; set; }
        public string DisplayName { get; set; }
        public string EmailAddress { get; set; }
    }

    public sealed class JiraDateTimeConverter : JsonConverter<DateTimeOffset>
    {
        public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            string dateString = reader.GetString() 
                ?? throw new JsonException("Date value is null");
            
            // Jira returns +0300; DateTime wants +03:00.
            if (dateString.Length > 6 && !dateString.Contains(':') && char.IsDigit(dateString[^4]))
            {
                dateString = dateString[..^2] + ":" + dateString[^2..];
            }
            
            return DateTimeOffset.Parse(dateString);
        }

        public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options)
        {
            writer.WriteStringValue(value.ToString("O"));
        }
    }
}
