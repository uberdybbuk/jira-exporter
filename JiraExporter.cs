using Microsoft.Extensions.Logging;

namespace FourArc.JiraExporter;

public class JiraExporter(ILoggerFactory loggerFactory, JiraSettings settings)
{
    private readonly JiraSettings settings = settings ?? throw new ArgumentNullException(nameof(settings));
    private readonly ILogger<JiraExporter> logger = loggerFactory.CreateLogger<JiraExporter>();
    private readonly JiraClient jiraClient = new(loggerFactory.CreateLogger<JiraClient>(), settings);
    private readonly CompletedWorkPackages completed = LoadCompleted(loggerFactory.CreateLogger<JiraExporter>());

    // One time for the whole run, so every work package closed in it gets the same detection time.
    private readonly DateTime runStartedAt = DateTime.Now;

    private static CompletedWorkPackages LoadCompleted(ILogger logger)
    {
        Directory.CreateDirectory(Constants.ProjectInfoDirectory);
        return CompletedWorkPackages.Load(
            Constants.DoneProjectsAndProposalsFileName, Constants.LegacyDoneProjectsAndProposalsFileName, logger);
    }

    // Project issues read in this run. A project with several scoping issues is read once.
    private readonly Dictionary<string, JiraIssue> projectTasks = new(StringComparer.Ordinal);

    private async Task<JiraIssue> LoadProjectTaskAsync(string projectKey)
    {
        if (projectTasks.TryGetValue(projectKey, out var loaded))
        {
            return loaded;
        }

        // Every field JiraIssue declares, so the single-issue fetch below returns all of them.
        var customFieldsToInclude = JiraIssue.GetCustomFields();
        logger.LogDebug("Additional custom fields to include: {CustomFields}", string.Join(", ", customFieldsToInclude));

        try
        {
            var projectTask = await jiraClient.GetIssue(projectKey, customFieldsToInclude);
            projectTasks[projectKey] = projectTask;
            return projectTask;
        }
        catch (Exception e)
        {
            logger.LogError(e, "Failed to fetch project issue for key {ProjectKey}", projectKey);
            throw;
        }
    }

    public async Task<List<WorkPackage>> GetActiveProjects()
    {
        List<JiraIssue> proposalScopingIssues = await GetProposalScopingIssuesAsync();

        List<WorkPackage> resultList = [];

        // Milestone dates live on the parent project issue, so each one is fetched separately.
        for (int i = 0; i < proposalScopingIssues.Count; i++)
        {
            JiraIssue proposalTask = proposalScopingIssues[i];
            logger.LogInformation("Processing {Index}/{Total}: {Key} ...", i + 1, proposalScopingIssues.Count, proposalTask.Key);

            try
            {
                JiraIssue projectTask = await LoadProjectTaskAsync(proposalTask.ParentKey);
                var projectTaskGroup = new WorkPackage(projectTask, proposalTask);

                // A closed work package stays in the reports for the retention period, so its
                // closing is delivered even if one report goes missing; then it drops out.
                if (projectTask.IsClosed)
                {
                    if (completed.MarkClosed(projectTask, proposalTask, runStartedAt))
                    {
                        logger.LogInformation("Project {ProjectKey} is {Status}. {Key} added to the completed list.", projectTask.Key, projectTask.Status, proposalTask.Key);
                    }
                }
                else if (completed.Remove(proposalTask.Key))
                {
                    logger.LogInformation("Project {ProjectKey} was reopened ({Status}). {Key} removed from the completed list.", projectTask.Key, projectTask.Status, proposalTask.Key);
                }

                resultList.Add(projectTaskGroup);
                var filename = Path.Combine(Constants.ProjectInfoDirectory, $"{projectTaskGroup.UniqueId}.json");
                await projectTaskGroup.SaveAsJsonAsync(filename);

                logger.LogInformation("Successfully processed {Key}. Project issue: {ProjectKey} file saved to {FileName}", proposalTask.Key, projectTask.Key, filename);
            }
            catch (Exception e)
            {
                logger.LogError(e, "Failed to fetch parent issue for {Key}. Parent key: {ParentKey}", proposalTask.Key, proposalTask.ParentKey);
            }
        }

        completed.Save();
        logger.LogInformation("Finished processing issues. Total project issues fetched: {Count}", resultList.Count);

        return resultList;
    }

    private async Task<List<JiraIssue>> GetProposalScopingIssuesAsync()
    {
        var jql = settings.ProjectQuery;
        logger.LogInformation("Fetching proposal scoping issues from Jira with JQL: {Jql}", jql);

        List<JiraIssue> proposalScopingIssues = await jiraClient.Search(
            jql, timeout: 20, customFieldsToInclude: ["estimation", "salesForceBudget"]);
        await proposalScopingIssues.SaveAsJsonAsync(Constants.AllProposalScopingIssuesFileName);
        logger.LogInformation("Fetched {Count} proposal scoping issues. Data saved to {FileName}", proposalScopingIssues.Count, Constants.AllProposalScopingIssuesFileName);

        // Drop the work packages closed longer ago than the retention period.
        int allCount = proposalScopingIssues.Count;
        proposalScopingIssues = proposalScopingIssues.Where(issue => !completed.IsRetired(issue.Key, runStartedAt)).ToList();
        int activeCount = proposalScopingIssues.Count;
        logger.LogInformation("Filtered out work packages closed more than {Months} months ago. Before: {BeforeCount}, After: {AfterCount}",
            Constants.CompletedRetentionMonths, allCount, activeCount);

        await proposalScopingIssues.SaveAsJsonAsync(Constants.ActiveProposalScopingIssuesFileName);
        logger.LogInformation("Active proposal scoping issues saved to {FileName}", Constants.ActiveProposalScopingIssuesFileName);

        return proposalScopingIssues;
    }

    // Tracked fields are the report's own columns, minus identity, the timestamps
    // that always move, and values derived from other columns. Relation counts are
    // left out too: every sub-task (UAT bugs included) would otherwise raise a change.
    private static readonly HashSet<string> s_untrackedColumnIds = new(StringComparer.Ordinal)
    {
        "project-key", "ps-task", "created", "ps-created", "updated", "latest-date", "error-message", "summary",
        "link-count", "subtasks"
    };

    private static readonly ReportColumnConfig[] s_trackedColumns =
        ReportColumns.All.Where(c => !s_untrackedColumnIds.Contains(c.Id)).ToArray();

    // Asks Jira what changed instead of re-reading everything: one search lists the
    // work packages that exist now, a second reads their project issues' update
    // timestamps, and only the ones that actually moved are fetched in full.
    public async Task<ChangeReport> CheckForUpdatesAsync()
    {
        var report = new ChangeReport();

        if (!File.Exists(Constants.ProjectTasksFileName))
        {
            logger.LogError("Snapshot '{FilePath}' not found. Run 'fetch' first to create it.", Constants.ProjectTasksFileName);
            return report;
        }

        var snapshot = JsonHelper.FromJson<List<WorkPackage>>(Constants.ProjectTasksFileName);

        var usable = snapshot
            .Where(wp => wp.ProjectTask is not null && wp.ProposalScopingTask is not null)
            .ToList();

        // Completed work packages are carried over untouched, whether or not 'fetch' is
        // still reporting them within the retention period. Without this every one of
        // them would be reported as new or as removed depending on which ran last.
        var retained = usable
            .Where(wp => completed.Contains(wp.ProposalScopingTask.Key))
            .ToList();

        // Keyed by work package rather than by project: one project can have several
        // scoping issues, and each carries its own estimate and budget.
        var previous = usable
            .Where(wp => !completed.Contains(wp.ProposalScopingTask.Key))
            .ToDictionary(wp => wp.UniqueId, StringComparer.Ordinal);
        logger.LogInformation("Loaded {Total} work packages from {FilePath}; {Tracked} tracked, {Retained} already completed.",
            usable.Count, Constants.ProjectTasksFileName, previous.Count, retained.Count);

        var scopingIssues = await jiraClient.Search(
            settings.ProjectQuery, timeout: 20, customFieldsToInclude: ["estimation", "salesForceBudget"]);

        var current = new Dictionary<string, JiraIssue>(StringComparer.Ordinal);
        foreach (var issue in scopingIssues)
        {
            if (string.IsNullOrEmpty(issue.ParentKey))
            {
                logger.LogWarning("Scoping issue {Key} has no parent, so it cannot form a work package.", issue.Key);
                continue;
            }

            if (completed.Contains(issue.Key))
            {
                continue;
            }

            current[$"{issue.ParentKey}_{issue.Key}"] = issue;
        }
        logger.LogInformation("The query returned {Count} work packages still being tracked.", current.Count);

        foreach (var (uniqueId, workPackage) in previous)
        {
            if (!current.ContainsKey(uniqueId))
            {
                report.Removed.Add(workPackage);
            }
        }

        var projectUpdateTimes = await GetProjectUpdateTimesAsync(
            current.Values.Select(issue => issue.ParentKey).Distinct().ToList());

        List<string> toRefresh = [];
        foreach (var (uniqueId, scopingIssue) in current)
        {
            if (!previous.TryGetValue(uniqueId, out var before))
            {
                toRefresh.Add(uniqueId);
                continue;
            }

            if (!projectUpdateTimes.TryGetValue(scopingIssue.ParentKey, out var projectUpdated))
            {
                // The scoping issue still matches, but its project issue did not come
                // back, so there is nothing to compare against.
                report.Unreadable.Add(before);
                continue;
            }

            if (HasMoved(before.ProposalScopingTask.Updated, scopingIssue.Updated) ||
                HasMoved(before.ProjectTask.Updated, projectUpdated))
            {
                toRefresh.Add(uniqueId);
            }
        }

        logger.LogInformation("{Count} work package(s) moved since the last snapshot.", toRefresh.Count);

        // Work packages of the same project share a project issue; fetch each one once.
        Dictionary<string, JiraIssue> projectTasks = new(StringComparer.Ordinal);
        var allCustomFields = JiraIssue.GetCustomFields();

        foreach (var uniqueId in toRefresh)
        {
            var scopingIssue = current[uniqueId];

            if (!projectTasks.TryGetValue(scopingIssue.ParentKey, out var projectTask))
            {
                try
                {
                    projectTask = await jiraClient.GetIssue(scopingIssue.ParentKey, allCustomFields);
                }
                catch (Exception e)
                {
                    logger.LogError(e, "Failed to fetch project issue {ProjectKey} for {UniqueId}.", scopingIssue.ParentKey, uniqueId);
                    continue;
                }

                if (projectTask is null)
                {
                    logger.LogWarning("Project issue {ProjectKey} came back without fields.", scopingIssue.ParentKey);
                    continue;
                }

                projectTasks[scopingIssue.ParentKey] = projectTask;
            }

            var workPackage = new WorkPackage(projectTask, scopingIssue);
            report.Refreshed.Add(workPackage);

            if (!previous.TryGetValue(uniqueId, out var before))
            {
                report.Added.Add(workPackage);
                continue;
            }

            var changes = FindDifferences(before, workPackage);
            if (changes.Count == 0)
            {
                // The update timestamp moved, but no field we track did.
                logger.LogDebug("{UniqueId} was updated, but no tracked field changed.", uniqueId);
                continue;
            }

            foreach (var change in changes)
            {
                logger.LogInformation("{UniqueId} changed - {Change}", uniqueId, change);
            }

            report.Changed.Add(new ChangedWorkPackage(workPackage, changes));
        }

        report.NextSnapshot = BuildNextSnapshot(previous, current, retained, report);
        return report;
    }

    // Written only once the notification has gone out, so a failed send leaves the
    // same changes to be reported again on the next run.
    public async Task CommitSnapshotAsync(ChangeReport report)
    {
        foreach (var workPackage in report.Refreshed)
        {
            await workPackage.SaveAsJsonAsync(Path.Combine(Constants.ProjectInfoDirectory, $"{workPackage.UniqueId}.json"));
        }

        await report.NextSnapshot.SaveAsJsonAsync(Constants.ProjectTasksFileName);
        logger.LogInformation("Snapshot updated: {Count} work packages, {RefreshedCount} of them refreshed.",
            report.NextSnapshot.Count, report.Refreshed.Count);
    }

    // Everything the query still returns: refreshed where it was fetched this run,
    // carried over from the previous snapshot where it was not. Completed work
    // packages are no longer tracked but stay in the file, so the reports built
    // from it keep the full history.
    private static List<WorkPackage> BuildNextSnapshot(
        Dictionary<string, WorkPackage> previous,
        Dictionary<string, JiraIssue> current,
        List<WorkPackage> retained,
        ChangeReport report)
    {
        var refreshed = report.Refreshed.ToDictionary(wp => wp.UniqueId, StringComparer.Ordinal);

        List<WorkPackage> next = [.. retained];
        foreach (var uniqueId in current.Keys)
        {
            if (refreshed.TryGetValue(uniqueId, out var updated))
            {
                next.Add(updated);
            }
            else if (previous.TryGetValue(uniqueId, out var carried))
            {
                next.Add(carried);
            }
        }

        return next;
    }

    // 'key in (...)' with the whole active set would produce an over-long URL, so
    // the keys go out in batches.
    private async Task<Dictionary<string, DateTime>> GetProjectUpdateTimesAsync(List<string> projectKeys)
    {
        const int BatchSize = 100;
        Dictionary<string, DateTime> updateTimes = new(StringComparer.Ordinal);

        for (int i = 0; i < projectKeys.Count; i += BatchSize)
        {
            var batch = projectKeys.Skip(i).Take(BatchSize);
            var issues = await jiraClient.Search($"key in ({string.Join(", ", batch)})", timeout: 30);

            foreach (var issue in issues)
            {
                updateTimes[issue.Key] = issue.Updated;
            }
        }

        var missingCount = projectKeys.Count - updateTimes.Count;
        if (missingCount > 0)
        {
            logger.LogWarning("{Count} project issue(s) were not returned by the key search.", missingCount);
        }

        return updateTimes;
    }

    // The snapshot's timestamps have been through JSON, so they are compared in one
    // time zone and at second precision instead of by exact DateTime equality.
    private static bool HasMoved(DateTime previous, DateTime current) =>
        previous.ToUniversalTime().Ticks / TimeSpan.TicksPerSecond
            != current.ToUniversalTime().Ticks / TimeSpan.TicksPerSecond;

    private static List<ChangeLogEntry> FindDifferences(WorkPackage before, WorkPackage after)
    {
        List<ChangeLogEntry> changes = [];

        foreach (var column in s_trackedColumns)
        {
            var previousValue = Format(column, before);
            var currentValue = Format(column, after);

            if (!string.Equals(previousValue, currentValue, StringComparison.Ordinal))
            {
                changes.Add(new ChangeLogEntry(column.Header, previousValue, currentValue));
            }
        }

        return changes;
    }

    private static string Format(ReportColumnConfig column, WorkPackage workPackage)
    {
        var value = column.ValueSelector(workPackage);
        return value is null ? "" : column.Formatter(value);
    }
}
