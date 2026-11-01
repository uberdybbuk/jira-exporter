using System.Security.Cryptography;
using System.Text;
using ClosedXML.Excel;
using Microsoft.Extensions.Logging;
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
// the noisy fields are dropped here.
//
// Issues are read in batches through search. Each batch is written to disk as soon as it arrives, so an
// interrupted run picks up where it stopped. Delete data/changelogs to read everything again.
public class ChangelogExporter(ILoggerFactory loggerFactory, JiraSettings settings)
{
    private const int BatchSize = 50;
    private const int MaxValueLength = 200;

    private static readonly HashSet<string> s_droppedFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "description", "summary", "Comment", "Attachment", "Link", "RemoteIssueLink", "labels", "Rank",
        "Worklog", "WorklogId", "timespent", "timeestimate", "timeoriginalestimate"
    };

    private readonly ILogger<ChangelogExporter> logger = loggerFactory.CreateLogger<ChangelogExporter>();
    private readonly JiraClient jiraClient = new(loggerFactory.CreateLogger<JiraClient>(), settings);

    public async Task<List<ProjectChangelog>> ExportAsync()
    {
        var scopingIssues = await jiraClient.Search(settings.ProjectQuery, timeout: 20);
        var projectKeys = scopingIssues
            .Select(i => i.ParentKey)
            .Where(k => !string.IsNullOrEmpty(k))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();
        var batches = projectKeys.Chunk(BatchSize).ToList();
        logger.LogInformation("{ScopingCount} scoping issue(s), {ProjectCount} project issue(s), {BatchCount} batch(es).",
            scopingIssues.Count, projectKeys.Count, batches.Count);

        Directory.CreateDirectory(Constants.ChangelogBatchDirectory);
        List<ProjectChangelog> results = [];
        for (int i = 0; i < batches.Count; i++)
        {
            results.AddRange(await ReadBatchAsync(batches[i], i + 1, batches.Count));
        }

        await results.SaveAsJsonAsync(Constants.ChangelogsFileName);
        SaveWorkbook(results);
        logger.LogInformation("{Count} project issue(s) written to '{Json}' and '{Xlsx}'.",
            results.Count, Constants.ChangelogsFileName, Constants.ChangelogsWorkbookFileName);
        return results;
    }

    // The file name carries a hash of the batch's keys: when the query's result changes, the batches
    // that changed are read again and the rest come from disk.
    private async Task<List<ProjectChangelog>> ReadBatchAsync(string[] keys, int index, int total)
    {
        var file = Path.Combine(Constants.ChangelogBatchDirectory, $"batch-{Hash(keys)}.json");
        if (File.Exists(file))
        {
            logger.LogInformation("Batch {Index}/{Total}: from disk.", index, total);
            return await JsonHelper.FromJsonAsync<List<ProjectChangelog>>(file);
        }

        var jql = $"key in ({string.Join(", ", keys)}) AND (statusCategory != Done OR resolved >= startOfMonth(-{Constants.ChangelogClosedMonths}))";
        List<ProjectChangelog> batch = [];
        int startAt = 0;
        int found;
        do
        {
            var page = await jiraClient.SearchWithChangelogPage(jql, "summary,status,resolutiondate", startAt, BatchSize);
            found = page.Value<int>("total");
            var issues = page["issues"] as JArray ?? [];
            batch.AddRange(issues.Select(ToProjectChangelog));
            if (issues.Count == 0)
            {
                break;
            }

            startAt += issues.Count;
        } while (startAt < found);

        await batch.SaveAsJsonAsync(file);
        logger.LogInformation("Batch {Index}/{Total}: {Count} of {KeyCount} project issue(s) open or recently resolved.",
            index, total, batch.Count, keys.Length);
        return batch;
    }

    private ProjectChangelog ToProjectChangelog(JToken issue)
    {
        var fields = issue["fields"];
        var histories = issue["changelog"]?["histories"] as JArray ?? [];
        var key = issue.Value<string>("key");

        var historyTotal = issue["changelog"]?.Value<int?>("total") ?? histories.Count;
        if (histories.Count < historyTotal)
        {
            logger.LogWarning("{Key}: got {Count} of {Total} history entries.", key, histories.Count, historyTotal);
        }

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

    private static string Hash(string[] keys)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(string.Join(",", keys)));
        return Convert.ToHexString(bytes)[..12].ToLowerInvariant();
    }
}
