using System.Text.Json;
using System.Text.Json.Serialization;

namespace SmartWiFi.Core.Storage;

/// <summary>Reads and writes a JSON file atomically (write to .tmp, then replace).</summary>
public static class JsonFile
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static T Load<T>(string path) where T : new()
    {
        try
        {
            if (!File.Exists(path)) return new T();
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<T>(json, Options) ?? new T();
        }
        catch (JsonException)
        {
            // Keep the broken file for inspection instead of silently losing it.
            try { File.Copy(path, path + ".broken", overwrite: true); } catch (IOException) { }
            return new T();
        }
    }

    public static void Save<T>(string path, T value)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(value, Options));
        File.Move(tmp, path, overwrite: true);
    }
}
