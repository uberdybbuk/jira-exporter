using Microsoft.Extensions.Logging;
using Serilog;

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
                await FetchProposalScopingIssues(loggerFactory, settings);
                GenerateReport(logger);
                ExchangeEmailService.SendWithAttachment(
                    settings.Email.ReportRecipient, "Jira projeleri", "Ektedir.",
                    Constants.ExcelReportFileName, settings.Email.AutodiscoverAddress);
                break;

            case "checkforupdates":
                logger.LogInformation("Running: check for updates in Jira and send email if there are changes");
                await CheckForUpdatesAndNotify(loggerFactory, settings);
                break;

            default:
                Console.WriteLine("Usage: dotnet run -- <command>");
                Console.WriteLine("Commands:");
                Console.WriteLine("  fetch             Fetch data from Jira and save to disk");
                Console.WriteLine("  report            Generate HTML report from previously saved data");
                Console.WriteLine("  all               Fetch data from Jira, generate report, and send email");
                Console.WriteLine("  checkforupdates   Report what changed since the last fetch and email the differences");
                break;
        }

        Log.CloseAndFlush();
    }

    private static async Task CheckForUpdatesAndNotify(ILoggerFactory loggerFactory, AppSettings settings)
    {
        var logger = loggerFactory.CreateLogger<Program>();
        var exporter = new JiraExporter(loggerFactory, settings.Jira);

        var report = await exporter.CheckForUpdatesAsync();
        if (!report.HasChanges)
        {
            logger.LogInformation("Nothing changed since the last snapshot; no mail sent.");
            return;
        }

        // The report goes in the mail body, not in an attachment.
        ExchangeEmailService.SendHtml(
            settings.Email.ReportRecipient,
            report.Subject,
            ChangeReportHtml.Render(report, settings.Jira.BaseApiUrl),
            settings.Email.AutodiscoverAddress);
        logger.LogInformation("Sent '{Subject}' to {Recipient}", report.Subject, settings.Email.ReportRecipient);

        await exporter.CommitSnapshotAsync(report);
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
}
