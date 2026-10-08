using ClosedXML.Excel;

namespace FourArc.JiraExporter;

public class ExcelReportGenerator
{
    // Issue keys in these Relations columns become links to Jira.
    private static readonly HashSet<string> s_relationKeyColumns = new(StringComparer.Ordinal) { "ProjectKey", "RelatedKey" };

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
    // out of a crowded cell on the main sheet. Every value is written as text and a
    // missing one leaves the cell blank, so an export to CSV reads back unchanged.
    private static void AddRelationsSheet(XLWorkbook workbook, List<RelationRow> relations, string browseUrl)
    {
        var columns = ReportRelations.Columns;
        var worksheet = workbook.Worksheets.Add("Relations");

        for (int col = 0; col < columns.Length; col++)
        {
            var cell = worksheet.Cell(1, col + 1);
            cell.Value = columns[col].Header;
            cell.Style.Font.Bold = true;
            cell.Style.Fill.BackgroundColor = XLColor.LightGray;
        }

        for (int i = 0; i < relations.Count; i++)
        {
            for (int col = 0; col < columns.Length; col++)
            {
                var value = columns[col].Value(relations[i]);
                if (string.IsNullOrEmpty(value))
                {
                    continue;
                }

                var cell = worksheet.Cell(i + 2, col + 1);
                cell.Value = value;
                if (s_relationKeyColumns.Contains(columns[col].Header) && !string.IsNullOrEmpty(browseUrl))
                {
                    cell.SetHyperlink(new XLHyperlink(new Uri(browseUrl + value)));
                }
            }
        }

        worksheet.Columns().AdjustToContents();

        worksheet.RangeUsed()?.SetAutoFilter();
        worksheet.SheetView.FreezeRows(1);
    }
}
