using System.Text;

namespace WindowRestorer.Core;

/// <summary>Simple thread-safe file log. Rolls over to log.old.txt at 2 MB.</summary>
public static class Log
{
    private const long MaxBytes = 2 * 1024 * 1024;
    private static readonly object Gate = new();
    private static string? _path;
    private static readonly UTF8Encoding Utf8NoBom = new(false);

    public static void Init(string path)
    {
        lock (Gate) _path = path;
    }

    public static void Info(string message) => Write("INFO", message);
    public static void Warn(string message) => Write("WARN", message);
    public static void Error(string message, Exception? ex = null) => Write("ERROR", ex == null ? message : $"{message}: {ex}");

    private static void Write(string level, string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {level,-5} {message}{Environment.NewLine}";
        System.Diagnostics.Debug.Write(line);
        lock (Gate)
        {
            if (_path == null) return; // Not initialised (e.g. inside unit tests): debug output only.
            try
            {
                var info = new FileInfo(_path);
                if (info.Exists && info.Length > MaxBytes)
                {
                    File.Move(_path, Path.ChangeExtension(_path, ".old.txt"), overwrite: true);
                }
                File.AppendAllText(_path, line, Utf8NoBom);
            }
            catch (IOException)
            {
                // Logging must never take the app down; the debug output above still has the line.
            }
        }
    }
}
