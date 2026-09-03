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
        target.Email.AutodiscoverAddress = Pick(source.Email.AutodiscoverAddress, target.Email.AutodiscoverAddress);
        target.Email.ReportRecipient = Pick(source.Email.ReportRecipient, target.Email.ReportRecipient);

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
        settings.Email.AutodiscoverAddress = Env("EMAIL_AUTODISCOVER_ADDRESS") ?? settings.Email.AutodiscoverAddress;
        settings.Email.ReportRecipient = Env("EMAIL_REPORT_RECIPIENT") ?? settings.Email.ReportRecipient;

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

    // JQL selecting the scoping issues to export.
    public string ProjectQuery { get; set; }

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
    // Address Exchange autodiscover resolves the mailbox from.
    public string AutodiscoverAddress { get; set; }

    public string ReportRecipient { get; set; }
}
