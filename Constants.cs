namespace FourArc.JiraExporter;

public static class Constants
{
    public static readonly string RootDataDirectory = "data";
    public static readonly string ProjectInfoDirectory = Path.Combine(RootDataDirectory, "project-info");
    public static readonly string HttpCacheDirectory = Path.Combine(RootDataDirectory, "MiniHttpCache");

    // Reference data kept outside 'data' because it is long-lived material consulted
    // occasionally for analysis, not part of a fetch/report run's working output.
    public static readonly string ReferenceDirectory = "reference";

    public static readonly string AllProposalScopingIssuesFileName = Path.Combine(RootDataDirectory, "all-proposal-scoping-issues.json");
    public static readonly string ActiveProposalScopingIssuesFileName = Path.Combine(RootDataDirectory, "proposal-scoping-issues.json");
    public static readonly string ProjectTasksFileName = Path.Combine(RootDataDirectory, "project-tasks.json");
    public static readonly string DoneProjectsAndProposalsFileName = Path.Combine(RootDataDirectory, "done-or-cancelled-projects-and-proposals.txt");

    public static readonly string HtmlReportFileName = Path.Combine(RootDataDirectory, "results.html");
    public static readonly string ExcelReportFileName = Path.Combine(RootDataDirectory, "results-excel.xlsx");
    public static readonly string JsonReportFileName = Path.Combine(RootDataDirectory, "results.json");

    public static readonly string JiraFieldsFileName = Path.Combine(ReferenceDirectory, "jira-fields.json");
}