using Microsoft.Extensions.Logging;

namespace FourArc.JiraExporter;

public class JiraExporter
{
    private readonly ILogger<JiraExporter> _logger;
    private readonly JiraClient _jiraClient;
    private readonly ILoggerFactory _loggerFactory;
    private readonly Dictionary<string, string> _doneProjectKeysAndProposalScopingKeys = []; // key: proposal scoping task key, value: project key
    private readonly JiraSettings _settings;

    public JiraExporter(ILoggerFactory loggerFactory, JiraSettings settings)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<JiraExporter>();
        _jiraClient = new JiraClient(_loggerFactory.CreateLogger<JiraClient>(), settings);

        LoadDoneProjectsAndProposals();
        Directory.CreateDirectory(Constants.ProjectInfoDirectory);
    }

    private void LoadDoneProjectsAndProposals()
    {
        var filePath = Constants.DoneProjectsAndProposalsFileName;
        if (!File.Exists(filePath))
        {
            _logger.LogWarning("Done projects and proposals file not found at {FilePath}", filePath);
            return;
        }

        var lines = File.ReadAllLines(filePath);

        foreach (var line in lines)
        {
            var parts = line.Split(';'); // there may be more than 2 parts but we only care about the first 2 (project key and proposal scoping key)
            if (parts.Length >= 2)
            {
                var projectKey = parts[0].Trim();
                var proposalScopingKey = parts[1].Trim();
                _doneProjectKeysAndProposalScopingKeys[proposalScopingKey] = projectKey;
            }
        }

        _logger.LogInformation("Loaded {Count} done project keys from {FilePath}", _doneProjectKeysAndProposalScopingKeys.Count, filePath);
    }

    private async Task<JiraIssue> LoadProjectTaskAsync(string projectKey)
    {
        // Every field JiraIssue declares, so the single-issue fetch below returns all of them.
        var customFieldsToInclude = JiraIssue.GetCustomFields();
        _logger.LogDebug("Additional custom fields to include: {CustomFields}", string.Join(", ", customFieldsToInclude));

        try
        {
            return await _jiraClient.GetIssue(projectKey, customFieldsToInclude);
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Failed to fetch project issue for key {ProjectKey}", projectKey);
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
            _logger.LogInformation("Processing {Index}/{Total}: {Key} ...", i + 1, proposalScopingIssues.Count, proposalTask.Key);

            try
            {
                JiraIssue projectTask = await LoadProjectTaskAsync(proposalTask.ParentKey);
                var projectTaskGroup = new WorkPackage(projectTask, proposalTask);

                // Record it as finished so later runs skip it. This run still keeps the issue,
                // so a completed project is captured once before it drops out.
                if (projectTask.Status == "Done" || projectTask.Status == "Cancelled")
                {
                    string status = projectTask.Status;
                    _doneProjectKeysAndProposalScopingKeys[proposalTask.Key] = projectTask.Key;
                    File.AppendAllLines(Constants.DoneProjectsAndProposalsFileName, [$"{projectTask.Key};{proposalTask.Key};{"Auto-added on " + DateTime.Now.ToString("yyyy-MM-dd")}. Status = {status}"]);
                    _logger.LogInformation("Project {ProjectKey} is marked as {Status}. Added to done projects and proposals list.", projectTask.Key, status);
                }

                resultList.Add(projectTaskGroup);
                var filename = Path.Combine(Constants.ProjectInfoDirectory, $"{projectTaskGroup.UniqueId}.json");
                await projectTaskGroup.SaveAsJsonAsync(filename);

                _logger.LogInformation("Successfully processed {Key}. Project issue: {ProjectKey} file saved to {FileName}", proposalTask.Key, projectTask.Key, filename);
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Failed to fetch parent issue for {Key}. Parent key: {ParentKey}", proposalTask.Key, proposalTask.ParentKey);
            }
        }

        _logger.LogInformation("Finished processing issues. Total project issues fetched: {Count}", resultList.Count);

        return resultList;
    }

    private async Task<List<JiraIssue>> GetProposalScopingIssuesAsync()
    {
        var jql = _settings.ProjectQuery;
        _logger.LogInformation("Fetching proposal scoping issues from Jira with JQL: {Jql}", jql);

        List<JiraIssue> proposalScopingIssues = await _jiraClient.Search(
            jql, timeout: 20, customFieldsToInclude: ["estimation", "salesForceBudget"]);
        await proposalScopingIssues.SaveAsJsonAsync(Constants.AllProposalScopingIssuesFileName);
        _logger.LogInformation("Fetched {Count} proposal scoping issues. Data saved to {FileName}", proposalScopingIssues.Count, Constants.AllProposalScopingIssuesFileName);

        // Drop the ones already recorded as finished; the dictionary is keyed by scoping issue.
        int allCount = proposalScopingIssues.Count;
        proposalScopingIssues = proposalScopingIssues.Where(issue => !_doneProjectKeysAndProposalScopingKeys.ContainsKey(issue.Key)).ToList();
        int activeCount = proposalScopingIssues.Count;
        _logger.LogInformation("Filtered proposal scoping issues based on done projects. Before: {BeforeCount}, After: {AfterCount}", allCount, activeCount);

        await proposalScopingIssues.SaveAsJsonAsync(Constants.ActiveProposalScopingIssuesFileName);
        _logger.LogInformation("Active proposal scoping issues saved to {FileName}", Constants.ActiveProposalScopingIssuesFileName);

        return proposalScopingIssues;
    }

    // Tracked fields are the report's own columns, minus identity, the timestamps
    // that always move, and values derived from other columns.
    private static readonly HashSet<string> s_untrackedColumnIds = new(StringComparer.Ordinal)
    {
        "project-key", "ps-task", "created", "updated", "latest-date", "error-message", "summary"
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
            _logger.LogError("Snapshot '{FilePath}' not found. Run 'fetch' first to create it.", Constants.ProjectTasksFileName);
            return report;
        }

        var snapshot = JsonHelper.FromJson<List<WorkPackage>>(Constants.ProjectTasksFileName);

        // Keyed by work package rather than by project: one project can have several
        // scoping issues, and each carries its own estimate and budget.
        var previous = snapshot
            .Where(wp => wp.ProjectTask is not null && wp.ProposalScopingTask is not null)
            .ToDictionary(wp => wp.UniqueId, StringComparer.Ordinal);
        _logger.LogInformation("Loaded {Count} work packages from {FilePath}", previous.Count, Constants.ProjectTasksFileName);

        var scopingIssues = await _jiraClient.Search(
            _settings.ProjectQuery, timeout: 20, customFieldsToInclude: ["estimation", "salesForceBudget"]);

        var current = new Dictionary<string, JiraIssue>(StringComparer.Ordinal);
        foreach (var issue in scopingIssues)
        {
            if (string.IsNullOrEmpty(issue.ParentKey))
            {
                _logger.LogWarning("Scoping issue {Key} has no parent, so it cannot form a work package.", issue.Key);
                continue;
            }

            current[$"{issue.ParentKey}_{issue.Key}"] = issue;
        }
        _logger.LogInformation("The query returned {Count} work packages.", current.Count);

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

        _logger.LogInformation("{Count} work package(s) moved since the last snapshot.", toRefresh.Count);

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
                    // Bypass the response cache; a day-old answer would hide the change.
                    projectTask = await _jiraClient.GetIssue(scopingIssue.ParentKey, allCustomFields, useCache: false);
                }
                catch (Exception e)
                {
                    _logger.LogError(e, "Failed to fetch project issue {ProjectKey} for {UniqueId}.", scopingIssue.ParentKey, uniqueId);
                    continue;
                }

                if (projectTask is null)
                {
                    _logger.LogWarning("Project issue {ProjectKey} came back without fields.", scopingIssue.ParentKey);
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
                _logger.LogDebug("{UniqueId} was updated, but no tracked field changed.", uniqueId);
                continue;
            }

            foreach (var change in changes)
            {
                _logger.LogInformation("{UniqueId} changed - {Change}", uniqueId, change);
            }

            report.Changed.Add(new ChangedWorkPackage(workPackage, changes));
        }

        report.NextSnapshot = BuildNextSnapshot(previous, current, report);
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
        _logger.LogInformation("Snapshot updated: {Count} work packages, {RefreshedCount} of them refreshed.",
            report.NextSnapshot.Count, report.Refreshed.Count);
    }

    // Everything the query still returns: refreshed where it was fetched this run,
    // carried over from the previous snapshot where it was not.
    private static List<WorkPackage> BuildNextSnapshot(
        Dictionary<string, WorkPackage> previous,
        Dictionary<string, JiraIssue> current,
        ChangeReport report)
    {
        var refreshed = report.Refreshed.ToDictionary(wp => wp.UniqueId, StringComparer.Ordinal);

        List<WorkPackage> next = [];
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
            var issues = await _jiraClient.Search($"key in ({string.Join(", ", batch)})", timeout: 30);

            foreach (var issue in issues)
            {
                updateTimes[issue.Key] = issue.Updated;
            }
        }

        var missingCount = projectKeys.Count - updateTimes.Count;
        if (missingCount > 0)
        {
            _logger.LogWarning("{Count} project issue(s) were not returned by the key search.", missingCount);
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
