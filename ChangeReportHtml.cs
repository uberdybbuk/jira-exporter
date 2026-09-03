using System.Net;
using System.Text;

namespace FourArc.JiraExporter;

// Renders a change report as the mail body. Everything is inline-styled and
// table-based because Outlook strips stylesheets, and it goes in the body rather
// than in an attachment so there is no file for mail scanning to reject.
public static class ChangeReportHtml
{
    private const string TableStyle = "border-collapse:collapse;font-family:Segoe UI,Arial,sans-serif;font-size:13px;width:100%";
    private const string HeaderCellStyle = "border:1px solid #ccc;padding:4px 8px;background:#eee;text-align:left;font-weight:bold";
    private const string CellStyle = "border:1px solid #ccc;padding:4px 8px;vertical-align:top";
    private const string SectionStyle = "font-family:Segoe UI,Arial,sans-serif;font-size:15px;font-weight:bold;margin:24px 0 8px 0";

    public static string Render(ChangeReport report, string baseApiUrl)
    {
        var browseUrl = ToBrowseUrl(baseApiUrl);
        var html = new StringBuilder();

        html.Append("<div style=\"font-family:Segoe UI,Arial,sans-serif;font-size:13px\">");

        if (report.Added.Count > 0)
        {
            AppendSection(html, $"New work packages ({report.Added.Count})");
            AppendWorkPackageTable(html, report.Added, browseUrl);
        }

        if (report.Changed.Count > 0)
        {
            AppendSection(html, $"Changed ({report.Changed.Count})");
            foreach (var changed in report.Changed)
            {
                AppendChangeBlock(html, changed, browseUrl);
            }
        }

        if (report.Removed.Count > 0)
        {
            AppendSection(html, $"No longer matched by the query ({report.Removed.Count})");
            AppendWorkPackageTable(html, report.Removed, browseUrl);
        }

        if (report.Unreadable.Count > 0)
        {
            AppendSection(html, $"Project issue no longer readable ({report.Unreadable.Count})");
            html.Append("<div style=\"margin-bottom:8px;color:#666\">The scoping issue still matches, but its project issue did not come back. This is usually a permission change.</div>");
            AppendWorkPackageTable(html, report.Unreadable, browseUrl);
        }

        html.Append("</div>");
        return html.ToString();
    }

    private static void AppendSection(StringBuilder html, string title) =>
        html.Append($"<div style=\"{SectionStyle}\">{Escape(title)}</div>");

    private static void AppendWorkPackageTable(StringBuilder html, List<WorkPackage> items, string browseUrl)
    {
        html.Append($"<table style=\"{TableStyle}\"><tr>");
        foreach (var header in new[] { "Project", "Scoping", "Summary", "Status", "Assignee", "Estimation", "Budget" })
        {
            html.Append($"<th style=\"{HeaderCellStyle}\">{Escape(header)}</th>");
        }
        html.Append("</tr>");

        foreach (var item in items)
        {
            html.Append("<tr>");
            html.Append($"<td style=\"{CellStyle}\">{IssueLink(item.ProjectTask?.Key, browseUrl)}</td>");
            html.Append($"<td style=\"{CellStyle}\">{IssueLink(item.ProposalScopingTask?.Key, browseUrl)}</td>");
            html.Append($"<td style=\"{CellStyle}\">{Escape(item.ProjectTask?.Summary)}</td>");
            html.Append($"<td style=\"{CellStyle}\">{Escape(item.ProjectTask?.Status)}</td>");
            html.Append($"<td style=\"{CellStyle}\">{Escape(ReportColumns.TrimAfterDash(item.ProjectTask?.AssigneeDisplayName))}</td>");
            html.Append($"<td style=\"{CellStyle}\">{Escape(item.ProposalScopingTask?.Estimation?.ToString())}</td>");
            html.Append($"<td style=\"{CellStyle}\">{Escape(item.ProposalScopingTask?.SalesForceBudget?.ToString())}</td>");
            html.Append("</tr>");
        }

        html.Append("</table>");
    }

    private static void AppendChangeBlock(StringBuilder html, ChangedWorkPackage changed, string browseUrl)
    {
        var item = changed.WorkPackage;

        html.Append("<div style=\"margin:14px 0 4px 0\">");
        html.Append($"<b>{IssueLink(item.ProjectTask?.Key, browseUrl)}</b> / {IssueLink(item.ProposalScopingTask?.Key, browseUrl)} &nbsp; {Escape(item.ProjectTask?.Summary)}");
        html.Append("</div>");

        html.Append($"<table style=\"{TableStyle}\"><tr>");
        foreach (var header in new[] { "Field", "Before", "After" })
        {
            html.Append($"<th style=\"{HeaderCellStyle}\">{Escape(header)}</th>");
        }
        html.Append("</tr>");

        foreach (var change in changed.Changes)
        {
            html.Append("<tr>");
            html.Append($"<td style=\"{CellStyle}\">{Escape(change.FieldName)}</td>");
            html.Append($"<td style=\"{CellStyle};color:#888\">{Escape(Blank(change.PreviousValue))}</td>");
            html.Append($"<td style=\"{CellStyle}\"><b>{Escape(Blank(change.CurrentValue))}</b></td>");
            html.Append("</tr>");
        }

        html.Append("</table>");
    }

    private static string IssueLink(string key, string browseUrl)
    {
        if (string.IsNullOrEmpty(key))
        {
            return "";
        }

        return string.IsNullOrEmpty(browseUrl)
            ? Escape(key)
            : $"<a href=\"{Escape(browseUrl)}{Escape(key)}\">{Escape(key)}</a>";
    }

    // Turns ".../rest/api/latest" into ".../browse/" so keys become links. Any
    // other shape yields an empty prefix and the keys are rendered as plain text.
    private static string ToBrowseUrl(string baseApiUrl)
    {
        if (string.IsNullOrWhiteSpace(baseApiUrl) || !Uri.TryCreate(baseApiUrl, UriKind.Absolute, out var uri))
        {
            return "";
        }

        var index = uri.AbsoluteUri.IndexOf("/rest/", StringComparison.OrdinalIgnoreCase);
        return index < 0 ? "" : $"{uri.AbsoluteUri[..index]}/browse/";
    }

    private static string Blank(string value) => string.IsNullOrEmpty(value) ? "—" : value;

    private static string Escape(string value) => WebUtility.HtmlEncode(value ?? "");
}
