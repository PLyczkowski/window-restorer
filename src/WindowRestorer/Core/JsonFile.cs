using System.Text.Json;
using System.Text.Json.Serialization;

namespace WindowRestorer.Core;

/// <summary>Reads and atomically writes the app's JSON files.</summary>
public static class JsonFile
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>
    /// Loads <paramref name="path"/>, or returns a new object if it doesn't exist.
    /// A broken file is kept aside as *.broken-&lt;time&gt; and logged, never silently dropped.
    /// </summary>
    public static T Load<T>(string path) where T : new()
    {
        if (!File.Exists(path)) return new T();
        try
        {
            var value = JsonSerializer.Deserialize<T>(File.ReadAllText(path), Options);
            if (value != null) return value;
            throw new JsonException("File contains null.");
        }
        catch (JsonException ex)
        {
            var backup = $"{path}.broken-{DateTime.Now:yyyyMMdd-HHmmss}";
            File.Move(path, backup);
            Log.Error($"Could not read {path}; moved it to {backup} and started fresh", ex);
            return new T();
        }
    }

    public static void Save<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(value, Options));
        File.Move(temp, path, overwrite: true);
    }
}
