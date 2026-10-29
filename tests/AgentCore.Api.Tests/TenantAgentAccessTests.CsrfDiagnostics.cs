using System.Collections.Concurrent;
using AgentCore.Application.Observability;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace AgentCore.Api.Tests;

public sealed partial class TenantAgentAccessTests
{
    private sealed class HttpSetupFailureLogs : ILoggerProvider
    {
        private readonly ConcurrentQueue<string> _events = new();
        public ILogger CreateLogger(string categoryName) => new Capture(this);
        public void Dispose() { }
        public string Describe() => string.Join(" | ", _events);

        private void Record(Exception error)
        {
            // Startup can fail before Core's HTTP handler runs. Capture framework
            // errors through the same content-free snapshot, never their raw text.
            var safe = error.GetType().Assembly == typeof(DiagnosticLog).Assembly
                && error.GetType().DeclaringType?.FullName == "AgentCore.Application.Observability.DiagnosticExceptionSnapshot"
                && error.GetType().Name is "SafeException" or "SafeAggregateException";
            var text = (safe ? error : DiagnosticExceptionSnapshot.Capture(error)).ToString();
            lock (_events)
                if (_events.Count < 8) _events.Enqueue(text[..Math.Min(text.Length, 4096)]);
        }
        private sealed class Capture(HttpSetupFailureLogs owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel level) => level >= LogLevel.Error;
            public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (level >= LogLevel.Error && exception is not null) owner.Record(exception);
            }
        }
    }

    [Fact]
    public async Task Automation_setup_keeps_CSRF_failure_and_only_content_free_HTTP_exception_evidence()
    {
        const string privateValue = "https://private.test/secret?token=PRIVATE_CSRF_COOKIE body=PRIVATE_SOURCE";
        await using var factory = new RuntimeStateFactory(false, manualDispatch: true, configure: services =>
            services.Replace(ServiceDescriptor.Singleton<IAntiforgery>(new UnavailableAntiforgery(privateValue))));
        using var owner = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost") });
        var failure = await Assert.ThrowsAnyAsync<Xunit.Sdk.XunitException>(() => MutationAgent(factory, owner));
        Assert.Contains("CSRF request status=500; phase=before-login", failure.Message);
        Assert.Contains("HTTP diagnostics=", failure.Message);
        Assert.Contains("System.InvalidOperationException HResult=", failure.Message);
        Assert.DoesNotContain("PRIVATE", failure.Message); Assert.DoesNotContain("private.test", failure.Message);
        Assert.DoesNotContain("token=", failure.Message); Assert.DoesNotContain("body=", failure.Message);
    }

    private sealed class UnavailableAntiforgery(string privateValue) : IAntiforgery
    {
        public AntiforgeryTokenSet GetAndStoreTokens(HttpContext context) => throw new InvalidOperationException(privateValue);
        public AntiforgeryTokenSet GetTokens(HttpContext context) => throw new InvalidOperationException(privateValue);
        public Task<bool> IsRequestValidAsync(HttpContext context) => throw new InvalidOperationException(privateValue);
        public void SetCookieTokenAndHeader(HttpContext context) => throw new InvalidOperationException(privateValue);
        public Task ValidateRequestAsync(HttpContext context) => throw new InvalidOperationException(privateValue);
    }
}
