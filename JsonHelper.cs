using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;

namespace FourArc.JiraExporter;

public static class JsonHelper
{
    public static readonly JsonSerializerOptions JsonSerializerOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All) // tüm Unicode karakterlerini normal yaz
    };

    public static string ToJson(this object obj)
    {
        return JsonSerializer.Serialize(obj, JsonSerializerOptions);
    }

    public static void SaveAsJson(this object obj, string filename)
    {
        string json = obj.ToJson();
        File.WriteAllText(filename, json);
    }

    public static async Task SaveAsJsonAsync(this object obj, string filename)
    {
        string json = obj.ToJson();
        await File.WriteAllTextAsync(filename, json);
    }

    public static T FromJson<T>(string filename)
    {
        string json = File.ReadAllText(filename);
        return JsonSerializer.Deserialize<T>(json, JsonSerializerOptions);
    }

    public static async Task<T> FromJsonAsync<T>(string filename)
    {
        string json = await File.ReadAllTextAsync(filename);
        return JsonSerializer.Deserialize<T>(json, JsonSerializerOptions);
    }
}
