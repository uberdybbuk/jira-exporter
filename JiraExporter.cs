using Microsoft.Extensions.Logging;
using System.Collections;
using System.Reflection;

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

    internal async Task<List<WorkPackage>> CheckForUpdatesAsync()
    {
        List<WorkPackage> results = [];

        // Load what the previous run saw.
        if (!File.Exists(Constants.ProjectTasksFileName))
        {
            _logger.LogWarning("Project tasks file not found at {FilePath}. Run 'fetch' first to create this file.", Constants.ProjectTasksFileName);
            return results;
        }

        var existingProjectTasks = JsonHelper.FromJson<List<WorkPackage>>(Constants.ProjectTasksFileName);
        _logger.LogInformation("Loaded {Count} project tasks from {FilePath}", existingProjectTasks.Count, Constants.ProjectTasksFileName);

        HashSet<string> uniqueProjectKeys = existingProjectTasks.Select(pt => pt.ProjectTask.Key).ToHashSet();

        var jql = $"key in ({string.Join(", ", uniqueProjectKeys)})";
        List<JiraIssue> projectTaskSummaries = await _jiraClient.Search(jql, timeout: 30);

        // An issue that stops coming back has usually become invisible to this account.
        var allUpdatedProjectKeys = projectTaskSummaries.Select(i => i.Key).ToHashSet();
        var allMissingProjectKeys = uniqueProjectKeys.Where(k => !allUpdatedProjectKeys.Contains(k)).ToList();
        if (allMissingProjectKeys.Count > 0)
        {
            // TODO: this should be specially reported
            _logger.LogWarning("The following project keys were not returned in the search results. This may indicate a visibility change or deletion. Missing project keys: {MissingProjectKeys}", 
                string.Join(", ", allMissingProjectKeys));
        }

        // Compare against the previous snapshot.
        foreach (var updatedProjectTaskSummary in projectTaskSummaries)
        {
            var existingProjectTaskGroup = existingProjectTasks.First(pt => pt.ProjectTask.Key == updatedProjectTaskSummary.Key);
            if(existingProjectTaskGroup.ProjectTask.Updated == updatedProjectTaskSummary.Updated)
            {
                _logger.LogDebug("No update detected for project {Key}. Updated date is the same as before: {UpdatedDate}", 
                    updatedProjectTaskSummary.Key, updatedProjectTaskSummary.Updated);
                continue;
            }

            JiraIssue updatedProjectTask = await LoadProjectTaskAsync(updatedProjectTaskSummary.Key);
            List<ChangeLogEntry> changes = FindDifferences(existingProjectTaskGroup.ProjectTask, updatedProjectTask);

            if (changes.Count == 0)
            {
                // Updated timestamp moved but nothing we track changed.
                _logger.LogInformation("No changes detected for project {Key} for the fields we care about.", updatedProjectTask.Key);
                continue;
            }

            _logger.LogInformation("Detected {ChangeCount} change(s) in project {Key}. Local update: {PreviousUpdate}, Current update: {CurrentUpdate}",
                changes.Count, updatedProjectTask.Key, existingProjectTaskGroup.ProjectTask.Updated, updatedProjectTask.Updated);

            foreach (var change in changes)
            {
                _logger.LogInformation("Project {Key} changed - {Change}", updatedProjectTask.Key, change);
            }

            results.Add(new WorkPackage(updatedProjectTask, existingProjectTaskGroup.ProposalScopingTask));
        }

        return results;
    }

    private static List<ChangeLogEntry> FindDifferences(JiraIssue existingIssue, JiraIssue updatedIssue)
    {
        List<ChangeLogEntry> changes = [];
        PropertyInfo[] properties = typeof(JiraIssue)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.GetIndexParameters().Length == 0)
            .ToArray();

        foreach (PropertyInfo property in properties)
        {
            object existingValue = property.GetValue(existingIssue);
            object updatedValue = property.GetValue(updatedIssue);

            if (AreEqual(existingValue, updatedValue))
            {
                continue;
            }

            changes.Add(new ChangeLogEntry(
                property.Name,
                FormatValue(existingValue),
                FormatValue(updatedValue)));
        }

        return changes;
    }

    private static bool AreEqual(object existingValue, object updatedValue)
    {
        if (ReferenceEquals(existingValue, updatedValue))
        {
            return true;
        }

        if (existingValue is null || updatedValue is null)
        {
            return false;
        }

        if (existingValue is IEnumerable existingEnumerable && updatedValue is IEnumerable updatedEnumerable &&
            existingValue is not string && updatedValue is not string)
        {
            string[] existingItems = existingEnumerable.Cast<object>().Select(FormatValue).OrderBy(x => x).ToArray();
            string[] updatedItems = updatedEnumerable.Cast<object>().Select(FormatValue).OrderBy(x => x).ToArray();
            return existingItems.SequenceEqual(updatedItems, StringComparer.Ordinal);
        }

        return Equals(existingValue, updatedValue);
    }

    private static string FormatValue(object value)
    {
        if (value is null)
        {
            return "<null>";
        }

        return value switch
        {
            DateTime dateTime => dateTime.ToString("yyyy-MM-dd HH:mm:ss"),
            DateTimeOffset dateTimeOffset => dateTimeOffset.ToString("yyyy-MM-dd HH:mm:ss zzz"),
            DateOnly dateOnly => dateOnly.ToString("yyyy-MM-dd"),
            IEnumerable enumerable when value is not string => string.Join(", ", enumerable.Cast<object>().Select(FormatValue)),
            _ => value.ToString() ?? "<null>"
        };
    }
}

public record ChangeLogEntry(string FieldName, string PreviousValue, string CurrentValue)
{
    public override string ToString() => $"{FieldName}: '{PreviousValue}' -> '{CurrentValue}'";
}
