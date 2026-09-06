namespace ScheduleApp.PushListener.Services;

/// <summary>
/// Appends every raw upload body to data/raw_uploads-yyyyMMdd.log, unparsed, so nothing a
/// device sends (OPERLOG/BIODATA/etc., or anything else this listener doesn't otherwise parse)
/// is silently lost -- ported from AdmsServer, since extended with rotation.
///
/// Originally a single ever-growing raw_uploads.log. Now rolls to a new file each UTC day and
/// deletes files older than RetentionDays, so this can run unattended indefinitely without
/// slowly filling the disk -- the same problem the Serilog file sink in Program.cs solves for
/// the structured log, applied here too.
///
/// Kept as its own file rather than folded into the structured ILogger/Serilog pipeline: raw
/// device bodies are bulky, unparsed text (a whole ATTLOG batch per line) that would dominate
/// and clutter the JSON log stream if mixed in. This stays a dedicated, append-only record of
/// exactly what arrived on the wire -- unstructured on purpose, so nothing about how a device
/// phrases something can cause a parse failure here the way it could in a structured sink.
///
/// Registered as a singleton since the lock and the current file path need to be shared across
/// every request.
/// </summary>
public class RawUploadLogger
{
    private const int RetentionDays = 30;

    private readonly string _dataDir;
    private readonly object _lock = new();
    private string? _currentPath;
    private DateOnly _currentDate;

    public RawUploadLogger()
    {
        _dataDir = Path.Combine(AppContext.BaseDirectory, "data");
        Directory.CreateDirectory(_dataDir);
        lock (_lock)
        {
            RollIfNeeded();
        }
    }

    public void Append(string label, string content)
    {
        var entry = $"[{DateTime.UtcNow:O}] {label}\r\n{content}\r\n---\r\n";
        lock (_lock)
        {
            RollIfNeeded();
            File.AppendAllText(_currentPath!, entry);
        }
    }

    /// <summary>
    /// Switches to a new day's file when the UTC date has changed since the last append (or on
    /// first use), and sweeps old files at the same time -- once a day is often enough, there's
    /// no need to scan the directory on every single Append call. Must be called with _lock
    /// already held.
    /// </summary>
    private void RollIfNeeded()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        if (_currentPath is not null && today == _currentDate)
        {
            return;
        }

        _currentDate = today;
        _currentPath = Path.Combine(_dataDir, $"raw_uploads-{today:yyyyMMdd}.log");
        PurgeOldFiles();
    }

    private void PurgeOldFiles()
    {
        var cutoff = DateTime.UtcNow.AddDays(-RetentionDays);
        foreach (var file in Directory.EnumerateFiles(_dataDir, "raw_uploads-*.log"))
        {
            try
            {
                if (File.GetLastWriteTimeUtc(file) < cutoff)
                {
                    File.Delete(file);
                }
            }
            catch (IOException)
            {
                // Something else briefly has a handle on it, or it's already gone -- skip it
                // this pass, tomorrow's roll will retry.
            }
        }
    }
}
