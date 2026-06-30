using System.Runtime.InteropServices;
using System.Text;

namespace StockResearchAgent.Api.Diagnostics;

/// <summary>
/// Ultra-lightweight startup logger that writes to Console + file before
/// the ASP.NET Core hosting/logging pipeline is available.
///
/// Targets:
///   1. Console (stdout) — always
///   2. Azure App Service log path (D:\home\LogFiles or /home/LogFiles)
///   3. Fallback: ./startup-diagnostics.log
///
/// Rules:
///   • Never throws — every write is wrapped in try/catch.
///   • Never logs secrets, API keys, or full env var values.
///   • Thread-safe via lock.
/// </summary>
public static class BootstrapLogger
{
    private static readonly object Lock = new();
    private static string? _logFilePath;
    private static readonly StringBuilder _buffer = new();
    private static readonly DateTimeOffset _bootTime = DateTimeOffset.UtcNow;

    /// <summary>The resolved log file path, or null if file logging failed.</summary>
    public static string? LogFilePath => _logFilePath;

    /// <summary>All log lines captured so far (for /api/debug/startup).</summary>
    public static string CapturedLog
    {
        get { lock (Lock) { return _buffer.ToString(); } }
    }

    /// <summary>UTC timestamp when the process started.</summary>
    public static DateTimeOffset BootTime => _bootTime;

    /// <summary>
    /// Call once at the very top of Program.cs to resolve the log file path.
    /// </summary>
    public static void Init()
    {
        _logFilePath = ResolveLogPath();
        Log("BOOT 001", "Process started");
        Log("BOOT 002", $"UTC timestamp: {_bootTime:O}");
    }

    /// <summary>
    /// Write a checkpoint line to all targets.
    /// </summary>
    public static void Log(string checkpoint, string message)
    {
        var line = $"[{DateTimeOffset.UtcNow:HH:mm:ss.fff}] {checkpoint}: {message}";

        lock (Lock)
        {
            _buffer.AppendLine(line);
        }

        // 1. Console — always
        try { Console.WriteLine(line); } catch { /* swallow */ }

        // 2. File — best-effort
        if (_logFilePath is not null)
        {
            try { File.AppendAllText(_logFilePath, line + Environment.NewLine); }
            catch { /* swallow */ }
        }
    }

    /// <summary>
    /// Log a fatal exception before the process exits.
    /// </summary>
    public static void LogFatal(Exception ex)
    {
        Log("BOOT FATAL", $"{ex.GetType().Name}: {ex.Message}");
        Log("BOOT FATAL", $"StackTrace: {ex.StackTrace}");
        if (ex.InnerException is { } inner)
        {
            Log("BOOT FATAL", $"Inner: {inner.GetType().Name}: {inner.Message}");
        }
    }

    // -----------------------------------------------------------------

    private static string? ResolveLogPath()
    {
        // Azure App Service paths
        var candidates = new List<string>();

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            candidates.Add(@"D:\home\LogFiles\startup-diagnostics.log");

        candidates.Add("/home/LogFiles/startup-diagnostics.log");
        candidates.Add("./startup-diagnostics.log");

        foreach (var path in candidates)
        {
            try
            {
                var dir = Path.GetDirectoryName(path);
                if (dir is not null && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                // Test write
                File.AppendAllText(path, $"[{DateTimeOffset.UtcNow:O}] Bootstrap logger initialized{Environment.NewLine}");
                return path;
            }
            catch
            {
                // Try next candidate
            }
        }

        return null;
    }
}
