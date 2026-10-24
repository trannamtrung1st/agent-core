using AgentCore.Infrastructure.Sandbox;
using Microsoft.Extensions.Logging;

namespace AgentCore.Infrastructure.Tests;

internal sealed class OpenSandboxVerificationLogs : ILogger<OpenSandboxService>
{
    private readonly List<(string Message, bool HasException)> events = [];
    public IReadOnlyList<(string Message, bool HasException)> Events { get { lock (events) return events.ToArray(); } }
    public string Diagnostic => string.Join(Environment.NewLine, Events.Select(e => e.Message));
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;
    public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (level != LogLevel.Warning || id.Name != "SandboxVerificationFailure") return;
        var message = formatter(state, exception);
        lock (events)
        {
            if (events.Count == 16) events.RemoveAt(0);
            events.Add((message[..Math.Min(message.Length, 1024)], exception is not null));
        }
    }
}
