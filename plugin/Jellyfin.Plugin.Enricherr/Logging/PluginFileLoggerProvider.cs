using System;
using System.IO;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Plugin.Enricherr.Logging;

/// <summary>
/// Mirrors this plugin's own log entries into a dedicated file, separate from
/// Jellyfin's main server log, so a run can be inspected without wading through
/// unrelated server noise. Every <see cref="ILogger{TCategoryName}"/> call the plugin
/// already makes is captured automatically - this filters by category name rather
/// than requiring a second, duplicate logging call at every call site.
/// </summary>
public sealed class PluginFileLoggerProvider : ILoggerProvider
{
    private const string CategoryPrefix = "Jellyfin.Plugin.Enricherr";
    private const string LogFileName = "enricherr.log";

    private readonly object _writeLock = new();

    /// <inheritdoc />
    public ILogger CreateLogger(string categoryName)
    {
        if (!categoryName.StartsWith(CategoryPrefix, StringComparison.Ordinal))
        {
            return NullLogger.Instance;
        }

        return new PluginFileLogger(categoryName, GetLogFilePath, _writeLock);
    }

    /// <inheritdoc />
    public void Dispose()
    {
    }

    private static string? GetLogFilePath()
    {
        var dataFolderPath = Plugin.Instance?.DataFolderPath;
        if (string.IsNullOrEmpty(dataFolderPath))
        {
            return null;
        }

        return Path.Combine(dataFolderPath, LogFileName);
    }

    private sealed class PluginFileLogger : ILogger
    {
        private readonly string _category;
        private readonly Func<string?> _getLogFilePath;
        private readonly object _writeLock;

        public PluginFileLogger(string category, Func<string?> getLogFilePath, object writeLock)
        {
            _category = category;
            _getLogFilePath = getLogFilePath;
            _writeLock = writeLock;
        }

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            // Resolved lazily (not at provider-construction time): the logging
            // pipeline is wired up before Plugin.Instance exists, but by the time
            // anything in this plugin actually logs something, it always does.
            var path = _getLogFilePath();
            if (path is null)
            {
                return;
            }

            var line = $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz}] [{logLevel}] {_category}: {formatter(state, exception)}";
            if (exception is not null)
            {
                line += Environment.NewLine + exception;
            }

            try
            {
                lock (_writeLock)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    File.AppendAllText(path, line + Environment.NewLine);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Best-effort - a logging failure must never break the actual task.
            }
        }
    }
}
