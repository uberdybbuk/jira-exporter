using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace FourArc.JiraExporter;

// A work package whose project issue was found closed. Resolved is Jira's own
// resolution date; DetectedAt is the run that noticed it.
public record CompletedWorkPackage(
    string ProjectKey,
    string ProposalScopingKey,
    string Status,
    DateOnly? Resolved,
    DateTime DetectedAt);

// The done list, kept as a semicolon-separated CSV so Excel with a Turkish locale
// opens it in columns. A recorded work package is still fetched and reported for
// Constants.CompletedRetentionMonths after it was detected, so its closing reaches
// whatever consumes the reports even if one report is never delivered. After that
// it is skipped.
public class CompletedWorkPackages
{
    private const string Header = "ProjectKey;ProposalScopingKey;Status;Resolved;DetectedAt";
    private const string DateFormat = "yyyy-MM-dd";
    private const string DateTimeFormat = "yyyy-MM-dd HH:mm:ss";

    // Keyed by scoping issue: a project with several scoping issues has one row per work package.
    private readonly Dictionary<string, CompletedWorkPackage> _entries = new(StringComparer.Ordinal);
    private readonly string _path;

    private CompletedWorkPackages(string path)
    {
        _path = path;
    }

    public int Count => _entries.Count;

    // Reads the CSV. When there is none yet but the old free-text file exists, that one
    // is converted once and kept beside it as .bak.
    public static CompletedWorkPackages Load(string path, string legacyPath, ILogger logger)
    {
        var list = new CompletedWorkPackages(path);

        if (File.Exists(path))
        {
            foreach (var line in File.ReadAllLines(path).Skip(1))
            {
                list.AddParsed(ParseLine(line));
            }

            logger.LogInformation("Loaded {Count} completed work packages from {FilePath}", list.Count, path);
        }
        else if (File.Exists(legacyPath))
        {
            foreach (var line in File.ReadAllLines(legacyPath))
            {
                list.AddParsed(ParseLegacyLine(line));
            }

            list.Save();
            File.Move(legacyPath, legacyPath + ".bak", overwrite: true);
            logger.LogInformation("Converted {Count} completed work packages from {LegacyPath} to {FilePath}; the old file is kept as .bak.",
                list.Count, legacyPath, path);
        }
        else
        {
            logger.LogWarning("Completed work packages file not found at {FilePath}", path);
        }

        return list;
    }

    public bool Contains(string scopingKey) => _entries.ContainsKey(scopingKey);

    // True once the retention period has passed since detection; such work packages are not fetched any more.
    public bool IsRetired(string scopingKey, DateTime now) =>
        _entries.TryGetValue(scopingKey, out var entry)
        && entry.DetectedAt.AddMonths(Constants.CompletedRetentionMonths) <= now;

    // Records a closed work package, or refreshes its status and resolution date. An existing
    // row keeps its detection time, so fetching it again during retention does not extend it.
    // Returns true when the work package was not recorded before.
    public bool MarkClosed(JiraIssue projectTask, JiraIssue scopingTask, DateTime now)
    {
        var isNew = !_entries.TryGetValue(scopingTask.Key, out var existing);
        _entries[scopingTask.Key] = new CompletedWorkPackage(
            projectTask.Key,
            scopingTask.Key,
            projectTask.Status,
            projectTask.Resolved is { } resolved ? DateOnly.FromDateTime(resolved) : null,
            isNew ? now : existing.DetectedAt);
        return isNew;
    }

    // For a project reopened during retention: it is tracked again from the next run.
    public bool Remove(string scopingKey) => _entries.Remove(scopingKey);

    public void Save()
    {
        var lines = _entries.Values
            .OrderBy(e => e.DetectedAt)
            .ThenBy(e => e.ProjectKey, StringComparer.Ordinal)
            .ThenBy(e => e.ProposalScopingKey, StringComparer.Ordinal)
            .Select(e => string.Join(';',
                e.ProjectKey,
                e.ProposalScopingKey,
                e.Status,
                e.Resolved?.ToString(DateFormat, CultureInfo.InvariantCulture) ?? "",
                e.DetectedAt.ToString(DateTimeFormat, CultureInfo.InvariantCulture)));

        // With a BOM, so Excel reads non-ASCII status names correctly.
        File.WriteAllLines(_path, [Header, .. lines], new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
    }

    private void AddParsed(CompletedWorkPackage entry)
    {
        if (entry is not null)
        {
            _entries[entry.ProposalScopingKey] = entry;
        }
    }

    // Keys and status names never contain ';', so no quoting is needed.
    private static CompletedWorkPackage ParseLine(string line)
    {
        var parts = line.Split(';');
        if (parts.Length < 2 || string.IsNullOrWhiteSpace(parts[1]))
        {
            return null;
        }

        DateOnly? resolved = parts.Length > 3
            && DateOnly.TryParseExact(parts[3].Trim(), DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date
            : null;

        // A row without a readable detection time counts as already past retention.
        var detectedAt = parts.Length > 4
            && DateTime.TryParseExact(parts[4].Trim(), DateTimeFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var time)
            ? time
            : DateTime.MinValue;

        return new CompletedWorkPackage(parts[0].Trim(), parts[1].Trim(), parts.Length > 2 ? parts[2].Trim() : "", resolved, detectedAt);
    }

    // The old format: "PROJ-1;PROJ-2;Auto-added on 2026-09-30. Status = Done". Rows added
    // by hand may have only the two keys. The old file never held a resolution date.
    private static CompletedWorkPackage ParseLegacyLine(string line)
    {
        var parts = line.Split(';', 3);
        if (parts.Length < 2 || string.IsNullOrWhiteSpace(parts[1]))
        {
            return null;
        }

        var note = parts.Length > 2 ? parts[2] : "";
        var added = Regex.Match(note, @"Auto-added on (\d{4}-\d{2}-\d{2})");
        var status = Regex.Match(note, @"Status = (.+)$");

        var detectedAt = added.Success
            ? DateTime.ParseExact(added.Groups[1].Value, DateFormat, CultureInfo.InvariantCulture)
            : DateTime.MinValue;

        return new CompletedWorkPackage(parts[0].Trim(), parts[1].Trim(),
            status.Success ? status.Groups[1].Value.Trim() : "", null, detectedAt);
    }
}
