using System.Text.RegularExpressions;
using ClosedXML.Excel;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace FourArc.JiraExporter;

public class ProjectChangelog
{
    public string Key { get; set; }
    public string Summary { get; set; }
    public string Status { get; set; }
    public string StatusCategory { get; set; }
    public string Resolved { get; set; }

    // Entries in Jira's history before the noisy fields were dropped.
    public int HistoryCount { get; set; }

    public List<FieldChange> Changes { get; set; } = [];
}

public class FieldChange
{
    // "yyyy-MM-dd HH:mm:ss" in the machine's time zone (Türkiye on the Jira machine), so it sorts as text.
    public string Changed { get; set; }

    public string Author { get; set; }
    public string Field { get; set; }
    public string From { get; set; }
    public string To { get; set; }
}

// Change history of the project issues, where status and phase dates live. Scoping issues are left out.
// Only open projects and the ones resolved in the last months are read; the closed ones show the whole
// path to production. Jira Data Center cannot filter the changelog by field, so it comes back whole and
// the noisy fields are dropped when the outputs are built.
//
// Each issue is kept raw in data/changelogs/<KEY>.json. A run first asks Jira only for each issue's
// 'updated' time, then downloads the changelog of the issues that are new or whose 'updated' moved.
// An interrupted run therefore resumes, and a later run costs a few light requests. The outputs are
// always built from the files, so --offline rebuilds them without calling Jira at all.
public class ChangelogExporter(ILoggerFactory loggerFactory, JiraSettings settings)
{
    private const int BatchSize = 50;
    private const int MaxValueLength = 200;
    private const string Fields = "summary,status,resolutiondate,updated";

    private static readonly HashSet<string> s_droppedFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "description", "summary", "Comment", "Attachment", "Link", "RemoteIssueLink", "labels", "Rank",
        "Worklog", "WorklogId", "timespent", "timeestimate", "timeoriginalestimate"
    };

    private readonly ILogger<ChangelogExporter> logger = loggerFactory.CreateLogger<ChangelogExporter>();
    private readonly JiraClient jiraClient = new(loggerFactory.CreateLogger<JiraClient>(), settings);

    public async Task<List<ProjectChangelog>> ExportAsync(bool offline)
    {
        Directory.CreateDirectory(Constants.ChangelogIssueDirectory);

        List<string> keys;
        if (offline)
        {
            // Only <KEY>.json; older runs left batch-*.json files in the same folder.
            keys = Directory.GetFiles(Constants.ChangelogIssueDirectory, "*.json")
                .Select(Path.GetFileNameWithoutExtension)
                .Where(name => Regex.IsMatch(name, "^[A-Z][A-Z0-9]*-[0-9]+$"))
                .Order(StringComparer.Ordinal)
                .ToList();
            logger.LogInformation("Offline: building the outputs from {Count} file(s) on disk.", keys.Count);
        }
        else
        {
            var current = await ReadUpdateTimesAsync(await ReadProjectKeysAsync());
            var stale = current.Where(c => !IsUpToDate(c.Key, c.Value)).Select(c => c.Key).ToList();
            logger.LogInformation("{Count} project issue(s) open or recently resolved; {Stale} new or changed since the last run.", current.Count, stale.Count);
            await DownloadChangelogsAsync(stale);
            keys = current.Keys.Order(StringComparer.Ordinal).ToList();
        }

        List<ProjectChangelog> results = keys.Select(key => ToProjectChangelog(ReadIssueFile(key))).ToList();
        await results.SaveAsJsonAsync(Constants.ChangelogsFileName);
        SaveWorkbook(results);
        logger.LogInformation("{Count} project issue(s) written to '{Json}' and '{Xlsx}'.",
            results.Count, Constants.ChangelogsFileName, Constants.ChangelogsWorkbookFileName);
        return results;
    }

    // The scoping query is the entry point; its parents are the project issues.
    private async Task<List<string>> ReadProjectKeysAsync()
    {
        var scopingIssues = await jiraClient.Search(settings.ProjectQuery, timeout: 20);
        var projectKeys = scopingIssues
            .Select(i => i.ParentKey)
            .Where(k => !string.IsNullOrEmpty(k))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();
        logger.LogInformation("{ScopingCount} scoping issue(s), {ProjectCount} project issue(s).", scopingIssues.Count, projectKeys.Count);
        return projectKeys;
    }

    // Light search: no changelog, only the fields needed to decide what to download.
    private async Task<Dictionary<string, DateTime>> ReadUpdateTimesAsync(List<string> projectKeys)
    {
        Dictionary<string, DateTime> updated = new(StringComparer.Ordinal);
        foreach (var batch in projectKeys.Chunk(BatchSize))
        {
            var jql = $"key in ({string.Join(", ", batch)}) AND (statusCategory != Done OR resolved >= startOfMonth(-{Constants.ChangelogClosedMonths}))";
            foreach (var issue in await SearchAllAsync(jql, expand: ""))
            {
                updated[issue.Value<string>("key")] = issue["fields"]!.Value<DateTime>("updated");
            }
        }

        return updated;
    }

    private async Task DownloadChangelogsAsync(List<string> keys)
    {
        var batches = keys.Chunk(BatchSize).ToList();
        for (int i = 0; i < batches.Count; i++)
        {
            var issues = await SearchAllAsync($"key in ({string.Join(", ", batches[i])})", expand: "changelog");
            foreach (var issue in issues)
            {
                WarnIfHistoryIsCut(issue);
                await File.WriteAllTextAsync(IssueFile(issue.Value<string>("key")), issue.ToString(Formatting.None));
            }

            logger.LogInformation("Changelogs {Index}/{Total}: {Count} issue(s) saved.", i + 1, batches.Count, issues.Count);
        }
    }

    private async Task<List<JToken>> SearchAllAsync(string jql, string expand)
    {
        List<JToken> issues = [];
        int startAt = 0;
        int total;
        do
        {
            var page = await jiraClient.SearchPage(jql, Fields, expand, startAt, BatchSize);
            total = page.Value<int>("total");
            var pageIssues = page["issues"] as JArray ?? [];
            issues.AddRange(pageIssues);
            if (pageIssues.Count == 0)
            {
                break;
            }

            startAt += pageIssues.Count;
        } while (startAt < total);

        return issues;
    }

    // Compared in UTC at second precision: the file's value has been through JSON, Jira's has not.
    private static bool IsUpToDate(string key, DateTime updated)
    {
        if (!File.Exists(IssueFile(key)))
        {
            return false;
        }

        var saved = ReadIssueFile(key)["fields"]?.Value<DateTime?>("updated");
        return saved is not null
            && saved.Value.ToUniversalTime().Ticks / TimeSpan.TicksPerSecond == updated.ToUniversalTime().Ticks / TimeSpan.TicksPerSecond;
    }

    private void WarnIfHistoryIsCut(JToken issue)
    {
        var histories = issue["changelog"]?["histories"] as JArray ?? [];
        var total = issue["changelog"]?.Value<int?>("total") ?? histories.Count;
        if (histories.Count < total)
        {
            logger.LogWarning("{Key}: got {Count} of {Total} history entries.", issue.Value<string>("key"), histories.Count, total);
        }
    }

    private static string IssueFile(string key) => Path.Combine(Constants.ChangelogIssueDirectory, $"{key}.json");

    private static JObject ReadIssueFile(string key) => JObject.Parse(File.ReadAllText(IssueFile(key)));

    private static ProjectChangelog ToProjectChangelog(JToken issue)
    {
        var fields = issue["fields"];
        var histories = issue["changelog"]?["histories"] as JArray ?? [];
        var key = issue.Value<string>("key");

        var changes = histories
            .SelectMany(h => (h["items"] as JArray ?? []).Select(item => (History: h, Item: item)))
            .Where(x => !s_droppedFields.Contains(x.Item.Value<string>("field") ?? ""))
            .Select(x => new FieldChange
            {
                Changed = FormatDate(x.History["created"]),
                Author = x.History["author"]?.Value<string>("displayName"),
                Field = x.Item.Value<string>("field"),
                From = Shorten(x.Item.Value<string>("fromString") ?? x.Item.Value<string>("from")),
                To = Shorten(x.Item.Value<string>("toString") ?? x.Item.Value<string>("to"))
            })
            .OrderBy(c => c.Changed, StringComparer.Ordinal)
            .ToList();

        return new ProjectChangelog
        {
            Key = key,
            Summary = fields?.Value<string>("summary"),
            Status = fields?["status"]?.Value<string>("name"),
            StatusCategory = fields?["status"]?["statusCategory"]?.Value<string>("key"),
            Resolved = FormatDate(fields?["resolutiondate"]),
            HistoryCount = histories.Count,
            Changes = changes
        };
    }

    // DLP on the Jira machine lets XLSX out but blocks CSV, so the readable copy is a workbook.
    private static void SaveWorkbook(List<ProjectChangelog> changelogs)
    {
        using var workbook = new XLWorkbook();

        var changes = workbook.Worksheets.Add("Changes");
        WriteHeader(changes, "IssueKey", "Changed", "Author", "Field", "From", "To");
        int row = 2;
        foreach (var issue in changelogs)
        {
            foreach (var change in issue.Changes)
            {
                changes.Cell(row, 1).Value = issue.Key;
                changes.Cell(row, 2).Value = change.Changed;
                changes.Cell(row, 3).Value = change.Author;
                changes.Cell(row, 4).Value = change.Field;
                changes.Cell(row, 5).Value = change.From;
                changes.Cell(row, 6).Value = change.To;
                row++;
            }
        }

        var issues = workbook.Worksheets.Add("Issues");
        WriteHeader(issues, "IssueKey", "Summary", "Status", "StatusCategory", "Resolved", "HistoryCount", "KeptChanges");
        for (int i = 0; i < changelogs.Count; i++)
        {
            var issue = changelogs[i];
            issues.Cell(i + 2, 1).Value = issue.Key;
            issues.Cell(i + 2, 2).Value = issue.Summary;
            issues.Cell(i + 2, 3).Value = issue.Status;
            issues.Cell(i + 2, 4).Value = issue.StatusCategory;
            issues.Cell(i + 2, 5).Value = issue.Resolved;
            issues.Cell(i + 2, 6).Value = issue.HistoryCount;
            issues.Cell(i + 2, 7).Value = issue.Changes.Count;
        }

        foreach (var sheet in new[] { changes, issues })
        {
            sheet.RangeUsed()?.SetAutoFilter();
            sheet.SheetView.FreezeRows(1);
        }

        workbook.SaveAs(Constants.ChangelogsWorkbookFileName);
    }

    private static void WriteHeader(IXLWorksheet sheet, params string[] headers)
    {
        for (int col = 0; col < headers.Length; col++)
        {
            var cell = sheet.Cell(1, col + 1);
            cell.Value = headers[col];
            cell.Style.Font.Bold = true;
            cell.Style.Fill.BackgroundColor = XLColor.LightGray;
        }
    }

    // JObject.Parse turns Jira's timestamps into DateTime (local time); ToString would then use the culture.
    private static string FormatDate(JToken token)
    {
        return token?.Type switch
        {
            JTokenType.Date => token.Value<DateTime>().ToString("yyyy-MM-dd HH:mm:ss"),
            JTokenType.String => token.Value<string>(),
            _ => null
        };
    }

    private static string Shorten(string value)
    {
        return value is { Length: > MaxValueLength } ? value[..MaxValueLength] + "…" : value;
    }
}
