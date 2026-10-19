using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace FinancialTransactions.IntegrationTests;

/// <summary>Guarda as mensagens já formatadas (e a exceção, quando houver) de todos os logs do host de teste.</summary>
public sealed class CapturingLoggerProvider : ILoggerProvider
{
    private readonly ConcurrentQueue<string> _messages = new();

    public IReadOnlyList<string> Messages => _messages.ToArray();

    public ILogger CreateLogger(string categoryName) => new CapturingLogger(_messages, categoryName);

    public void Dispose()
    {
        // Nada a liberar: só memória.
    }

    private sealed class CapturingLogger(ConcurrentQueue<string> messages, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);
            var text = $"{category} [{logLevel}] {formatter(state, exception)}";
            messages.Enqueue(exception is null ? text : $"{text} {exception}");
        }
    }
}
