using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using System.Text.Json.Serialization;

namespace FourArc.JiraExporter;

public class JiraClient(ILogger<JiraClient> logger, JiraSettings settings)
{
    private readonly JiraSettings settings = settings ?? throw new ArgumentNullException(nameof(settings));

    private const int PageSize = 500;

    private HttpClient CreateHttpClientWithAuthHeaders(int timeout)
    {
        // The changelog JSON repeats the same avatar URLs hundreds of times; gzip shrinks it a lot when Jira offers it.
        var handler = new HttpClientHandler { AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate };
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(timeout) };
        var authToken = Convert.ToBase64String(Encoding.ASCII.GetBytes($"{settings.Username}:{settings.Password}"));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", authToken);
        return client;
    }

    private string CreateJiraEndpointUrl(string endpoint, object parameters)
    {
        var url = $"{settings.BaseApiUrl}/{endpoint}";

        if (parameters != null)
        {
            var queryParams = parameters.GetType().GetProperties()
                .Select(p => $"{Uri.EscapeDataString(p.Name)}={Uri.EscapeDataString(p.GetValue(parameters)?.ToString() ?? string.Empty)}");
            url += $"?{string.Join("&", queryParams)}";
        }

        return url;
    }

    private async Task<JObject> CallJiraApi(string endpoint, object parameters = null, int timeout = 20)
    {
        string url = CreateJiraEndpointUrl(endpoint, parameters);

        logger.LogInformation("Calling Jira API: {Url}", url);
        using HttpClient client = CreateHttpClientWithAuthHeaders(timeout);
        using HttpResponseMessage response = await client.GetAsync(url);

        string content = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            logger.LogError($"API call failed: {{Url}}. Status: {{StatusCode}} {{Content}}", url, response.StatusCode, content);
            throw new Exception($"API call failed: {response.StatusCode} - {content}");
        }

        if (response.Content?.Headers?.ContentType?.MediaType != "application/json")
        {
            logger.LogError($"Expected JSON response, but got: {{ContentType}} from {{Url}}", response.Content?.Headers?.ContentType?.MediaType, url);
            logger.LogError("Response content: {Content}", content);
            throw new Exception($"Expected JSON response, but got: {response.Content?.Headers?.ContentType?.MediaType}");
        }

        JObject json = null;
        try
        {
            json = JObject.Parse(content);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to parse JSON response from {Url}. Content: {Content}", url, content);
            throw new Exception("Failed to parse JSON response", ex);
        }

        if (json == null) // Should be caught by the try-catch above, but as a safeguard.
        {
            logger.LogError("Parsed JSON response is null from {Url}. Content: {Content}", url, content);
            throw new Exception("Failed to parse JSON response (resulted in null)");
        }

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
            logger.LogError(e, "Error in SafeGetValue for property '{jiraIssueKey}' '{PropertyName}' (subProperty: '{SubPropertyName}')", issue.Key, propertyName, subPropertyName);
            return "";
        }
    }

    private JiraIssue MapJiraIssue(JiraIssue issue, JToken fieldsJson, string[] customFieldsToInclude = null)
    {
        typeof(JiraIssue).GetProperties()
            .Where(p => p.GetCustomAttributes(typeof(JiraFieldInfoAttribute), false).Length > 0)
            .Where(p => !settings.IsMappedField(((JiraFieldInfoAttribute)p.GetCustomAttributes(typeof(JiraFieldInfoAttribute), false)[0]).FieldName)
                        || (customFieldsToInclude != null
                            && customFieldsToInclude.Contains(((JiraFieldInfoAttribute)p.GetCustomAttributes(typeof(JiraFieldInfoAttribute), false)[0]).FieldName)))
            .ToList()
            .ForEach(pi =>
            {
                var attr = (JiraFieldInfoAttribute)pi.GetCustomAttributes(typeof(JiraFieldInfoAttribute), false)[0];
                // The attribute holds a logical name; settings resolve it to the real Jira field.
                var value = SafeGetValue(issue, fieldsJson, settings.ResolveField(attr.FieldName), attr.SubFieldName);
                if (pi.PropertyType == typeof(DateTime))
                {
                    pi.SetValue(issue, DateTime.Parse(value));
                }
                else if (pi.PropertyType == typeof(DateTime?))
                {
                    pi.SetValue(issue, string.IsNullOrEmpty(value) ? null : DateTime.Parse(value));
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
                        logger.LogWarning("Failed to parse decimal for issue {Key}, field {FieldName}: '{Value}'", issue.Key, attr.FieldName, value);
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
                        logger.LogWarning("Failed to parse nullable decimal for issue {Key}, field {FieldName}: '{Value}'", issue.Key, attr.FieldName, value);
                        pi.SetValue(issue, null);
                    }
                }
                else
                {
                    pi.SetValue(issue, value);
                }
            });

        issue.StatusCategory = fieldsJson["status"]?["statusCategory"]?["key"]?.ToString();

        // Present only when the field was requested.
        if (fieldsJson["parent"] != null)
        {
            issue.ParentKey = fieldsJson["parent"]["key"]?.ToString();
        }

        // Present only when issuelinks was requested. Capture type and direction so the
        // main-project link (an inward link) and other link types can be analysed later.
        if (fieldsJson["issuelinks"] is JArray issueLinks)
        {
            foreach (var link in issueLinks)
            {
                AddIssueLink(issue, link["inwardIssue"], link["type"], "inward");
                AddIssueLink(issue, link["outwardIssue"], link["type"], "outward");
            }
        }

        // Present only when subtasks was requested.
        if (fieldsJson["subtasks"] is JArray subtasks)
        {
            foreach (var subtask in subtasks)
            {
                var key = subtask?["key"]?.ToString();
                if (string.IsNullOrEmpty(key))
                {
                    continue;
                }

                var subtaskFields = subtask["fields"];
                issue.Subtasks.Add(new JiraSubtask
                {
                    Key = key,
                    Summary = subtaskFields?["summary"]?.ToString(),
                    Status = subtaskFields?["status"]?["name"]?.ToString(),
                    StatusCategory = subtaskFields?["status"]?["statusCategory"]?["key"]?.ToString(),
                    IssueType = subtaskFields?["issuetype"]?["name"]?.ToString(),
                });
            }
        }

        return issue;
    }

    // The link type carries a phrase per direction ("slices" / "is sliced by"); the one
    // matching the direction reads correctly from this issue's side.
    private static void AddIssueLink(JiraIssue issue, JToken linkedIssue, JToken linkType, string direction)
    {
        var key = linkedIssue?["key"]?.ToString();
        if (string.IsNullOrEmpty(key))
        {
            return;
        }

        var linkedFields = linkedIssue["fields"];
        issue.IssueLinks.Add(new JiraIssueLink
        {
            LinkType = linkType?["name"]?.ToString(),
            Direction = direction,
            Relation = linkType?[direction]?.ToString(),
            Key = key,
            Summary = linkedFields?["summary"]?.ToString(),
            Status = linkedFields?["status"]?["name"]?.ToString(),
            StatusCategory = linkedFields?["status"]?["statusCategory"]?["key"]?.ToString(),
            IssueType = linkedFields?["issuetype"]?["name"]?.ToString(),
        });
    }

    // Builds the fields= parameter. Attributes carry logical names; the query gets real ones.
    public string GetJiraIssueFields(string[] customFieldsToInclude = null)
    {
        var standardFields = typeof(JiraIssue).GetProperties()
            .Where(p => p.GetCustomAttributes(typeof(JiraFieldInfoAttribute), false).Length > 0)
            .Select(p => (JiraFieldInfoAttribute)p.GetCustomAttributes(typeof(JiraFieldInfoAttribute), false)[0])
            .Where(attr => !settings.IsMappedField(attr.FieldName) || (customFieldsToInclude != null && customFieldsToInclude.Contains(attr.FieldName)))
            .Select(attr => settings.ResolveField(attr.FieldName))
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
        logger.LogInformation("Starting Jira search with JQL: {Jql}", jql);
        int startAt = 0;
        int total = 0;
        var issues = new List<JiraIssue>();

        do
        {
            logger.LogDebug("Fetching page starting at {StartAt} for JQL: {Jql}", startAt, jql);
            JObject json = await CallJiraApi("search",
                new
                {
                    jql = jql,
                    fields = GetJiraIssueFields(customFieldsToInclude),
                    startAt,
                    maxResults = PageSize
                },
                timeout);

            total = json.Value<int>("total");
            logger.LogInformation("Total issues found: {TotalIssues}", total);

            // The response is an array; guard against null.
            if (json["issues"] is not JArray issueList)
            {
                logger.LogWarning("No 'issues' array found in the response for JQL: {Jql}, page starting at {StartAt}", jql, startAt);
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

            // Advance by what the server actually returned: it may cap maxResults below PageSize,
            // and stepping by PageSize would then skip issues without any error.
            if (issueList.Count == 0)
            {
                break;
            }

            startAt += issueList.Count;
        } while (startAt < total);

        logger.LogInformation("Finished Jira search. Retrieved {IssueCount} issues for JQL: {Jql}", issues.Count, jql);
        return issues;
    }

    // One page of raw search results. With expand "changelog" each issue carries its full history:
    // Jira Data Center cannot filter the changelog by field, so the caller keeps what it needs.
    public async Task<JObject> SearchPage(string jql, string fields, string expand, int startAt, int maxResults)
    {
        return await CallJiraApi("search",
            new
            {
                jql,
                fields,
                expand,
                startAt,
                maxResults
            },
            timeout: 120);
    }

    // Returns null when the issue has no fields, which is how a missing issue shows up here.
    public async Task<JiraIssue> GetIssue(string issueKey, string[] customFieldsToInclude = null)
    {
        return (await FetchIssueAsync(issueKey, customFieldsToInclude, withChangelog: false))?.Issue;
    }

    // The issue and its full change history in one request. Null when the issue has no fields.
    public async Task<(JiraIssue Issue, JToken Changelog)?> GetIssueWithChangelog(string issueKey, string[] customFieldsToInclude = null)
    {
        return await FetchIssueAsync(issueKey, customFieldsToInclude, withChangelog: true);
    }

    private async Task<(JiraIssue Issue, JToken Changelog)?> FetchIssueAsync(string issueKey, string[] customFieldsToInclude, bool withChangelog)
    {
        logger.LogInformation("Fetching issue: {IssueKey} ...", issueKey);

        try
        {
            var fields = GetJiraIssueFields(customFieldsToInclude) + ",issuelinks,subtasks";
            JObject json = withChangelog
                ? await CallJiraApi($"issue/{issueKey}", new { fields, expand = "changelog" }, timeout: 60)
                : await CallJiraApi($"issue/{issueKey}", new { fields });

            var fieldsJson = json["fields"];
            if (fieldsJson == null)
            {
                logger.LogWarning("No 'fields' object found in the response for issue: {IssueKey}", issueKey);
                return null;
            }

            var issue = new JiraIssue(json["key"].ToString());
            issue = MapJiraIssue(issue, fieldsJson, customFieldsToInclude);

            var parentString = string.IsNullOrEmpty(issue.ParentKey) ? "<No parent>" : issue.ParentKey;
            logger.LogInformation("Successfully fetched issue {IssueKey} Parent: {ParentString} {Summary}", issueKey, parentString, issue.Summary);
            return (issue, json["changelog"]);
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
        var url = $"{settings.BaseApiUrl}/issue/{issueKey}?expand=changelog&fields=none";
        logger.LogInformation("Downloading changelog for issue {IssueKey} from URL: {Url}", issueKey, url);

        using var httpClient = CreateHttpClientWithAuthHeaders(30);
        using var response = await httpClient.GetAsync(url);
        response.EnsureSuccessStatusCode();

        string json = await response.Content.ReadAsStringAsync();
        logger.LogDebug("Received changelog response for issue {IssueKey}: {Json}", issueKey, json);

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
