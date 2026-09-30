using ClosedXML.Excel;

namespace FourArc.JiraExporter;

public class ExcelReportGenerator
{
    private static readonly string[] s_relationHeaders =
        ["Project", "Kind", "Link Type", "Relation", "Related Issue", "Summary", "Issue Type", "Status"];

    // browseUrl turns issue keys on the Relations sheet into links; empty leaves them as text.
    public void SaveResults(List<WorkPackage> results, string browseUrl = "")
    {
        var columns = ReportColumns.All;
        using var workbook = new XLWorkbook();
        var worksheet = workbook.Worksheets.Add("Report");

        for (int col = 0; col < columns.Count; col++)
        {
            var cell = worksheet.Cell(1, col + 1);
            cell.Value = columns[col].Header;
            cell.Style.Font.Bold = true;
            cell.Style.Fill.BackgroundColor = XLColor.LightGray;
        }

        for (int row = 0; row < results.Count; row++)
        {
            var item = results[row];
            for (int col = 0; col < columns.Count; col++)
            {
                var columnConfig = columns[col];
                var rawValue = columnConfig.ValueSelector(item);
                var cell = worksheet.Cell(row + 2, col + 1);

                // Numbers and dates go in typed so Excel can sort and format them.
                if (rawValue is decimal decimalVal)
                {
                    cell.Value = (double)decimalVal;
                }
                else if (rawValue is int intVal)
                {
                    cell.Value = intVal;
                }
                else if (rawValue is DateTime dateTimeVal)
                {
                    cell.Value = dateTimeVal;
                    cell.Style.DateFormat.Format = "yyyy-MM-dd";
                }
                else if (rawValue is DateOnly dateOnlyVal)
                {
                    cell.Value = dateOnlyVal.ToDateTime(TimeOnly.MinValue);
                    cell.Style.DateFormat.Format = "yyyy-MM-dd";
                }
                else
                {
                    cell.Value = columnConfig.Formatter(rawValue);
                }
            }
        }

        worksheet.Columns().AdjustToContents();

        worksheet.RangeUsed()?.SetAutoFilter();

        AddRelationsSheet(workbook, ReportRelations.Build(results), browseUrl);

        workbook.SaveAs(Constants.ExcelReportFileName);
    }

    // One row per relation, so relations can be filtered and pivoted rather than read
    // out of a crowded cell on the main sheet.
    private static void AddRelationsSheet(XLWorkbook workbook, List<RelationRow> relations, string browseUrl)
    {
        var worksheet = workbook.Worksheets.Add("Relations");

        for (int col = 0; col < s_relationHeaders.Length; col++)
        {
            var cell = worksheet.Cell(1, col + 1);
            cell.Value = s_relationHeaders[col];
            cell.Style.Font.Bold = true;
            cell.Style.Fill.BackgroundColor = XLColor.LightGray;
        }

        for (int i = 0; i < relations.Count; i++)
        {
            var relation = relations[i];
            int row = i + 2;

            SetIssueKey(worksheet.Cell(row, 1), relation.ProjectKey, browseUrl);
            worksheet.Cell(row, 2).Value = relation.Kind;
            worksheet.Cell(row, 3).Value = relation.LinkType;
            worksheet.Cell(row, 4).Value = relation.Relation;
            SetIssueKey(worksheet.Cell(row, 5), relation.Key, browseUrl);
            worksheet.Cell(row, 6).Value = relation.Summary;
            worksheet.Cell(row, 7).Value = relation.IssueType;
            worksheet.Cell(row, 8).Value = relation.Status;
        }

        worksheet.Columns().AdjustToContents();

        worksheet.RangeUsed()?.SetAutoFilter();
        worksheet.SheetView.FreezeRows(1);
    }

    private static void SetIssueKey(IXLCell cell, string key, string browseUrl)
    {
        cell.Value = key;
        if (!string.IsNullOrEmpty(key) && !string.IsNullOrEmpty(browseUrl))
        {
            cell.SetHyperlink(new XLHyperlink(new Uri(browseUrl + key)));
        }
    }
}
