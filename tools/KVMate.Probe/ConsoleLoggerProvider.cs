using Microsoft.Extensions.Logging;

namespace KVMate.Probe;

/// <summary>Writes log lines to standard error, so they stay apart from the command's own output.</summary>
/// <remarks>
/// Hand-rolled rather than Microsoft.Extensions.Logging.Console, which would be a package for one
/// diagnostic tool to print a line.
/// </remarks>
internal sealed class ConsoleLoggerProvider : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new ConsoleLogger(categoryName);

    public void Dispose()
    {
        // Nothing buffered.
    }

    private sealed class ConsoleLogger(string category) : ILogger
    {
        private readonly string _shortCategory = category[(category.LastIndexOf('.') + 1)..];

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

            Console.Error.WriteLine($"  [{logLevel}] {_shortCategory}: {formatter(state, exception)}");
            if (exception is not null)
            {
                Console.Error.WriteLine($"    {exception.GetType().Name}: {exception.Message}");
            }
        }
    }
}
