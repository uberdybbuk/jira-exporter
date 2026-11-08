using System.Net.Mail;
using System.Text.Json;

namespace FourArc.JiraExporter;

public class AppSettings
{
    public const string TemplateFileName = "appsettings.json";
    public const string LocalFileName = "appsettings.local.json";

    public JiraSettings Jira { get; set; } = new();
    public EmailSettings Email { get; set; } = new();

    // Precedence: template file, then the local file, then environment variables.
    public static AppSettings Load(string directory = null)
    {
        directory ??= AppContext.BaseDirectory;

        var settings = ReadFile(Path.Combine(directory, TemplateFileName)) ?? new AppSettings();
        var local = ReadFile(Path.Combine(directory, LocalFileName));
        if (local is not null)
        {
            Merge(settings, local);
        }

        ApplyEnvironmentOverrides(settings);
        settings.Validate(directory);
        return settings;
    }

    private static AppSettings ReadFile(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        };

        return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), options);
    }

    // Only values that are actually set in the local file override the template.
    private static void Merge(AppSettings target, AppSettings source)
    {
        target.Jira.BaseApiUrl = Pick(source.Jira.BaseApiUrl, target.Jira.BaseApiUrl);
        target.Jira.Username = Pick(source.Jira.Username, target.Jira.Username);
        target.Jira.Password = Pick(source.Jira.Password, target.Jira.Password);
        target.Jira.ProjectQuery = Pick(source.Jira.ProjectQuery, target.Jira.ProjectQuery);
        target.Jira.RequestQuery = Pick(source.Jira.RequestQuery, target.Jira.RequestQuery);
        target.Email.From = Pick(source.Email.From, target.Email.From);
        target.Email.To = Pick(source.Email.To, target.Email.To);
        target.Email.Cc = Pick(source.Email.Cc, target.Email.Cc);

        foreach (var (name, value) in source.Jira.Fields)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                target.Jira.Fields[name] = value;
            }
        }

        static string Pick(string preferred, string fallback) =>
            string.IsNullOrWhiteSpace(preferred) ? fallback : preferred;
    }

    private static void ApplyEnvironmentOverrides(AppSettings settings)
    {
        settings.Jira.BaseApiUrl = Env("JIRA_BASE_API_URL") ?? settings.Jira.BaseApiUrl;
        settings.Jira.Username = Env("JIRA_USERNAME") ?? settings.Jira.Username;
        settings.Jira.Password = Env("JIRA_PASSWORD") ?? settings.Jira.Password;
        settings.Jira.ProjectQuery = Env("JIRA_PROJECT_QUERY") ?? settings.Jira.ProjectQuery;
        settings.Jira.RequestQuery = Env("JIRA_REQUEST_QUERY") ?? settings.Jira.RequestQuery;
        settings.Email.From = Env("EMAIL_FROM") ?? settings.Email.From;
        settings.Email.To = Env("EMAIL_TO") ?? settings.Email.To;
        settings.Email.Cc = Env("EMAIL_CC") ?? settings.Email.Cc;

        static string Env(string name)
        {
            var value = Environment.GetEnvironmentVariable(name);
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }
    }

    private void Validate(string directory)
    {
        List<string> missing = [];
        if (string.IsNullOrWhiteSpace(Jira.BaseApiUrl)) missing.Add("Jira.BaseApiUrl");
        if (string.IsNullOrWhiteSpace(Jira.Username)) missing.Add("Jira.Username (or JIRA_USERNAME)");
        if (string.IsNullOrWhiteSpace(Jira.Password)) missing.Add("Jira.Password (or JIRA_PASSWORD)");
        if (string.IsNullOrWhiteSpace(Jira.ProjectQuery)) missing.Add("Jira.ProjectQuery");
        if (Jira.Fields.Count == 0) missing.Add("Jira.Fields");

        if (missing.Count > 0)
        {
            // Point at the project directory, not the output directory: the settings file is
            // created there and copied to the output on build.
            throw new InvalidOperationException(
                $"Missing settings: {string.Join(", ", missing)}. "
                + $"Create '{LocalFileName}' in the project directory and fill it in "
                + $"('{TemplateFileName}' is the template), or set the matching environment "
                + $"variables. Looked in: {directory}");
        }
    }
}

public class JiraSettings
{
    public string BaseApiUrl { get; set; }
    public string Username { get; set; }
    public string Password { get; set; }

    // Wait before each Jira request, from --delay-ms. Not read from the settings files.
    public int RequestDelayMs { get; set; }

    // JQL selecting the scoping issues to export.
    public string ProjectQuery { get; set; }

    // JQL selecting the request issues for the 'requests' command. Only that command needs it.
    public string RequestQuery { get; set; }

    // Logical field name to the real Jira field, e.g. "estimation": "customfield_XXXXX".
    // Custom field IDs differ between installations, so they are configured rather than hardcoded.
    public Dictionary<string, string> Fields { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    // Unmapped names are assumed to be standard Jira fields and returned unchanged.
    public string ResolveField(string logicalName) =>
        Fields.TryGetValue(logicalName, out var actual) && !string.IsNullOrWhiteSpace(actual)
            ? actual
            : logicalName;

    public bool IsMappedField(string logicalName) =>
        Fields.TryGetValue(logicalName, out var actual) && !string.IsNullOrWhiteSpace(actual);
}

public class EmailSettings
{
    // The signed-in Windows account's address; Exchange autodiscover finds the server from it.
    public string From { get; set; }

    // One address, or several separated by ';' or ','.
    public string To { get; set; }
    public string Cc { get; set; }

    public List<string> ToAddresses => Split(To);
    public List<string> CcAddresses => Split(Cc);

    // Returns what is wrong with the settings, or null when mail can be sent.
    public string FindProblem()
    {
        if (string.IsNullOrWhiteSpace(From))
        {
            return "Email.From is empty. Set it in appsettings.local.json.";
        }

        if (ToAddresses.Count == 0)
        {
            return "Email.To is empty. Set it in appsettings.local.json.";
        }

        var invalid = ToAddresses.Concat(CcAddresses).Append(From.Trim())
            .Where(address => !MailAddress.TryCreate(address, out _))
            .ToList();
        return invalid.Count > 0 ? $"Not a valid email address in the Email settings: {string.Join(", ", invalid)}" : null;
    }

    private static List<string> Split(string addresses) =>
        (addresses ?? "").Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
}
