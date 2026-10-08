namespace FourArc.JiraExporter;

// One relation of a project issue, flattened to a row: its parent, an issue link or a
// sub-task. Read by a program as well as by people, so Kind and Direction hold fixed
// values and anything that does not apply to the kind is null.
public record RelationRow(
    string ProjectKey,
    string Kind,                  // "parent", "link" or "subtask"
    string LinkType,              // links only, e.g. "Relates", "Slice"
    string Direction,             // links only: "inward" or "outward", as seen from the project
    string RelatedKey,
    string RelatedSummary,
    string RelatedIssueType,
    string RelatedStatus,
    string RelatedStatusCategory, // "new", "indeterminate" or "done"
    string LinkDescription);      // links only: Jira's wording for the direction, e.g. "is sliced by"

// A column of the Relations sheet. Headers carry no spaces so a program can find
// them by name; the order is fixed so one reading by position works too.
public record RelationColumn(string Header, Func<RelationRow, string> Value);

public static class ReportRelations
{
    // Columns added later go at the end, so a reader working by position keeps working.
    public static readonly RelationColumn[] Columns =
    [
        new("ProjectKey", r => r.ProjectKey),
        new("Kind", r => r.Kind),
        new("LinkType", r => r.LinkType),
        new("Direction", r => r.Direction),
        new("RelatedKey", r => r.RelatedKey),
        new("RelatedSummary", r => r.RelatedSummary),
        new("RelatedIssueType", r => r.RelatedIssueType),
        new("RelatedStatus", r => r.RelatedStatus),
        new("RelatedStatusCategory", r => r.RelatedStatusCategory),
        new("LinkDescription", r => r.LinkDescription),
    ];

    // Relations belong to the project issue rather than the work package, so a project
    // with several scoping issues is listed once instead of once per scoping issue. For
    // every project in the report the rows are the complete set: a link removed in Jira
    // simply stops appearing.
    public static List<RelationRow> Build(IEnumerable<WorkPackage> workPackages) =>
        workPackages
            .Select(wp => wp.ProjectTask)
            .Where(task => task is not null)
            .DistinctBy(task => task.Key)
            .SelectMany(ForIssue)
            .ToList();

    public static IEnumerable<RelationRow> ForIssue(JiraIssue task)
    {
        if (task is null)
        {
            yield break;
        }

        // Only the key is fetched for the parent.
        if (!string.IsNullOrEmpty(task.ParentKey))
        {
            yield return new RelationRow(task.Key, "parent", null, null, task.ParentKey, null, null, null, null, null);
        }

        foreach (var link in task.IssueLinks)
        {
            yield return new RelationRow(task.Key, "link", link.LinkType, link.Direction,
                link.Key, link.Summary, link.IssueType, link.Status, link.StatusCategory, link.Relation);
        }

        foreach (var subtask in task.Subtasks)
        {
            yield return new RelationRow(task.Key, "subtask", null, null,
                subtask.Key, subtask.Summary, subtask.IssueType, subtask.Status, subtask.StatusCategory, null);
        }
    }

    // Sub-task counts per issue type, largest first, e.g. "UAT Bug: 93, Proposal Scoping: 3".
    public static string SubtaskSummary(JiraIssue task)
    {
        if (task is null || task.Subtasks.Count == 0)
        {
            return "";
        }

        return string.Join(", ", task.Subtasks
            .GroupBy(s => string.IsNullOrEmpty(s.IssueType) ? "?" : s.IssueType)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => $"{g.Key}: {g.Count()}"));
    }
}
