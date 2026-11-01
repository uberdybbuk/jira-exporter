using ClosedXML.Excel;
using Microsoft.Extensions.Logging;

namespace FourArc.JiraExporter;

public class IssueChangelog
{
    public string Key { get; set; }

    // "project" (the parent that carries status and phase dates) or "scoping".
    public string Role { get; set; }

    public string ProjectKey { get; set; }
    public List<string> ScopingKeys { get; set; } = [];
    public string Summary { get; set; }
    public string Status { get; set; }
    public List<JiraClient.ChangelogEntry> Histories { get; set; } = [];
}

// Downloads the full change history of every work package the project query matches:
// the project issue and each of its scoping issues. Closed ones are included, since
// they show the whole path from scoping to production.
public class ChangelogExporter(ILoggerFactory loggerFactory, JiraSettings settings)
{
    private readonly ILogger<ChangelogExporter> logger = loggerFactory.CreateLogger<ChangelogExporter>();
    private readonly JiraClient jiraClient = new(loggerFactory.CreateLogger<JiraClient>(), settings);

    public async Task<List<IssueChangelog>> ExportAsync()
    {
        var scopingIssues = await jiraClient.Search(settings.ProjectQuery, timeout: 20);
        var issues = BuildIssueList(scopingIssues);
        logger.LogInformation("{ScopingCount} scoping issue(s), {Total} issue(s) to read in total.", scopingIssues.Count, issues.Count);

        List<IssueChangelog> results = [];
        List<string> failed = [];
        for (int i = 0; i < issues.Count; i++)
        {
            var issue = issues[i];
            logger.LogInformation("Changelog {Index}/{Total}: {Key} ({Role})", i + 1, issues.Count, issue.Key, issue.Role);

            try
            {
                var response = await jiraClient.DownloadIssueHistoryAsync(issue.Key);
                issue.Histories = response.Changelog?.Histories ?? [];

                // Jira Data Center returns the whole history here; warn if this one ever pages.
                if (response.Changelog is not null && issue.Histories.Count < response.Changelog.Total)
                {
                    logger.LogWarning("{Key}: got {Count} of {Total} history entries.", issue.Key, issue.Histories.Count, response.Changelog.Total);
                }

                results.Add(issue);
            }
            catch (Exception e)
            {
                // Some issues become invisible to this account once they close.
                logger.LogError(e, "Failed to read the changelog of {Key}.", issue.Key);
                failed.Add(issue.Key);
            }
        }

        Directory.CreateDirectory(Constants.RootDataDirectory);
        await results.SaveAsJsonAsync(Constants.ChangelogsFileName);
        SaveWorkbook(results);

        logger.LogInformation("Wrote {Count} changelog(s) to '{Json}' and '{Xlsx}'. Failed: {FailedCount} {Failed}",
            results.Count, Constants.ChangelogsFileName, Constants.ChangelogsWorkbookFileName, failed.Count, string.Join(", ", failed));
        return results;
    }

    // One entry per project issue (read once even when it has several scoping issues), then the scoping issues.
    private static List<IssueChangelog> BuildIssueList(List<JiraIssue> scopingIssues)
    {
        Dictionary<string, IssueChangelog> projects = new(StringComparer.Ordinal);
        List<IssueChangelog> scopings = [];

        foreach (var scoping in scopingIssues)
        {
            scopings.Add(new IssueChangelog
            {
                Key = scoping.Key,
                Role = "scoping",
                ProjectKey = scoping.ParentKey,
                Summary = scoping.Summary,
                Status = scoping.Status
            });

            if (string.IsNullOrEmpty(scoping.ParentKey))
            {
                continue;
            }

            if (!projects.TryGetValue(scoping.ParentKey, out var project))
            {
                project = new IssueChangelog { Key = scoping.ParentKey, Role = "project", ProjectKey = scoping.ParentKey };
                projects[scoping.ParentKey] = project;
            }

            project.ScopingKeys.Add(scoping.Key);
        }

        return [.. projects.Values, .. scopings];
    }

    // DLP on the Jira machine lets XLSX out but blocks CSV, so the readable copy is a workbook.
    // "Changes" has every changed field on its own row; "Issues" lists what was read.
    private static void SaveWorkbook(List<IssueChangelog> changelogs)
    {
        using var workbook = new XLWorkbook();

        var changes = workbook.Worksheets.Add("Changes");
        WriteHeader(changes, "IssueKey", "Role", "ProjectKey", "Changed", "Author", "Field", "FieldType", "From", "To");
        int row = 2;
        foreach (var issue in changelogs)
        {
            foreach (var history in issue.Histories.OrderBy(h => h.Created))
            {
                foreach (var item in history.Items)
                {
                    changes.Cell(row, 1).Value = issue.Key;
                    changes.Cell(row, 2).Value = issue.Role;
                    changes.Cell(row, 3).Value = issue.ProjectKey;
                    changes.Cell(row, 4).Value = history.Created.ToString("yyyy-MM-dd HH:mm:ss zzz");
                    changes.Cell(row, 5).Value = history.Author?.DisplayName;
                    changes.Cell(row, 6).Value = item.Field;
                    changes.Cell(row, 7).Value = item.FieldType;
                    changes.Cell(row, 8).Value = CellText(item.FromString ?? item.From);
                    changes.Cell(row, 9).Value = CellText(item.ToStringValue ?? item.To);
                    row++;
                }
            }
        }

        var issues = workbook.Worksheets.Add("Issues");
        WriteHeader(issues, "IssueKey", "Role", "ProjectKey", "ScopingKeys", "Summary", "Status", "HistoryCount");
        for (int i = 0; i < changelogs.Count; i++)
        {
            var issue = changelogs[i];
            issues.Cell(i + 2, 1).Value = issue.Key;
            issues.Cell(i + 2, 2).Value = issue.Role;
            issues.Cell(i + 2, 3).Value = issue.ProjectKey;
            issues.Cell(i + 2, 4).Value = string.Join(", ", issue.ScopingKeys);
            issues.Cell(i + 2, 5).Value = issue.Summary;
            issues.Cell(i + 2, 6).Value = issue.Status;
            issues.Cell(i + 2, 7).Value = issue.Histories.Count;
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

    // An Excel cell holds at most 32767 characters; long description edits are cut.
    private static string CellText(string value)
    {
        const int MaxLength = 32767;
        return value is { Length: > MaxLength } ? value[..MaxLength] : value;
    }
}
