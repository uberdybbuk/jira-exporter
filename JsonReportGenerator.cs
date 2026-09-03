using System.Text.Json;

namespace FourArc.JiraExporter;

public class JsonReportGenerator
{
    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        WriteIndented = true,
    };

    public void SaveResults(List<WorkPackage> results)
    {
        var columns = ReportColumns.All;

        var rows = results.Select(item =>
        {
            var row = new Dictionary<string, object>();
            foreach (var col in columns)
            {
                var rawValue = col.ValueSelector(item);
                row[col.Id] = rawValue switch
                {
                    DateTime dt => dt.ToString("yyyy-MM-dd HH:mm:ss"),
                    DateOnly d => d.ToString("yyyy-MM-dd"),
                    decimal dec => dec,
                    null => null,
                    _ => col.Formatter(rawValue)
                };
            }
            return row;
        }).ToList();

        var json = JsonSerializer.Serialize(rows, s_jsonOptions);
        File.WriteAllText(Constants.JsonReportFileName, json);
    }
}
