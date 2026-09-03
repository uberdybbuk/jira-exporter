namespace FourArc.JiraExporter;

public class ReportColumnConfig
{
    public string Id { get; }
    public string Header { get; }
    public Func<WorkPackage, object> ValueSelector { get; }

    // Plain text, no markup: Excel uses this directly.
    public Func<object, string> Formatter { get; }

    public ReportColumnConfig(string id, string header, Func<WorkPackage, object> valueSelector, Func<object, string> formatter = null)
    {
        Id = id;
        Header = header;
        ValueSelector = valueSelector;
        Formatter = formatter ?? (val => val?.ToString() ?? "");
    }
}

// Column definitions shared by every report generator.
public static class ReportColumns
{
    public static string ToSmallDateString(DateTime? date) => date.HasValue ? date.Value.ToString("yyyy-MM-dd") : "";
    public static string ToSmallDateString(DateOnly? date) => date.HasValue ? date.Value.ToString("yyyy-MM-dd") : "";
    public static string TrimAfterDash(object val)
    {
        var name = val?.ToString() ?? "";
        var dashIndex = name.IndexOf('-');
        return dashIndex >= 0 ? name[..dashIndex].Trim() : name;
    }
    public static string JoinList(IEnumerable<string> items) => string.Join("; ", items);

    // Latest milestone date on the project task, as "yyyy-MM-dd <date-name>".
    public static string LatestDate(WorkPackage item)
    {
        var task = item.ProjectTask;
        if (task is null)
            return "";

        (string Name, DateOnly? Date)[] dates =
        [
            ("Actual Scope Start", task.ActualScopeStartDate),
            ("Actual Scope End", task.ActualScopeEndDate),
            ("Actual Planned Start", task.ActualPlannedStartDate),
            ("Actual Analysis Start", task.ActualAnalysisStartDate),
            ("Actual Analysis End", task.ActualAnalysisEndDate),
            ("Dev Start", task.DevelopmentStartDate),
            ("Actual Dev Start", task.ActualDevelopmentStartDate),
            ("Actual Dev End", task.ActualDevelopmentEndDate),
            ("UAT Start", task.UATStartDate),
            ("UAT End", task.UATEndDate),
            ("Actual UAT Start", task.ActualUATStartDate),
            ("Actual UAT End", task.ActualUATEndDate),
            ("Actual Pre-Prod Start", task.ActualPreProdStartDate),
            ("Actual Pre-Prod End", task.ActualPreProdEndDate),
            ("Actual Security Approval End", task.ActualSecurityApprovalEndDate),
            ("Actual Prod Date", task.ActualProdDate),
            ("Prod Date", task.ProdDate),
        ];

        var latest = dates
            .Where(d => d.Date.HasValue)
            .OrderByDescending(d => d.Date!.Value)
            .FirstOrDefault();

        return latest.Name is null ? "" : $"{ToSmallDateString(latest.Date)} {latest.Name}";
    }

    public static List<ReportColumnConfig> All =>
    [
        new ("project-key", "ProjectKey", item => item.ProjectTask?.Key ),
        new ("created", "Created", item => item.ProjectTask?.Created ),
        new ("updated", "Updated", item => item.ProjectTask?.Updated ),
        new ("assignee", "Assignee", item => item.ProjectTask?.AssigneeDisplayName, TrimAfterDash ), // proje taskının o anda kime atandığı
        new ("ps-assignee", "Proposal Scoping Assignee", item => item.ProposalScopingTask?.AssigneeDisplayName, TrimAfterDash ), // proposal scoping taskını kim halletmiş
        new ("issue-type", "Issue Type", item => item.ProjectTask?.IssueType ),
        new ("status", "Status", item => item.ProjectTask?.Status ),
        new ("resolution", "Resolution", item => item.ProjectTask?.Resolution ),
        // new ("company", "Company", item => item.ProjectTask?.Company ),
        new ("summary", "Summary", item => item.ProjectTask?.Summary ),
        new ("estimation", "Estimation", item => item.ProposalScopingTask?.Estimation ),
        new ("salesforce-budget", "SalesForce Budget", item => item.ProposalScopingTask?.SalesForceBudget ),

        new ("latest-date", "Latest Date", item => LatestDate(item) ),

        new ("actual-scope-start", "Actual Scope Start", item => item.ProjectTask?.ActualScopeStartDate, val => ToSmallDateString((DateOnly?)val) ),
        new ("actual-scope-end", "Actual Scope End", item => item.ProjectTask?.ActualScopeEndDate, val => ToSmallDateString((DateOnly?)val) ),

        new ("actual-planned-start", "Actual Planned Start", item => item.ProjectTask?.ActualPlannedStartDate, val => ToSmallDateString((DateOnly?)val) ),

        new ("actual-analysis-start", "Actual Analysis Start", item => item.ProjectTask?.ActualAnalysisStartDate, val => ToSmallDateString((DateOnly?)val) ),
        new ("actual-analysis-end", "Actual Analysis End", item => item.ProjectTask?.ActualAnalysisEndDate, val => ToSmallDateString((DateOnly?)val) ),

        new ("dev-start", "Dev Start", item => item.ProjectTask?.DevelopmentStartDate, val => ToSmallDateString((DateOnly?)val) ),
        new ("actual-dev-start", "Actual Dev Start", item => item.ProjectTask?.ActualDevelopmentStartDate, val => ToSmallDateString((DateOnly?)val) ),
        new ("actual-dev-end", "Actual Dev End", item => item.ProjectTask?.ActualDevelopmentEndDate, val => ToSmallDateString((DateOnly?)val) ),

        new ("uat-start", "UAT Start", item => item.ProjectTask?.UATStartDate, val => ToSmallDateString((DateOnly?)val) ),
        new ("uat-end", "UAT End", item => item.ProjectTask?.UATEndDate, val => ToSmallDateString((DateOnly?)val) ),
        new ("actual-uat-start", "Actual UAT Start", item => item.ProjectTask?.ActualUATStartDate, val => ToSmallDateString((DateOnly?)val) ),
        new ("actual-uat-end", "Actual UAT End", item => item.ProjectTask?.ActualUATEndDate, val => ToSmallDateString((DateOnly?)val) ),

        new ("actual-preprod-start", "Actual Pre-Prod Start", item => item.ProjectTask?.ActualPreProdStartDate, val => ToSmallDateString((DateOnly?)val) ),
        new ("actual-preprod-end", "Actual Pre-Prod End", item => item.ProjectTask?.ActualPreProdEndDate, val => ToSmallDateString((DateOnly?)val) ),
        // new ("actual-security-end", "Actual Security Approval End", item => item.ProjectTask?.ActualSecurityApprovalEndDate, val => ToSmallDateString((DateOnly?)val) ),
        new ("actual-prod-date", "Actual Prod Date", item => item.ProjectTask?.ActualProdDate, val => ToSmallDateString((DateOnly?)val) ),
        new ("prod-date", "Prod Date", item => item.ProjectTask?.ProdDate, val => ToSmallDateString((DateOnly?)val) ),
        new ("ps-task", "Proposal Scoping Task", item => item.ProposalScopingTask?.Key ),
        new ("error-message", "Error Message", item => ReportColumns.JoinList(item.ErrorMessages) ),
    ];
}
