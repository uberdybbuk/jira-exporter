using ClosedXML.Excel;

namespace FourArc.JiraExporter;

public class ExcelReportGenerator
{
    public void SaveResults(List<WorkPackage> results)
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

        workbook.SaveAs(Constants.ExcelReportFileName);
    }
}
