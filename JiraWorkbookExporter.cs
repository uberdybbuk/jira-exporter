using ClosedXML.Excel;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;

namespace FourArc.JiraExporter;

// A project issue as downloaded, with its status and date field changes. Kept on disk so an interrupted run
// does not download it again while its 'updated' time stays the same.
public class ProjectSnapshot
{
    public DateTime Updated { get; set; }
    public JiraIssue Issue { get; set; }
    public List<FieldChange> Changes { get; set; } = [];
}

// What the light search returns for a project issue. A cancelled issue may have no resolution date; its last update
// stands in, which at worst keeps it in the file a little longer.
public record ProjectActivity(DateTime Updated, string Status, string StatusCategory, DateTime? Resolved)
{
    private bool IsClosed => StatusCategory == "done" || Status is "Done" or "Cancelled";

    public bool IsActiveSince(DateTime closedSince) => !IsClosed || (Resolved ?? Updated) >= closedSince;
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

// Everything taken from Jira in one workbook for Horizon: Meta, Report (work packages), Relations and Changes (status
// and date field history of the project issues). Only data: what a status or a date means is decided by Horizon.
//
// Every file stands on its own, nothing depends on an earlier run: a lost file is made good by the next one. Active,
// the daily file, holds the open projects and the ones closed in the last few days, so Horizon also sees a project
// close. Full holds everything the query returns and is for the first load or after a long gap.
public class JiraWorkbookExporter(ILoggerFactory loggerFactory, JiraSettings settings)
{
    private const int KeyBatchSize = 100;
    private const string EmailTemplateFileName = "export-email.html";

    private readonly ILogger<JiraWorkbookExporter> logger = loggerFactory.CreateLogger<JiraWorkbookExporter>();
    private readonly JiraClient jiraClient = new(loggerFactory.CreateLogger<JiraClient>(), settings);

    public async Task ExportAsync(bool full, EmailSettings sendTo = null)
    {
        // Checked first: a missing address or template should stop the run, not surface after a long download.
        string emailTemplate = null;
        if (sendTo is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(sendTo.ExportRecipient, "Email.ExportRecipient");
            emailTemplate = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, EmailTemplateFileName));
        }

        var startedAt = DateTime.Now;
        var dateFields = await ReadDateFieldsAsync();
        var scopings = await jiraClient.Search(settings.ProjectQuery, timeout: 20, customFieldsToInclude: ["estimation", "salesForceBudget"]);
        var withParent = scopings.Where(s => !string.IsNullOrEmpty(s.ParentKey)).ToList();
        var activity = await ReadProjectActivityAsync(withParent.Select(s => s.ParentKey).Distinct().ToList());
        var unreadable = withParent.Where(s => !activity.ContainsKey(s.ParentKey)).Select(s => s.ParentKey).Distinct().ToList();
        if (unreadable.Count > 0)
        {
            logger.LogWarning("{Count} project issue(s) did not come back from the key search: {Keys}", unreadable.Count, string.Join(", ", unreadable));
        }

        var closedSince = startedAt.AddDays(-Constants.ActiveExportClosedDays);
        var selected = withParent
            .Where(s => activity.TryGetValue(s.ParentKey, out var project) && (full || project.IsActiveSince(closedSince)))
            .ToList();
        logger.LogInformation("{Mode}: {Selected} of {Total} work package(s) to export.", full ? "Full" : "Active", selected.Count, withParent.Count);

        var projects = await LoadProjectsAsync(selected.Select(s => s.ParentKey).Distinct().ToList(), activity, dateFields);
        var workPackages = selected
            .Where(s => projects.ContainsKey(s.ParentKey))
            .Select(s => new WorkPackage(projects[s.ParentKey].Issue, s))
            .ToList();

        var file = SaveWorkbook(full, startedAt, workPackages, projects);
        if (sendTo is not null)
        {
            var html = FillEmailTemplate(emailTemplate, file, full, startedAt, workPackages.Count);
            ExchangeEmailService.SendHtmlWithAttachment(sendTo.ExportRecipient, Path.GetFileName(file), html, file, sendTo.AutodiscoverAddress);
            logger.LogInformation("Sent '{File}' to {Recipient}", Path.GetFileName(file), sendTo.ExportRecipient);
        }

        logger.LogInformation("Wrote '{File}': {WorkPackages} work package(s). Failed project issue(s): {Failed}.",
            file, workPackages.Count, selected.Count(s => !projects.ContainsKey(s.ParentKey)));
    }

    private static string FillEmailTemplate(string template, string file, bool full, DateTime startedAt, int workPackages)
    {
        return template
            .Replace("{{FileName}}", System.Net.WebUtility.HtmlEncode(Path.GetFileName(file)))
            .Replace("{{Mode}}", full ? "Full" : "Active")
            .Replace("{{ExportedAt}}", startedAt.ToString("dd.MM.yyyy HH:mm:ss"))
            .Replace("{{WorkPackageCount}}", workPackages.ToString());
    }

    // Names and ids of the date and datetime fields; their changes go to the Changes sheet with the status.
    private async Task<HashSet<string>> ReadDateFieldsAsync()
    {
        var fields = await new JiraFieldDumper(loggerFactory.CreateLogger<JiraFieldDumper>(), settings).GetAllFieldsAsync();
        return fields
            .Where(f => f.SchemaType is "date" or "datetime")
            .SelectMany(f => new[] { f.Id, f.Name })
            .Where(n => !string.IsNullOrEmpty(n))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    // Light search: only what decides whether a project goes in the file, batched so the key list stays within URL limits.
    private async Task<Dictionary<string, ProjectActivity>> ReadProjectActivityAsync(List<string> projectKeys)
    {
        Dictionary<string, ProjectActivity> activity = new(StringComparer.Ordinal);
        foreach (var batch in projectKeys.Chunk(KeyBatchSize))
        {
            var jql = $"key in ({string.Join(", ", batch)})";
            int startAt = 0;
            int total;
            do
            {
                var page = await jiraClient.SearchPage(jql, "updated,status,resolutiondate", expand: "", startAt, KeyBatchSize);
                total = page.Value<int>("total");
                var issues = page["issues"] as JArray ?? [];
                foreach (var issue in issues)
                {
                    var fields = issue["fields"]!;
                    activity[issue.Value<string>("key")] = new ProjectActivity(
                        fields.Value<DateTime>("updated"),
                        fields["status"]?.Value<string>("name"),
                        fields["status"]?["statusCategory"]?.Value<string>("key"),
                        fields.Value<DateTime?>("resolutiondate"));
                }

                if (issues.Count == 0)
                {
                    break;
                }

                startAt += issues.Count;
            } while (startAt < total);
        }

        return activity;
    }

    // From disk when the snapshot is as recent as Jira's 'updated', otherwise downloaded with its changelog.
    // A project that fails is left out of this file and retried by the next one.
    private async Task<Dictionary<string, ProjectSnapshot>> LoadProjectsAsync(
        List<string> keys, Dictionary<string, ProjectActivity> activity, HashSet<string> dateFields)
    {
        Directory.CreateDirectory(Constants.ExportCacheDirectory);
        var customFields = JiraIssue.GetCustomFields();
        Dictionary<string, ProjectSnapshot> projects = new(StringComparer.Ordinal);

        for (int i = 0; i < keys.Count; i++)
        {
            var key = keys[i];
            var file = Path.Combine(Constants.ExportCacheDirectory, $"{key}.json");
            var updated = activity[key].Updated;
            if (File.Exists(file))
            {
                var cached = await JsonHelper.FromJsonAsync<ProjectSnapshot>(file);
                if (!HasMoved(cached.Updated, updated))
                {
                    projects[key] = cached;
                    continue;
                }
            }

            logger.LogInformation("Project issue {Index}/{Total}: {Key}", i + 1, keys.Count, key);
            try
            {
                var fetched = await jiraClient.GetIssueWithChangelog(key, customFields);
                if (fetched is not { } result)
                {
                    logger.LogWarning("Project issue {Key} came back without fields.", key);
                    continue;
                }

                var snapshot = new ProjectSnapshot { Updated = updated, Issue = result.Issue, Changes = ReadChanges(result.Changelog, dateFields) };
                await snapshot.SaveAsJsonAsync(file);
                projects[key] = snapshot;
            }
            catch (Exception e)
            {
                logger.LogError(e, "Failed to read project issue {Key}.", key);
            }
        }

        return projects;
    }

    // Status and date field changes only. Status values are names; date values are taken raw ("2026-09-02",
    // "2026-02-12 13:39:09") because Jira's display text for older entries is "6/May/24".
    private static List<FieldChange> ReadChanges(JToken changelog, HashSet<string> dateFields)
    {
        return (changelog?["histories"] as JArray ?? [])
            .SelectMany(h => (h["items"] as JArray ?? []).Select(item => (History: h, Item: item)))
            .Select(x => (x.History, x.Item, Field: x.Item.Value<string>("field") ?? ""))
            .Where(x => x.Field == "status" || dateFields.Contains(x.Field))
            .Select(x =>
            {
                var isStatus = x.Field == "status";
                return new FieldChange
                {
                    Changed = FormatDate(x.History["created"]),
                    Author = x.History["author"]?.Value<string>("displayName"),
                    Field = x.Field,
                    From = isStatus ? x.Item.Value<string>("fromString") : RawValue(x.Item["from"]) ?? x.Item.Value<string>("fromString"),
                    To = isStatus ? x.Item.Value<string>("toString") : RawValue(x.Item["to"]) ?? x.Item.Value<string>("toString")
                };
            })
            .OrderBy(c => c.Changed, StringComparer.Ordinal)
            .ToList();
    }

    private string SaveWorkbook(bool full, DateTime startedAt, List<WorkPackage> workPackages, Dictionary<string, ProjectSnapshot> projects)
    {
        using var workbook = new XLWorkbook();
        var exportedProjects = workPackages.Select(w => w.ProjectTask.Key).Distinct(StringComparer.Ordinal).ToList();
        var changeRows = exportedProjects.Sum(k => projects[k].Changes.Count);

        AddMetaSheet(workbook,
        [
            ("ExportedAt", startedAt.ToString("yyyy-MM-dd HH:mm:ss")),
            ("Mode", full ? "Full" : "Active"),
            ("ProjectQuery", settings.ProjectQuery),
            ("WorkPackages", workPackages.Count.ToString()),
            ("ProjectIssues", exportedProjects.Count.ToString()),
            ("ChangeRows", changeRows.ToString())
        ]);
        ExcelReportGenerator.AddReportSheet(workbook, workPackages);
        ExcelReportGenerator.AddRelationsSheet(workbook, ReportRelations.Build(workPackages), ChangeReportHtml.ToBrowseUrl(settings.BaseApiUrl));
        AddChangesSheet(workbook, exportedProjects.Select(k => (k, projects[k].Changes)));

        Directory.CreateDirectory(Constants.ExportDirectory);
        var file = Path.Combine(Constants.ExportDirectory, $"projects-{startedAt:yyyyMMdd-HHmmss}-{(full ? "full" : "active")}.xlsx");
        workbook.SaveAs(file);
        return file;
    }

    private static void AddMetaSheet(XLWorkbook workbook, (string Key, string Value)[] rows)
    {
        var sheet = workbook.Worksheets.Add("Meta");
        WriteHeader(sheet, "Key", "Value");
        for (int i = 0; i < rows.Length; i++)
        {
            sheet.Cell(i + 2, 1).Value = rows[i].Key;
            sheet.Cell(i + 2, 2).Value = rows[i].Value;
        }

        sheet.Columns().AdjustToContents();
    }

    // The complete status and date field history of each exported project issue; the reader replaces a project's
    // rows wholesale. Every value is text.
    private static void AddChangesSheet(XLWorkbook workbook, IEnumerable<(string ProjectKey, List<FieldChange> Changes)> projects)
    {
        var sheet = workbook.Worksheets.Add("Changes");
        WriteHeader(sheet, "ProjectKey", "Changed", "Author", "Field", "From", "To");
        int row = 2;
        foreach (var (projectKey, changes) in projects)
        {
            foreach (var change in changes)
            {
                sheet.Cell(row, 1).Value = projectKey;
                sheet.Cell(row, 2).Value = change.Changed;
                sheet.Cell(row, 3).Value = change.Author;
                sheet.Cell(row, 4).Value = change.Field;
                sheet.Cell(row, 5).Value = change.From;
                sheet.Cell(row, 6).Value = change.To;
                row++;
            }
        }

        sheet.RangeUsed()?.SetAutoFilter();
        sheet.SheetView.FreezeRows(1);
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

    // Compared in UTC at second precision: one side has been through JSON, the other has not.
    private static bool HasMoved(DateTime previous, DateTime current)
    {
        return previous.ToUniversalTime().Ticks / TimeSpan.TicksPerSecond != current.ToUniversalTime().Ticks / TimeSpan.TicksPerSecond;
    }

    // A date field's raw value. Date-only values stay text ("2026-09-02"); datetime values arrive as ISO text that
    // JObject.Parse has already turned into DateTime, and reading them back as string would use the machine's
    // culture ("02/12/2026 13:39:09"), so they are written as "yyyy-MM-dd HH:mm:ss" in local time.
    private static string RawValue(JToken token)
    {
        return token?.Type switch
        {
            JTokenType.Date => token.Value<DateTime>().ToString("yyyy-MM-dd HH:mm:ss"),
            JTokenType.String => token.Value<string>(),
            _ => null
        };
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
}
