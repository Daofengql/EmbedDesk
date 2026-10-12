using Microsoft.Extensions.Logging;

namespace EmbedDesk.Infrastructure.Remote;

// A factory per connection keeps concurrent session logs separate.
internal sealed class SshConnectionLogger(Action<string> write) : ILoggerFactory
{
    public bool Handshaking { get; set; } = true;
    public ILogger CreateLogger(string categoryName) => new ConnectionLogger(this, categoryName, write);
    public void AddProvider(ILoggerProvider provider) { }
    public void Dispose() { }

    private sealed class ConnectionLogger(SshConnectionLogger owner, string category, Action<string> write) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None && (owner.Handshaking || logLevel >= LogLevel.Warning);
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel)) write($"{category.Split('.').Last()}: {formatter(state, exception)}");
        }
    }
}
