using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;

namespace KVMate.Core.Logging;

/// <summary>
/// Writes log lines to <c>KVMate.log</c> in one folder, rolled by size with a hard cap.
/// </summary>
/// <remarks>
/// <para>
/// When the file passes <see cref="MaxFileBytes"/> it becomes <c>KVMate.1.log</c>, replacing the
/// previous one, and a fresh file starts. Two files at most, so the logs can never take more than
/// twice the limit however long the app runs.
/// </para>
/// <para>
/// Logging must never be the reason the app fails. A write that throws switches file logging off
/// for the session rather than retrying on every line.
/// </para>
/// </remarks>
public sealed class FileLoggerProvider : ILoggerProvider
{
    /// <summary>The size at which the current file is rolled.</summary>
    public const long MaxFileBytes = 1024 * 1024;

    private const string CurrentName = "KVMate.log";
    private const string PreviousName = "KVMate.1.log";

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly Lock _gate = new();
    private readonly string _directory;
    private readonly TimeProvider _time;

    private StreamWriter? _writer;
    private bool _disabled;
    private bool _disposed;

    /// <param name="directory">The log folder, normally <see cref="StoragePaths.LogDirectory"/>.</param>
    /// <param name="time">Clock for timestamps.</param>
    public FileLoggerProvider(string directory, TimeProvider time)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentNullException.ThrowIfNull(time);

        _directory = directory;
        _time = time;
    }

    /// <inheritdoc />
    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName ?? string.Empty);

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _writer?.Dispose();
            _writer = null;
        }
    }

    private void Write(LogLevel level, string category, string message, Exception? exception)
    {
        var timestamp = _time.GetLocalNow();

        lock (_gate)
        {
            if (_disposed || _disabled)
            {
                return;
            }

            try
            {
                var writer = Writer();

                writer.Write(timestamp.ToString("yyyy-MM-dd HH:mm:ss.fff K", CultureInfo.InvariantCulture));
                writer.Write("  ");
                writer.Write(Label(level));
                writer.Write("  ");
                writer.Write(category[(category.LastIndexOf('.') + 1)..]);
                writer.Write("  ");
                writer.WriteLine(message);

                if (exception is not null)
                {
                    writer.WriteLine(exception.ToString());
                }

                writer.Flush();
            }
            catch (Exception)
            {
                // Whatever stopped this write (a full disk, a locked file) will stop the next one too.
                _disabled = true;
                _writer?.Dispose();
                _writer = null;
            }
        }
    }

    private static string Label(LogLevel level) => level switch
    {
        LogLevel.Trace => "TRACE",
        LogLevel.Debug => "DEBUG",
        LogLevel.Information => "INFO ",
        LogLevel.Warning => "WARN ",
        LogLevel.Error => "ERROR",
        LogLevel.Critical => "CRIT ",
        _ => "NONE ",
    };

    private StreamWriter Writer()
    {
        if (_writer is not null && _writer.BaseStream.Length < MaxFileBytes)
        {
            return _writer;
        }

        _writer?.Dispose();
        _writer = null;

        Directory.CreateDirectory(_directory);

        var current = Path.Combine(_directory, CurrentName);
        if (File.Exists(current) && new FileInfo(current).Length >= MaxFileBytes)
        {
            File.Move(current, Path.Combine(_directory, PreviousName), overwrite: true);
        }

        // Shared for reading, so the file can be opened in an editor while the app runs.
        var stream = new FileStream(current, FileMode.Append, FileAccess.Write, FileShare.Read);
        _writer = new StreamWriter(stream, Utf8NoBom);
        return _writer;
    }

    private sealed class FileLogger(FileLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);

            if (IsEnabled(logLevel))
            {
                provider.Write(logLevel, category, formatter(state, exception), exception);
            }
        }
    }
}
