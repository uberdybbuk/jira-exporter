using System.Globalization;
using ClosedXML.Excel;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;

namespace FourArc.JiraExporter;

// Writes the issues of Jira.RequestQuery to one workbook. Unlike 'export' there are no work packages here: the query
// returns the request issues themselves.
//
// Requests: one row per issue, with the columns of the Jira issue navigator view the query is used with, plus the
// estimation total. Estimates: one row per person per issue, read from Estimation Details. By Person: a pivot table
// over Estimates, so per-person totals follow whatever filter is set in Excel.
public class RequestExporter(ILoggerFactory loggerFactory, JiraSettings settings)
{
    private const int PageSize = 500;

    private enum ColumnKind
    {
        Text,
        Key,
        DateTime,
        Number,
    }

    // Field is a logical name from Jira.Fields or a standard Jira field.
    private sealed record Column(string Header, string Field, ColumnKind Kind = ColumnKind.Text);

    private static readonly Column[] s_columns =
    [
        new("T", "issuetype"),
        new("Key", "key", ColumnKind.Key),
        new("Summary", "summary"),
        new("Assignee", "assignee"),
        new("P", "priority"),
        new("Status", "status"),
        new("Resolution", "resolution"),
        new("Project", "project"),
        new("Created", "created", ColumnKind.DateTime),
        new("Team", "team"),
        new("T-Shirt Size", "tShirtSize"),
        new("Category", "category"),
        new("Level-3 Team", "level3Team"),
        new("Company", "company"),
        new("Access Group", "accessGroup"),
        new("Salesforce Budget", "salesForceBudget", ColumnKind.Number),
        new("Estimation Details", "estimationDetails"),
    ];

    // Request columns repeated on every Estimates row, so the pivot can filter by them.
    private static readonly string[] s_estimateContextColumns = ["Project", "Company", "Team", "T-Shirt Size", "Status"];

    // Custom fields have no fixed id, so they must be mapped; an unmapped one would silently come back empty.
    private static readonly string[] s_customFields =
        ["team", "tShirtSize", "category", "level3Team", "company", "accessGroup", "salesForceBudget", "estimationDetails"];

    // Wider columns are capped and wrapped, so a long summary or estimation text does not stretch the sheet.
    private const double MaxColumnWidth = 60;

    private readonly ILogger<RequestExporter> logger = loggerFactory.CreateLogger<RequestExporter>();
    private readonly JiraClient jiraClient = new(loggerFactory.CreateLogger<JiraClient>(), settings);

    // Returns what is wrong with the settings, or null when the export can run.
    public string FindProblem(string jql)
    {
        if (string.IsNullOrWhiteSpace(jql))
        {
            return "Jira.RequestQuery is empty. Set it in appsettings.local.json, or pass the query with --jql \"...\".";
        }

        var unmapped = s_customFields.Where(f => !settings.IsMappedField(f)).ToList();
        return unmapped.Count > 0
            ? $"Jira.Fields has no mapping for: {string.Join(", ", unmapped)}. Add them to appsettings.local.json."
            : null;
    }

    public async Task<string> ExportAsync(string jql)
    {
        var startedAt = DateTime.Now;
        var issues = await SearchAsync(jql);

        Directory.CreateDirectory(Constants.ExportDirectory);
        var file = Path.Combine(Constants.ExportDirectory, $"requests-{startedAt:yyyyMMdd-HHmmss}.xlsx");
        SaveWorkbook(file, issues, ChangeReportHtml.ToBrowseUrl(settings.BaseApiUrl));

        logger.LogInformation("Wrote '{File}': {Count} issue(s).", file, issues.Count);
        return file;
    }

    private async Task<List<JObject>> SearchAsync(string jql)
    {
        var fields = string.Join(",", s_columns.Select(c => settings.ResolveField(c.Field)).Distinct());
        List<JObject> issues = [];
        int startAt = 0;
        int total;

        do
        {
            var page = await jiraClient.SearchPage(jql, fields, expand: "", startAt, PageSize);
            total = page.Value<int>("total");
            if (page["issues"] is not JArray pageIssues || pageIssues.Count == 0)
            {
                break;
            }

            issues.AddRange(pageIssues.OfType<JObject>());

            // Advance by what came back: the server may cap maxResults below PageSize.
            startAt += pageIssues.Count;
            logger.LogInformation("Read {Read} of {Total} issue(s).", startAt, total);
        } while (startAt < total);

        return issues;
    }

    // Internal so it can be run on sample issues without Jira.
    internal void SaveWorkbook(string file, List<JObject> issues, string browseUrl)
    {
        var estimations = issues
            .Select(i => EstimationDetails.Parse(FieldText(i, settings.ResolveField("estimationDetails"))))
            .ToList();

        using var workbook = new XLWorkbook();
        var requests = AddRequestsSheet(workbook, issues, estimations, browseUrl);
        var estimates = AddEstimatesSheet(workbook, requests, estimations);
        if (estimates is not null)
        {
            AddByPersonSheet(workbook, estimates);
        }

        foreach (var (issue, estimation) in issues.Zip(estimations).Where(p => p.Second.Problems.Count > 0))
        {
            logger.LogWarning("{Key}: Estimation Details line(s) not read as 'name : man-days': {Lines}",
                issue.Value<string>("key"), string.Join(" | ", estimation.Problems));
        }

        workbook.SaveAs(file);
    }

    private IXLWorksheet AddRequestsSheet(XLWorkbook workbook, List<JObject> issues, List<EstimationDetails> estimations, string browseUrl)
    {
        var sheet = workbook.Worksheets.Add("Requests");
        string[] headers = [.. s_columns.Select(c => c.Header), "Estimation Total", "Estimation Problems"];
        WriteHeader(sheet, headers);

        for (int row = 0; row < issues.Count; row++)
        {
            for (int col = 0; col < s_columns.Length; col++)
            {
                WriteCell(sheet.Cell(row + 2, col + 1), issues[row], s_columns[col], browseUrl);
            }

            var estimation = estimations[row];
            if (estimation.Estimates.Count > 0)
            {
                sheet.Cell(row + 2, s_columns.Length + 1).Value = estimation.Total;
            }

            if (estimation.Problems.Count > 0)
            {
                sheet.Cell(row + 2, s_columns.Length + 2).Value = string.Join("\n", estimation.Problems);
            }
        }

        FinishSheet(sheet);
        return sheet;
    }

    // One row per person per request. Returns null when no request has an estimate, since a pivot needs data rows.
    private static IXLTable AddEstimatesSheet(XLWorkbook workbook, IXLWorksheet requests, List<EstimationDetails> estimations)
    {
        if (estimations.All(e => e.Estimates.Count == 0))
        {
            return null;
        }

        var sheet = workbook.Worksheets.Add("Estimates");
        string[] headers = ["Key", "Person", "Man-Days", "Created", "Created Year", "Created Month", .. s_estimateContextColumns];
        WriteHeader(sheet, headers);

        var keyColumn = RequestColumn("Key");
        var createdColumn = RequestColumn("Created");
        var contextColumns = s_estimateContextColumns.Select(RequestColumn).ToArray();

        int row = 2;
        for (int i = 0; i < estimations.Count; i++)
        {
            var requestRow = requests.Row(i + 2);
            var created = requestRow.Cell(createdColumn).Value;

            foreach (var estimate in estimations[i].Estimates)
            {
                requestRow.Cell(keyColumn).CopyTo(sheet.Cell(row, 1));
                sheet.Cell(row, 2).Value = estimate.Person;
                sheet.Cell(row, 3).Value = estimate.ManDays;

                // Year and month as their own columns: a pivot filter on the date itself lists every single day.
                if (created.IsDateTime)
                {
                    var date = created.GetDateTime();
                    sheet.Cell(row, 4).Value = date.Date;
                    sheet.Cell(row, 4).Style.DateFormat.Format = "yyyy-MM-dd";
                    sheet.Cell(row, 5).Value = date.Year;
                    sheet.Cell(row, 6).Value = date.ToString("yyyy-MM", CultureInfo.InvariantCulture);
                }

                for (int c = 0; c < contextColumns.Length; c++)
                {
                    sheet.Cell(row, 7 + c).Value = requestRow.Cell(contextColumns[c]).Value;
                }

                row++;
            }
        }

        var table = sheet.Range(1, 1, row - 1, headers.Length).CreateTable("EstimatesTable");
        table.Theme = XLTableTheme.None;
        FinishSheet(sheet);
        return table;
    }

    // Total man-days and number of requests per person. Year, month, company and the rest are page
    // filters; Excel recalculates the totals when one is set.
    private static void AddByPersonSheet(XLWorkbook workbook, IXLTable estimates)
    {
        var sheet = workbook.Worksheets.Add("By Person");
        var pivot = sheet.PivotTables.Add("ByPerson", sheet.Cell(3, 1), estimates);

        pivot.RowLabels.Add("Person");
        pivot.Values.Add("Man-Days", "Total Man-Days").SetSummaryFormula(XLPivotSummary.Sum);
        pivot.Values.Add("Key", "Requests").SetSummaryFormula(XLPivotSummary.Count);

        string[] filters = ["Created Year", "Created Month", .. s_estimateContextColumns];
        foreach (var filter in filters)
        {
            pivot.ReportFilters.Add(filter);
        }

        workbook.Worksheet("By Person").Position = 1;
    }

    private static int RequestColumn(string header) => Array.FindIndex(s_columns, c => c.Header == header) + 1;

    private static void WriteHeader(IXLWorksheet sheet, string[] headers)
    {
        for (int col = 0; col < headers.Length; col++)
        {
            var cell = sheet.Cell(1, col + 1);
            cell.Value = headers[col];
            cell.Style.Font.Bold = true;
            cell.Style.Fill.BackgroundColor = XLColor.LightGray;
        }
    }

    private static void FinishSheet(IXLWorksheet sheet)
    {
        sheet.Columns().AdjustToContents();
        foreach (var column in sheet.ColumnsUsed().Where(c => c.Width > MaxColumnWidth))
        {
            column.Width = MaxColumnWidth;
            column.Style.Alignment.WrapText = true;
        }

        sheet.Rows().Style.Alignment.Vertical = XLAlignmentVerticalValues.Top;
        if (!sheet.Tables.Any())
        {
            sheet.RangeUsed()?.SetAutoFilter();
        }

        sheet.SheetView.FreezeRows(1);
    }

    private static string FieldText(JObject issue, string field)
    {
        var value = issue["fields"]?[field];
        return value is null || value.Type == JTokenType.Null ? "" : ToText(value);
    }

    private void WriteCell(IXLCell cell, JObject issue, Column column, string browseUrl)
    {
        if (column.Kind == ColumnKind.Key)
        {
            var key = issue.Value<string>("key") ?? "";
            cell.Value = key;
            if (key.Length > 0 && !string.IsNullOrEmpty(browseUrl))
            {
                cell.SetHyperlink(new XLHyperlink(new Uri(browseUrl + key)));
            }

            return;
        }

        var value = issue["fields"]?[settings.ResolveField(column.Field)];
        if (value is null || value.Type == JTokenType.Null)
        {
            return;
        }

        switch (column.Kind)
        {
            case ColumnKind.DateTime when TryParseJiraDateTime(value.ToString(), out var dateTime):
                cell.Value = dateTime;
                cell.Style.DateFormat.Format = "yyyy-MM-dd HH:mm";
                return;

            case ColumnKind.Number when value.Type is JTokenType.Float or JTokenType.Integer:
                cell.Value = value.Value<double>();
                return;
        }

        var text = ToText(value);
        if (text.Length > 0)
        {
            cell.Value = text;
        }

        if (text.Length == 0 && value.Type == JTokenType.Object)
        {
            logger.LogWarning("{Key}: '{Field}' has a value this export cannot read: {Value}",
                issue.Value<string>("key"), column.Header, value.ToString(Newtonsoft.Json.Formatting.None));
        }
    }

    // The readable text of a field value. Options carry 'value', users 'displayName', teams, groups and
    // statuses 'name'; a cascading select adds its child option; multi-value fields are joined.
    private static string ToText(JToken value)
    {
        switch (value.Type)
        {
            case JTokenType.Null:
                return "";

            case JTokenType.Array:
                return string.Join(", ", value.Select(ToText).Where(t => t.Length > 0));

            case JTokenType.Object:
                var text = value.Value<string>("displayName")
                    ?? value.Value<string>("value")
                    ?? value.Value<string>("name")
                    ?? value.Value<string>("key")
                    ?? "";
                var child = value["child"] is { Type: JTokenType.Object } c ? ToText(c) : "";
                return child.Length > 0 ? $"{text} - {child}" : text;

            case JTokenType.Float:
            case JTokenType.Integer:
                return value.Value<double>().ToString(CultureInfo.InvariantCulture);

            default:
                return value.ToString().Trim();
        }
    }

    // Jira writes "2026-02-12T13:39:09.000+0300". The time is kept as Jira shows it (Türkiye time).
    private static bool TryParseJiraDateTime(string text, out DateTime dateTime)
    {
        // DateTimeOffset wants the offset as +03:00.
        if (text.Length > 6 && char.IsDigit(text[^4]) && text[^5] is '+' or '-')
        {
            text = text[..^2] + ":" + text[^2..];
        }

        if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var offset))
        {
            dateTime = offset.DateTime;
            return true;
        }

        dateTime = default;
        return false;
    }
}
