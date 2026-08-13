using System.Globalization;
using Microsoft.Extensions.Logging;

namespace PersonalFitnessPlanner.Infrastructure.Logging;

public sealed class FileLoggerProvider : ILoggerProvider
{
    public const int RetentionDays = 14;

    private readonly AppPaths _paths;
    private readonly LogLevel _minimumLevel;
    private readonly object _writeLock = new();
    private bool _disposed;

    public FileLoggerProvider(AppPaths paths, LogLevel minimumLevel = LogLevel.Information)
    {
        _paths = paths;
        _minimumLevel = minimumLevel;
        _paths.EnsureCreated();
        lock (_writeLock)
        {
            CleanupExpiredLogs(DateOnly.FromDateTime(DateTime.Today));
        }
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    public void Dispose() => _disposed = true;

    internal bool IsEnabled(LogLevel level) => !_disposed && level >= _minimumLevel && level != LogLevel.None;

    internal void Write(string category, LogLevel level, EventId eventId, string message, Exception? exception)
    {
        if (!IsEnabled(level)) return;
        var timestamp = DateTimeOffset.Now;
        var path = Path.Combine(_paths.LogsDirectory, $"app-{timestamp:yyyyMMdd}.log");
        var line = $"{timestamp:O} [{level}] {category} ({eventId.Id}) {message}";
        if (exception is not null) line += Environment.NewLine + exception;
        lock (_writeLock)
        {
            if (_disposed) return;
            Directory.CreateDirectory(_paths.LogsDirectory);
            var today = DateOnly.FromDateTime(timestamp.Date);
            CleanupExpiredLogs(today);
            File.AppendAllText(path, line + Environment.NewLine);
        }
    }

    private void CleanupExpiredLogs(DateOnly today)
    {
        var oldestRetainedDate = today.AddDays(-(RetentionDays - 1));
        try
        {
            foreach (var path in Directory.EnumerateFiles(_paths.LogsDirectory, "app-*.log", SearchOption.TopDirectoryOnly))
            {
                var name = Path.GetFileName(path);
                if (name.Length != "app-yyyyMMdd.log".Length ||
                    !name.StartsWith("app-", StringComparison.Ordinal) ||
                    !name.EndsWith(".log", StringComparison.Ordinal) ||
                    !DateOnly.TryParseExact(
                        name.AsSpan(4, 8),
                        "yyyyMMdd",
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.None,
                        out var logDate) ||
                    logDate >= oldestRetainedDate)
                {
                    continue;
                }

                try
                {
                    File.Delete(path);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    // Logging must remain available even when an old file is locked.
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            // Retention is best effort and must never break application startup.
        }
    }

    private sealed class FileLogger : ILogger
    {
        private readonly FileLoggerProvider _provider;
        private readonly string _category;

        public FileLogger(FileLoggerProvider provider, string category)
        {
            _provider = provider;
            _category = category;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => _provider.IsEnabled(logLevel);

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            ArgumentNullException.ThrowIfNull(formatter);
            _provider.Write(_category, logLevel, eventId, formatter(state, exception), exception);
        }
    }

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();
        public void Dispose() { }
    }
}
