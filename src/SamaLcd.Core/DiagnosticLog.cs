using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace SamaLcd.Core;

/// <summary>Local, bounded JSON-lines logs. Logging failures never escape into the application.</summary>
public sealed class DiagnosticLog : IDisposable
{
    private readonly object _sync = new();
    private readonly long _maximumBytes;
    private readonly int _retainedFiles;
    private FileStream? _stream;
    private int _part;
    private bool _disposed;
    private volatile bool _debugEnabled;
    public string DirectoryPath { get; }
    public string SessionId { get; } = $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Environment.ProcessId}-{Guid.NewGuid():N}";
    public string CurrentFile { get; private set; } = "";
    public string? LastError { get; private set; }
    public bool DebugEnabled
    {
        get => _debugEnabled;
        set { _debugEnabled = value; Write("INFO", "diagnostics.debug", new { enabled = value }); }
    }
    private string SessionFile => Path.Combine(DirectoryPath, "last-session.json");

    public DiagnosticLog(string directory, bool debugEnabled = false, long maximumBytes = 2 * 1024 * 1024, int retainedFiles = 10)
    {
        DirectoryPath = Path.GetFullPath(directory);
        _debugEnabled = debugEnabled;
        _maximumBytes = Math.Max(1024, maximumBytes);
        _retainedFiles = Math.Max(2, retainedFiles);
        try
        {
            Directory.CreateDirectory(DirectoryPath);
            if (File.Exists(SessionFile))
            {
                using var previous = JsonDocument.Parse(File.ReadAllText(SessionFile));
                if (!previous.RootElement.TryGetProperty("completed", out var completed) || !completed.GetBoolean())
                    Write("WARN", "session.previous_incomplete", previous.RootElement.Clone());
            }
        }
        catch (Exception e) { LastError = e.Message; }
        Write("INFO", "session.start", RuntimeInfo());
        WriteSession(false);
    }

    public static object RuntimeInfo() => new
    {
        executable = Environment.ProcessPath,
        version = Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
        assemblyModifiedUtc = File.Exists(Assembly.GetEntryAssembly()?.Location) ? File.GetLastWriteTimeUtc(Assembly.GetEntryAssembly()!.Location) : (DateTime?)null,
        framework = RuntimeInformation.FrameworkDescription, os = RuntimeInformation.OSDescription,
        architecture = RuntimeInformation.ProcessArchitecture.ToString(), memoryBytes = Environment.WorkingSet,
        managedMemoryBytes = GC.GetTotalMemory(false), processId = Environment.ProcessId,
    };

    public void Write(string level, string eventName, object? data = null, Exception? exception = null, string? operationId = null)
    {
        if (level == "DEBUG" && !_debugEnabled) return;
        lock (_sync)
        {
            if (_disposed) return;
            try
            {
                var row = JsonSerializer.Serialize(new
                {
                    timeUtc = DateTimeOffset.UtcNow, level, session = SessionId,
                    processId = Environment.ProcessId, threadId = Environment.CurrentManagedThreadId,
                    eventName, operationId, data, exception = exception == null ? null : Clip(exception.ToString(), 32768),
                });
                if (row.Length > 65536) row = JsonSerializer.Serialize(new { timeUtc = DateTimeOffset.UtcNow, level, session = SessionId, eventName, operationId, truncated = Clip(row, 32768) });
                var bytes = Encoding.UTF8.GetBytes(row + "\n");
                if (_stream == null || _stream.Length + bytes.Length > _maximumBytes) OpenPart();
                _stream!.Write(bytes);
                _stream.Flush(true); // Native WebView2 failures can terminate without managed exception handlers.
            }
            catch (Exception e) { LastError = e.Message; }
        }
    }

    private void OpenPart()
    {
        _stream?.Dispose(); _stream = null;
        Directory.CreateDirectory(DirectoryPath);
        CurrentFile = Path.Combine(DirectoryPath, $"samalcd-{SessionId}-{_part++:D3}.log");
        _stream = new FileStream(CurrentFile, FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite);
        var old = new DirectoryInfo(DirectoryPath).GetFiles("samalcd-*.log")
            .Where(f => f.FullName != CurrentFile).OrderByDescending(f => f.LastWriteTimeUtc).Skip(_retainedFiles - 1);
        foreach (var file in old) { try { file.Delete(); } catch { /* another process may still be reading an old log */ } }
    }

    private void WriteSession(bool completed)
    {
        try { File.WriteAllText(SessionFile, JsonSerializer.Serialize(new { session = SessionId, processId = Environment.ProcessId, timeUtc = DateTimeOffset.UtcNow, completed, logFile = CurrentFile })); }
        catch (Exception e) { LastError = e.Message; }
    }

    public void CompleteSession()
    {
        lock (_sync) { if (_disposed) return; Write("INFO", "session.exit"); WriteSession(true); }
    }

    public void Export(string destination, object snapshot, string? windowsEvents = null)
    {
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            lock (_sync)
            {
                using var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write);
                using var archive = new ZipArchive(file, ZipArchiveMode.Create);
                _stream?.Flush(true);
                foreach (var path in Directory.EnumerateFiles(DirectoryPath, "samalcd-*.log").Order().TakeLast(_retainedFiles))
                {
                    using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    using var output = archive.CreateEntry("logs/" + Path.GetFileName(path)).Open();
                    input.CopyTo(output);
                }
                using (var writer = new StreamWriter(archive.CreateEntry("diagnostics.json").Open()))
                    writer.Write(JsonSerializer.Serialize(new { session = SessionId, runtime = RuntimeInfo(), debugEnabled = _debugEnabled, logError = LastError, snapshot }, new JsonSerializerOptions { WriteIndented = true }));
                if (windowsEvents != null)
                {
                    using var writer = new StreamWriter(archive.CreateEntry("windows-crashes.txt").Open());
                    writer.Write(windowsEvents);
                }
            }
            File.Move(temporary, destination, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public static string Clip(string? text, int length = 2048) => text == null ? "" : text.Length <= length ? text : text[..length] + " [truncated]";
    public void Dispose() { lock (_sync) { _disposed = true; _stream?.Dispose(); _stream = null; } }
}

public static class AppLog
{
    public static DiagnosticLog? Current { get; set; }
    private static readonly AsyncLocal<string?> Context = new();
    public static void Info(string name, object? data = null) => Current?.Write("INFO", name, data, operationId: Context.Value);
    public static void Debug(string name, object? data = null) => Current?.Write("DEBUG", name, data, operationId: Context.Value);
    public static void Error(string name, Exception error, object? data = null) => Current?.Write("ERROR", name, data, error, Context.Value);
    public static LogOperation Operation(string name, object? data = null) => new(name, data);

    public sealed class LogOperation : IDisposable
    {
        private readonly string _name, _id = Guid.NewGuid().ToString("N");
        private readonly string? _parent = Context.Value;
        private readonly Stopwatch _time = Stopwatch.StartNew();
        private bool _failed;
        internal LogOperation(string name, object? data)
        {
            _name = name; Context.Value = _id;
            Info(name + ".begin", data);
        }
        public void Fail(Exception e) { _failed = true; Error(_name + ".failed", e); }
        public void Dispose()
        {
            Info(_name + ".end", new { elapsedMs = _time.ElapsedMilliseconds, succeeded = !_failed });
            Context.Value = _parent;
        }
    }
}
