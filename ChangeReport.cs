namespace FourArc.JiraExporter;

public record ChangeLogEntry(string FieldName, string PreviousValue, string CurrentValue)
{
    public override string ToString() => $"{FieldName}: '{PreviousValue}' -> '{CurrentValue}'";
}

// One work package whose tracked fields moved since the last snapshot.
public sealed record ChangedWorkPackage(WorkPackage WorkPackage, List<ChangeLogEntry> Changes);

// What a single 'checkforupdates' run found. NextSnapshot is the state to persist
// once the notification has gone out; it is not written until then, so a failed
// send leaves the run repeatable.
public sealed class ChangeReport
{
    public List<WorkPackage> Added { get; } = [];
    public List<ChangedWorkPackage> Changed { get; } = [];
    public List<WorkPackage> Removed { get; } = [];

    // Work packages whose project issue the query no longer returns. Usually a
    // permission change rather than a deletion, so they are reported rather than
    // silently dropped.
    public List<WorkPackage> Unreadable { get; } = [];

    public List<WorkPackage> NextSnapshot { get; set; } = [];

    // Work packages refreshed from Jira this run, so only these need rewriting.
    public List<WorkPackage> Refreshed { get; } = [];

    public bool HasChanges => Added.Count > 0 || Changed.Count > 0 || Removed.Count > 0 || Unreadable.Count > 0;

    public string Subject
    {
        get
        {
            List<string> parts = [];
            if (Added.Count > 0) parts.Add($"{Added.Count} new");
            if (Changed.Count > 0) parts.Add($"{Changed.Count} changed");
            if (Removed.Count > 0) parts.Add($"{Removed.Count} removed");
            if (Unreadable.Count > 0) parts.Add($"{Unreadable.Count} unreadable");
            return parts.Count == 0 ? "Jira projects: no changes" : $"Jira projects: {string.Join(", ", parts)}";
        }
    }
}
