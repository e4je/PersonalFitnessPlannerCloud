using Microsoft.Extensions.Logging;

namespace PersonalFitnessPlanner.Infrastructure.Logging;

public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly AppPaths _paths;
    private readonly LogLevel _minimumLevel;
    private readonly object _writeLock = new();
    private bool _disposed;

    public FileLoggerProvider(AppPaths paths, LogLevel minimumLevel = LogLevel.Information)
    {
        _paths = paths;
        _minimumLevel = minimumLevel;
        _paths.EnsureCreated();
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
            File.AppendAllText(path, line + Environment.NewLine);
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
