using Microsoft.Extensions.DependencyInjection.Extensions;
using AgentCore.Application.Execution;
using System.Net.Http.Json;
using AgentCore.Api;
using AgentCore.Api.Realtime;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Contracts.Http;
using AgentCore.Contracts.Realtime;
using AgentCore.Domain.Conversation;
using AgentCore.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AgentCore.Api.Tests;

internal sealed class DiagnosticLineLoggerProvider(LogLevel minimum = LogLevel.Error) : ILoggerProvider
{
    public List<string> Lines { get; } = [];
    public List<Exception> Errors { get; } = [];

    public ILogger CreateLogger(string categoryName) => new DiagnosticLineLogger(Lines, Errors, minimum);

    public void Dispose()
    {
    }

    private sealed class DiagnosticLineLogger(List<string> lines, List<Exception> errors, LogLevel minimum) : ILogger
    {
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel < minimum)
            {
                return;
            }

            lock (lines)
            {
                lines.Add(formatter(state, exception) + " " + exception?.GetType().Name);
                if (exception is not null) errors.Add(exception);
            }
        }
    }

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();

        public void Dispose()
        {
        }
    }
}
