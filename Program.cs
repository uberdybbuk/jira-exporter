using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Serilog;
using static FourArc.JiraExporter.JiraClient;

namespace FourArc.JiraExporter;

class Program
{
    static async Task Main(string[] args)
    {
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .Enrich.WithThreadId()
            .Enrich.WithMachineName()
            .WriteTo.Console(outputTemplate:
                "{Timestamp:yyyy-MM-dd HH:mm:ss,fff} [T{ThreadId}] [{Level:u4}] {SourceContext} - {Message:lj}{NewLine}{Exception}")
            .WriteTo.File(
                path: "log.log",
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 30,
                fileSizeLimitBytes: 50 * 1024 * 1024, // 50MB
                rollOnFileSizeLimit: true,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss,fff} [T{ThreadId}] [{Level:u4}] {SourceContext} - {Message:lj}{NewLine}{Exception}")
            .CreateLogger();

        using var loggerFactory = new LoggerFactory().AddSerilog();
        var logger = loggerFactory.CreateLogger<Program>();
        logger.LogInformation("Application started");

        AppSettings settings;
        try
        {
            settings = AppSettings.Load();
        }
        catch (Exception e)
        {
            logger.LogError("{Message}", e.Message);
            Log.CloseAndFlush();
            return;
        }

        var command = args.Length > 0 ? args[0].ToLowerInvariant() : "";
        switch (command)
        {
            case "fetch":
                logger.LogInformation("Running: fetch data from Jira");
                await FetchProposalScopingIssues(loggerFactory, settings);
                break;

            case "report":
                logger.LogInformation("Running: generate Excel & HTML report from saved data");
                GenerateReport(logger);
                break;

            case "all":
                logger.LogInformation("Running: fetch data from Jira and generate Excel & HTML report");
                var results = await FetchProposalScopingIssues(loggerFactory, settings);
                GenerateReport(logger);
                ExchangeEmailService.SendWithAttachment(
                    settings.Email.ReportRecipient, "Jira projeleri", "Ektedir.",
                    Constants.ExcelReportFileName, settings.Email.AutodiscoverAddress);
                break;

            case "checkforupdates":
                logger.LogInformation("Running: check for updates in Jira and send email if there are changes");
                await CheckForUpdatesAndNotify(loggerFactory, settings);
                break;

            case "test":
                logger.LogInformation("Running: test");
                await ExportSupportSlaReport(settings);
                break;

            default:
                Console.WriteLine("Usage: dotnet run -- <command>");
                Console.WriteLine("Commands:");
                Console.WriteLine("  fetch             Fetch data from Jira and save to disk");
                Console.WriteLine("  report            Generate HTML report from previously saved data");
                Console.WriteLine("  all               Fetch data from Jira, generate report, and send email");
                Console.WriteLine("  checkforupdates   Check for updates in Jira and send email if there are changes");
                break;
        }

        Log.CloseAndFlush();
    }

    private static async Task CheckForUpdatesAndNotify(ILoggerFactory loggerFactory, AppSettings settings)
    {
        var exporter = new JiraExporter(loggerFactory, settings.Jira);
        await exporter.CheckForUpdatesAsync();
    }

    private static void TestIssueHistory(AppSettings settings)
    {
        var loggerFactory = new LoggerFactory().AddSerilog();
        var logger = loggerFactory.CreateLogger<Program>();

        var issue = "ODEA-12125";

        var jc = new JiraClient(loggerFactory.CreateLogger<JiraClient>(), settings.Jira);
        ChangeLogResponse history = jc.DownloadIssueHistoryAsync(issue).GetAwaiter().GetResult();

        logger.LogInformation("Changelog for {Issue}:", issue);
        foreach (var historyItem in history.Changelog.Histories)
        {
            foreach (var item in historyItem.Items)
            {
                logger.LogInformation("  - {Created:yyyy-MM-dd HH:mm:ss zzz}: {Author} changed {Field} from '{From}' to '{To}'",
                                historyItem.Created, historyItem.Author?.DisplayName ?? "<Unknown user>", item.Field, item.FromString, item.ToString);
            }
        }
    }

    private static async Task<List<WorkPackage>> FetchProposalScopingIssues(ILoggerFactory loggerFactory, AppSettings settings)
    {
        var exporter = new JiraExporter(loggerFactory, settings.Jira);
        return await exporter.GetActiveProjects();
    }

    // Rebuilds the reports from whatever the last fetch wrote to disk.
    private static void GenerateReport(ILogger<Program> logger)
    {
        if (!Directory.Exists(Constants.ProjectInfoDirectory))
        {
            logger.LogError("Directory for project files '{Dir}' not found. Run 'fetch' first.", Constants.ProjectInfoDirectory);
            return;
        }

        var files = Directory.GetFiles(Constants.ProjectInfoDirectory, "*.json");
        if (files.Length == 0)
        {
            logger.LogWarning("No JSON files found in '{Dir}'. Run 'fetch' first.", Constants.ProjectInfoDirectory);
            return;
        }

        var proposalScopingIssues = JsonHelper.FromJson<List<JiraIssue>>(Constants.ActiveProposalScopingIssuesFileName);
        logger.LogInformation("Loaded {Count} proposal scoping issues from '{FileName}'", proposalScopingIssues.Count, Constants.ActiveProposalScopingIssuesFileName);

        List<WorkPackage> results = [];
        int counter = 0;
        foreach (var issue in proposalScopingIssues)
        {
            counter++;
            var projectKey = issue.ParentKey;
            var uniqueId = $"{projectKey}_{issue.Key}";
            var projectFile = Path.Combine(Constants.ProjectInfoDirectory, $"{uniqueId}.json");

            logger.LogInformation("Processing {Index}/{Total}: Loading project file for {UniqueId}", counter, proposalScopingIssues.Count, uniqueId);

            if (!File.Exists(projectFile))
            {
                // Some issues become invisible to this account once they close.
                logger.LogError("Project file '{FileName}' not found for unique ID {UniqueId}. Skipping...", projectFile, uniqueId);
                continue;
            }

            var workPackage = JsonHelper.FromJson<WorkPackage>(projectFile);
            results.Add(workPackage);
        }
        logger.LogInformation("Loaded {Count} project tasks.", results.Count);

        logger.LogInformation("Saving combined results to '{FileName}'", Constants.ProjectTasksFileName);
        JsonHelper.SaveAsJson(results, Constants.ProjectTasksFileName);


        new JsonReportGenerator().SaveResults(results);
        logger.LogInformation("JSON report saved to '{FileName}'", Constants.JsonReportFileName);

        new ExcelReportGenerator().SaveResults(results);
        logger.LogInformation("Excel report saved to '{FileName}'", Constants.ExcelReportFileName);
    }

    // relatedSla and timeToSla both come back as arrays with more than one entry.
    private static async Task ExportSupportSlaReport(AppSettings settings)
    {
        JiraClient jc = new JiraClient(new LoggerFactory().AddSerilog().CreateLogger<JiraClient>(), settings.Jira);
        List<JiraIssue> issues = await jc.Search(
            settings.Jira.SupportQuery, timeout: 10, ["relatedSla", "timeToSla", "company"]);

        var list = issues.Select(issue => new
        {
            issue.Key,
            issue.IssueType,
            issue.Status,
            issue.Company,
            issue.AssigneeOrgEmail,
            RelatedSLA = ParseSLA(issue.RelatedSLA).Item2.TotalMinutes,
            TimeToSLA = ParseSLA(issue.TimeToSLA).Item2.TotalMinutes,
            issue.Summary
        }).OrderBy(issue => issue.RelatedSLA)
        .ToList();

        var sb = new StringBuilder();
        sb.AppendLine("Issue Key,Type,Status,Company,Assignee Email,Related SLA,Time to SLA,Summary");
        foreach (var issue in list)
        {
            sb.AppendLine($"{issue.Key},{issue.IssueType},{issue.Status},{issue.Company},{issue.AssigneeOrgEmail},{issue.RelatedSLA},{issue.TimeToSLA},{EscapeCsvField(issue.Summary)}");
        }
        File.WriteAllText("support-sla.csv", sb.ToString());
    }

    private static string EscapeCsvField(string field)
    {
        if (field.Contains(',') || field.Contains('\"') || field.Contains('\n'))
        {
            return $"\"{field.Replace("\"", "\"\"")}\"";
        }
        return field;
    }

    private static (string, TimeSpan) ParseSLA(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return ("", TimeSpan.Zero);
        }

        int point = input.IndexOf(':');
        string left = input.Substring(0, point).Trim();
        string right = input.Substring(point + 1).Trim();

        // Each unit (d/h/m/s) is optional and may appear in any order, e.g.
        // "3d 32m 49s", "5d", "3h 22m 5s", "-42m 17s", "2d 4h 34s".
        bool negative = right.StartsWith('-');

        Regex regex = new Regex(@"(\d+)\s*([dhms])", RegexOptions.Compiled);
        MatchCollection matches = regex.Matches(right);
        if (matches.Count > 0)
        {
            try
            {
                int days = 0, hours = 0, minutes = 0, seconds = 0;
                foreach (Match match in matches)
                {
                    int value = int.Parse(match.Groups[1].Value);
                    switch (match.Groups[2].Value)
                    {
                        case "d": days = value; break;
                        case "h": hours = value; break;
                        case "m": minutes = value; break;
                        // Seconds are deliberately ignored.
                    }
                }

                TimeSpan timeSpan = new TimeSpan(days, hours, minutes, seconds);
                if (negative)
                {
                    timeSpan = timeSpan.Negate();
                }

                return (left, timeSpan);
            }
            catch (Exception)
            {
                Console.WriteLine($"Error parsing SLA string '{input}' {left} {right}. Returning TimeSpan.Zero.");
                return (left, TimeSpan.Zero);
            }
        }
        else
        {
            Console.WriteLine($"Input string '{input}' is not in the expected format.");
            return (left, TimeSpan.Zero);
        }
    }
}
