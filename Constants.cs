namespace FourArc.JiraExporter;

public static class Constants
{
    public static readonly string RootDataDirectory = "data";
    public static readonly string ProjectInfoDirectory = Path.Combine(RootDataDirectory, "project-info");

    // Reference data kept outside 'data' because it is long-lived material consulted
    // occasionally for analysis, not part of a fetch/report run's working output.
    public static readonly string ReferenceDirectory = "reference";

    public static readonly string AllProposalScopingIssuesFileName = Path.Combine(RootDataDirectory, "all-proposal-scoping-issues.json");
    public static readonly string ActiveProposalScopingIssuesFileName = Path.Combine(RootDataDirectory, "proposal-scoping-issues.json");
    public static readonly string ProjectTasksFileName = Path.Combine(RootDataDirectory, "project-tasks.json");
    public static readonly string DoneProjectsAndProposalsFileName = Path.Combine(RootDataDirectory, "done-or-cancelled-projects-and-proposals.csv");

    // The free-text format used before the CSV; converted once on first load.
    public static readonly string LegacyDoneProjectsAndProposalsFileName = Path.Combine(RootDataDirectory, "done-or-cancelled-projects-and-proposals.txt");

    // How long a closed work package keeps being fetched and reported after it was detected.
    public const int CompletedRetentionMonths = 2;

    public static readonly string HtmlReportFileName = Path.Combine(RootDataDirectory, "results.html");
    public static readonly string ExcelReportFileName = Path.Combine(RootDataDirectory, "results-excel.xlsx");
    public static readonly string JsonReportFileName = Path.Combine(RootDataDirectory, "results.json");

    public static readonly string JiraFieldsFileName = Path.Combine(ReferenceDirectory, "jira-fields.json");

    public static readonly string ChangelogsFileName = Path.Combine(RootDataDirectory, "changelogs.json");
    public static readonly string ChangelogsWorkbookFileName = Path.Combine(RootDataDirectory, "changelogs.xlsx");
    // One raw Jira issue (with its changelog) per file, named <KEY>.json.
    public static readonly string ChangelogIssueDirectory = Path.Combine(RootDataDirectory, "changelogs");

    // 'changelogs' reads open projects and the ones resolved since the start of this many months ago.
    public const int ChangelogClosedMonths = 6;
}