namespace FourArc.JiraExporter;

// One relation of a project issue, flattened to a row: its parent, an issue link or a sub-task.
public record RelationRow(
    string ProjectKey,
    string Kind,
    string LinkType,
    string Relation,
    string Key,
    string Summary,
    string IssueType,
    string Status);

public static class ReportRelations
{
    // Relations belong to the project issue rather than the work package, so a project
    // with several scoping issues is listed once instead of once per scoping issue.
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
            yield return new RelationRow(task.Key, "Parent", "", "", task.ParentKey, "", "", "");
        }

        foreach (var link in task.IssueLinks)
        {
            // Snapshots written before Relation was captured only have the direction.
            yield return new RelationRow(task.Key, "Link", link.LinkType, link.Relation ?? link.Direction,
                link.Key, link.Summary, link.IssueType, link.Status);
        }

        foreach (var subtask in task.Subtasks)
        {
            yield return new RelationRow(task.Key, "Subtask", "", "",
                subtask.Key, subtask.Summary, subtask.IssueType, subtask.Status);
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
