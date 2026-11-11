using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Extensions.Logging;

namespace Nexus.Infrastructure.Logging;

public sealed record LogEntrySnapshot(
    DateTimeOffset Timestamp,
    string Level,
    string SourceContext,
    string Message,
    string? ExceptionMessage);

/// <summary>
/// Thread-safe ring-buffer sink enabling Desktop and Mobile diagnostic UIs to inspect live structured logs.
/// </summary>
public sealed class InMemoryLogSink : ILogEventSink
{
    private readonly int _capacity;
    private readonly ConcurrentQueue<LogEntrySnapshot> _entries = new();

    public InMemoryLogSink(int capacity = 250)
    {
        _capacity = Math.Max(20, capacity);
    }

    public event Action<LogEntrySnapshot>? LogRecorded;

    public void Emit(LogEvent logEvent)
    {
        var source = logEvent.Properties.TryGetValue("SourceContext", out var sc)
            ? sc.ToString().Trim('"')
            : "NEXUS";

        var snapshot = new LogEntrySnapshot(
            logEvent.Timestamp,
            logEvent.Level.ToString(),
            source,
            logEvent.RenderMessage(),
            logEvent.Exception?.Message);

        _entries.Enqueue(snapshot);
        while (_entries.Count > _capacity && _entries.TryDequeue(out _))
        {
        }

        LogRecorded?.Invoke(snapshot);
    }

    public IReadOnlyList<LogEntrySnapshot> GetRecent(int maxCount = 100)
        => _entries.Reverse().Take(Math.Clamp(maxCount, 1, _capacity)).ToArray();
}

public sealed class NexusLoggingOptions
{
    public string ApplicationName { get; set; } = "NEXUS";
    public string LogsDirectory { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "NEXUS",
        "logs");
    public LogEventLevel MinimumLevel { get; set; } = LogEventLevel.Information;
    public bool EnableConsole { get; set; } = true;
    public bool EnableFile { get; set; } = true;
}

public static class NexusLoggingBuilder
{
    public static Serilog.ILogger CreateLogger(NexusLoggingOptions options, InMemoryLogSink? memorySink = null)
    {
        var config = new LoggerConfiguration()
            .MinimumLevel.Is(options.MinimumLevel)
            .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
            .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
            .Enrich.FromLogContext()
            .Enrich.WithProperty("Application", options.ApplicationName);

        const string outputTemplate = "[{Timestamp:yyyy-MM-dd HH:mm:ss.fff} {Level:u3}] [{Application}] {SourceContext}: {Message:lj}{NewLine}{Exception}";

        if (options.EnableConsole)
        {
            config = config.WriteTo.Console(outputTemplate: outputTemplate);
        }

        if (options.EnableFile)
        {
            Directory.CreateDirectory(options.LogsDirectory);
            var logFile = Path.Combine(options.LogsDirectory, $"{options.ApplicationName.ToLowerInvariant()}-.log");
            config = config.WriteTo.File(
                logFile,
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14,
                fileSizeLimitBytes: 10 * 1024 * 1024,
                outputTemplate: outputTemplate);
        }

        if (memorySink is not null)
        {
            config = config.WriteTo.Sink(memorySink);
        }

        return config.CreateLogger();
    }

    public static ILoggerFactory CreateLoggerFactory(Serilog.ILogger logger)
        => new SerilogLoggerFactory(logger, dispose: false);
}

