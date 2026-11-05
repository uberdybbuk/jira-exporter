using ClosedXML.Excel;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;

namespace FourArc.JiraExporter;

// What the last completed export contained: the 'updated' time of every project and scoping issue it wrote.
// Written only after the workbook is saved, so an interrupted or failed run leaves the next one to report the
// same changes.
public class ExportState
{
    public DateTime? ExportedAt { get; set; }
    public Dictionary<string, DateTime> Projects { get; set; } = [];
    public Dictionary<string, DateTime> Scopings { get; set; } = [];
    public List<string> WorkPackages { get; set; } = [];
}

// A project issue as downloaded, with its status and date field changes. Kept on disk so an interrupted run
// does not download it again while its 'updated' time stays the same.
public class ProjectSnapshot
{
    public DateTime Updated { get; set; }
    public JiraIssue Issue { get; set; }
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

// Everything taken from Jira in one workbook for Horizon: Meta, Report (work packages), Relations, Changes
// (status and date field history of the project issues) and Removed. Only data: what a status or a date means
// is decided by Horizon.
//
// Full writes every work package the query returns, closed ones included. Incremental writes the work packages
// whose project or scoping issue changed since the last export, plus the ones that left the query. Each run
// writes a new timestamped file and Meta carries the previous export's time, so the reader can tell when a file
// in between was skipped.
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
        var previous = File.Exists(Constants.ExportStateFileName)
            ? await JsonHelper.FromJsonAsync<ExportState>(Constants.ExportStateFileName)
            : new ExportState();
        if (!full && previous.ExportedAt is null)
        {
            logger.LogInformation("No previous export found; running a full export.");
            full = true;
        }

        var dateFields = await ReadDateFieldsAsync();
        var scopings = await jiraClient.Search(settings.ProjectQuery, timeout: 20, customFieldsToInclude: ["estimation", "salesForceBudget"]);
        var withParent = scopings.Where(s => !string.IsNullOrEmpty(s.ParentKey)).ToList();
        var projectUpdated = await ReadUpdateTimesAsync(withParent.Select(s => s.ParentKey).Distinct().ToList());
        var unreadable = withParent.Where(s => !projectUpdated.ContainsKey(s.ParentKey)).Select(s => s.ParentKey).Distinct().ToList();
        if (unreadable.Count > 0)
        {
            // Retried next time: they stay out of the state, so they still count as changed.
            logger.LogWarning("{Count} project issue(s) did not come back from the key search: {Keys}", unreadable.Count, string.Join(", ", unreadable));
        }

        var changed = withParent
            .Where(s => projectUpdated.ContainsKey(s.ParentKey))
            .Where(s => full || HasMoved(previous.Scopings, s.Key, s.Updated) || HasMoved(previous.Projects, s.ParentKey, projectUpdated[s.ParentKey]))
            .ToList();
        logger.LogInformation("{Mode}: {Changed} of {Total} work package(s) to export.", full ? "Full" : "Incremental", changed.Count, withParent.Count);

        var projects = await LoadProjectsAsync(changed.Select(s => s.ParentKey).Distinct().ToList(), projectUpdated, dateFields);
        var workPackages = changed
            .Where(s => projects.ContainsKey(s.ParentKey))
            .Select(s => new WorkPackage(projects[s.ParentKey].Issue, s))
            .ToList();
        var current = withParent.Select(s => $"{s.ParentKey}_{s.Key}").ToHashSet(StringComparer.Ordinal);
        List<string> removed = full ? [] : previous.WorkPackages.Where(id => !current.Contains(id)).ToList();

        var file = SaveWorkbook(full, startedAt, previous.ExportedAt, workPackages, projects, removed);
        // Sent before the state is saved: if the mail fails the next run exports these changes again.
        if (sendTo is not null)
        {
            var html = FillEmailTemplate(emailTemplate, file, full, startedAt, previous.ExportedAt, workPackages.Count, removed.Count);
            ExchangeEmailService.SendHtmlWithAttachment(sendTo.ExportRecipient, Path.GetFileName(file), html, file, sendTo.AutodiscoverAddress);
            logger.LogInformation("Sent '{File}' to {Recipient}", Path.GetFileName(file), sendTo.ExportRecipient);
        }

        await SaveStateAsync(previous, full, startedAt, workPackages, projects, current);
        logger.LogInformation("Wrote '{File}': {WorkPackages} work package(s), {Removed} removed. Failed project issue(s): {Failed}.",
            file, workPackages.Count, removed.Count, changed.Count(s => !projects.ContainsKey(s.ParentKey)));
    }

    private static string FillEmailTemplate(string template, string file, bool full, DateTime startedAt, DateTime? previousExportAt, int workPackages, int removed)
    {
        const string TimeFormat = "dd.MM.yyyy HH:mm:ss";
        return template
            .Replace("{{FileName}}", System.Net.WebUtility.HtmlEncode(Path.GetFileName(file)))
            .Replace("{{Mode}}", full ? "Full" : "Incremental")
            .Replace("{{ExportedAt}}", startedAt.ToString(TimeFormat))
            .Replace("{{PreviousExportAt}}", !full && previousExportAt is DateTime previous ? previous.ToString(TimeFormat) : "-")
            .Replace("{{WorkPackageCount}}", workPackages.ToString())
            .Replace("{{RemovedCount}}", removed.ToString());
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

    // Light search: only 'updated', batched so the key list stays within URL limits.
    private async Task<Dictionary<string, DateTime>> ReadUpdateTimesAsync(List<string> projectKeys)
    {
        Dictionary<string, DateTime> updated = new(StringComparer.Ordinal);
        foreach (var batch in projectKeys.Chunk(KeyBatchSize))
        {
            var jql = $"key in ({string.Join(", ", batch)})";
            int startAt = 0;
            int total;
            do
            {
                var page = await jiraClient.SearchPage(jql, "updated", expand: "", startAt, KeyBatchSize);
                total = page.Value<int>("total");
                var issues = page["issues"] as JArray ?? [];
                foreach (var issue in issues)
                {
                    updated[issue.Value<string>("key")] = issue["fields"]!.Value<DateTime>("updated");
                }

                if (issues.Count == 0)
                {
                    break;
                }

                startAt += issues.Count;
            } while (startAt < total);
        }

        return updated;
    }

    // From disk when the snapshot is as recent as Jira's 'updated', otherwise downloaded with its changelog.
    // A project that fails is left out of this export and retried by the next one.
    private async Task<Dictionary<string, ProjectSnapshot>> LoadProjectsAsync(
        List<string> keys, Dictionary<string, DateTime> projectUpdated, HashSet<string> dateFields)
    {
        Directory.CreateDirectory(Constants.ExportCacheDirectory);
        var customFields = JiraIssue.GetCustomFields();
        Dictionary<string, ProjectSnapshot> projects = new(StringComparer.Ordinal);

        for (int i = 0; i < keys.Count; i++)
        {
            var key = keys[i];
            var file = Path.Combine(Constants.ExportCacheDirectory, $"{key}.json");
            var updated = projectUpdated[key];
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

    private string SaveWorkbook(bool full, DateTime startedAt, DateTime? previousExportAt, List<WorkPackage> workPackages,
        Dictionary<string, ProjectSnapshot> projects, List<string> removed)
    {
        using var workbook = new XLWorkbook();
        var exportedProjects = workPackages.Select(w => w.ProjectTask.Key).Distinct(StringComparer.Ordinal).ToList();
        var changeRows = exportedProjects.Sum(k => projects[k].Changes.Count);

        AddMetaSheet(workbook,
        [
            ("ExportedAt", startedAt.ToString("yyyy-MM-dd HH:mm:ss")),
            ("Mode", full ? "Full" : "Incremental"),
            ("PreviousExportAt", full ? "" : previousExportAt?.ToString("yyyy-MM-dd HH:mm:ss") ?? ""),
            ("ProjectQuery", settings.ProjectQuery),
            ("WorkPackages", workPackages.Count.ToString()),
            ("ProjectIssues", exportedProjects.Count.ToString()),
            ("ChangeRows", changeRows.ToString()),
            ("Removed", removed.Count.ToString())
        ]);
        ExcelReportGenerator.AddReportSheet(workbook, workPackages);
        ExcelReportGenerator.AddRelationsSheet(workbook, ReportRelations.Build(workPackages), ChangeReportHtml.ToBrowseUrl(settings.BaseApiUrl));
        AddChangesSheet(workbook, exportedProjects.Select(k => (k, projects[k].Changes)));
        AddRemovedSheet(workbook, removed);

        Directory.CreateDirectory(Constants.ExportDirectory);
        var file = Path.Combine(Constants.ExportDirectory, $"projects-{startedAt:yyyyMMdd-HHmmss}-{(full ? "full" : "incremental")}.xlsx");
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

    // Work packages in the previous export that the query no longer returns (deleted, moved, or no longer
    // visible to this account).
    private static void AddRemovedSheet(XLWorkbook workbook, List<string> removed)
    {
        var sheet = workbook.Worksheets.Add("Removed");
        WriteHeader(sheet, "ProjectKey", "ProposalScopingKey");
        for (int i = 0; i < removed.Count; i++)
        {
            var parts = removed[i].Split('_', 2);
            sheet.Cell(i + 2, 1).Value = parts[0];
            sheet.Cell(i + 2, 2).Value = parts.Length > 1 ? parts[1] : "";
        }
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

    // Exported entries get their new times; the ones that failed keep the old ones and are retried next time.
    private static async Task SaveStateAsync(ExportState previous, bool full, DateTime startedAt, List<WorkPackage> workPackages,
        Dictionary<string, ProjectSnapshot> projects, HashSet<string> current)
    {
        var state = new ExportState
        {
            ExportedAt = startedAt,
            Projects = full ? [] : new(previous.Projects),
            Scopings = full ? [] : new(previous.Scopings),
            WorkPackages = current.Order(StringComparer.Ordinal).ToList()
        };
        foreach (var workPackage in workPackages)
        {
            state.Projects[workPackage.ProjectTask.Key] = projects[workPackage.ProjectTask.Key].Updated;
            state.Scopings[workPackage.ProposalScopingTask.Key] = workPackage.ProposalScopingTask.Updated;
        }

        await state.SaveAsJsonAsync(Constants.ExportStateFileName);
    }

    private static bool HasMoved(Dictionary<string, DateTime> known, string key, DateTime updated)
    {
        return !known.TryGetValue(key, out var previous) || HasMoved(previous, updated);
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
