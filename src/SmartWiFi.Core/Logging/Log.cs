using System.Collections.Concurrent;

namespace SmartWiFi.Core.Logging;

public enum LogLevel { Info, Ok, Warn, Error }

public sealed record LogEntry(DateTime Time, LogLevel Level, string Message)
{
    public override string ToString() => $"{Time:yyyy-MM-dd HH:mm:ss} [{Level.ToString().ToUpperInvariant(),-5}] {Message}";
}

public interface ILog
{
    void Write(LogLevel level, string message);
    IReadOnlyList<LogEntry> Recent { get; }
    event Action<LogEntry>? Logged;
}

public static class LogExtensions
{
    public static void Info(this ILog log, string m) => log.Write(LogLevel.Info, m);
    public static void Ok(this ILog log, string m) => log.Write(LogLevel.Ok, m);
    public static void Warn(this ILog log, string m) => log.Write(LogLevel.Warn, m);
    public static void Error(this ILog log, string m) => log.Write(LogLevel.Error, m);
}

/// <summary>Keeps the last few hundred lines in memory and, optionally, appends to one file per day.</summary>
public sealed class Log : ILog
{
    private const int Keep = 300;
    private readonly ConcurrentQueue<LogEntry> _recent = new();
    private readonly string? _directory;
    private readonly IClock _clock;
    private readonly object _fileLock = new();

    public Log(string? directory, IClock? clock = null)
    {
        _directory = directory;
        _clock = clock ?? new SystemClock();
        if (_directory is not null)
        {
            Directory.CreateDirectory(_directory);
            PruneOldFiles();
        }
    }

    public event Action<LogEntry>? Logged;

    public IReadOnlyList<LogEntry> Recent => _recent.ToArray();

    public string? CurrentFile => _directory is null ? null : Path.Combine(_directory, $"smartwifi-{_clock.Now:yyyyMMdd}.log");

    public void Write(LogLevel level, string message)
    {
        var entry = new LogEntry(_clock.Now, level, message);
        _recent.Enqueue(entry);
        while (_recent.Count > Keep && _recent.TryDequeue(out _)) { }

        if (CurrentFile is { } file)
        {
            try
            {
                lock (_fileLock) File.AppendAllText(file, entry + Environment.NewLine);
            }
            catch (IOException) { /* never let logging take the app down */ }
            catch (UnauthorizedAccessException) { }
        }

        Logged?.Invoke(entry);
    }

    private void PruneOldFiles()
    {
        try
        {
            foreach (var f in Directory.GetFiles(_directory!, "smartwifi-*.log"))
                if (File.GetLastWriteTime(f) < DateTime.Now.AddDays(-14)) File.Delete(f);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
