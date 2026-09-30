using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;

namespace FourArc.JiraExporter;

// A single field definition as returned by Jira's /field endpoint.
public class JiraFieldInfo
{
    public string Id { get; set; }
    public string Name { get; set; }
    public bool Custom { get; set; }
    public bool Orderable { get; set; }
    public bool Navigable { get; set; }
    public bool Searchable { get; set; }
    public string SchemaType { get; set; }   // e.g. "string", "number", "array"
    public string SchemaItems { get; set; }  // element type when SchemaType is "array"
    public string CustomType { get; set; }   // e.g. "...customfieldtypes:float"
    public long? CustomId { get; set; }
    public string[] ClauseNames { get; set; }
}

// Dumps every field Jira knows about (system + custom) so their ids and types can be
// looked up before wiring them into JiraIssue or the Fields settings map.
public class JiraFieldDumper
{
    private readonly ILogger<JiraFieldDumper> _logger;
    private readonly JiraSettings _settings;
    private readonly HttpClient _client;

    public JiraFieldDumper(ILogger<JiraFieldDumper> logger, JiraSettings settings)
    {
        _logger = logger;
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));

        _client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        var authToken = Convert.ToBase64String(Encoding.ASCII.GetBytes($"{settings.Username}:{settings.Password}"));
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", authToken);
    }

    public async Task<List<JiraFieldInfo>> GetAllFieldsAsync()
    {
        var url = _settings.BaseApiUrl.TrimEnd('/') + "/field";
        _logger.LogInformation("Fetching all field definitions from {Url}", url);

        var response = await _client.GetAsync(url);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync();
            _logger.LogError("API call failed: {StatusCode} for {Url}. {Body}", response.StatusCode, url, body);
            throw new Exception($"API call failed: {response.StatusCode}");
        }

        var array = JArray.Parse(await response.Content.ReadAsStringAsync());
        var fields = new List<JiraFieldInfo>(array.Count);

        foreach (var field in array)
        {
            var schema = field["schema"];
            fields.Add(new JiraFieldInfo
            {
                Id = field.Value<string>("id"),
                Name = field.Value<string>("name"),
                Custom = field.Value<bool?>("custom") ?? false,
                Orderable = field.Value<bool?>("orderable") ?? false,
                Navigable = field.Value<bool?>("navigable") ?? false,
                Searchable = field.Value<bool?>("searchable") ?? false,
                SchemaType = schema?.Value<string>("type"),
                SchemaItems = schema?.Value<string>("items"),
                CustomType = schema?.Value<string>("custom"),
                CustomId = schema?.Value<long?>("customId"),
                ClauseNames = (field["clauseNames"] as JArray)?.Select(c => c.ToString()).ToArray() ?? [],
            });
        }

        _logger.LogInformation("Fetched {Total} field(s) ({Custom} custom).", fields.Count, fields.Count(f => f.Custom));
        return fields;
    }
}
