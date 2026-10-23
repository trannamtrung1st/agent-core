using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using AgentCore.Api.Http;
using AgentCore.Application.Ports;
using AgentCore.Infrastructure.Providers.Synthetic;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace AgentCore.Api.Tests;

public sealed partial class TenantSessionAccessTests
{
    [Theory]
    [InlineData(false, false)] [InlineData(true, false)]
    [InlineData(false, true)] [InlineData(true, true)]
    public async Task Unexpected_tenant_HTTP_failure_keeps_original_authority_and_correlation_without_private_exception_content_in_any_log(bool sqlite, bool hostile)
    {
        var root = Path.Combine(Path.GetTempPath(), "tenant-diagnostic-isolation-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using var capture = new DiagnosticSink();
        try
        {
            void Configure(IServiceCollection services)
            {
                services.Replace(ServiceDescriptor.Singleton<IAuthorizationAuditQueryStore>(new FailingDiagnosticAudit(hostile)));
                services.AddLogging(builder => builder.AddProvider(capture));
            }
            await using WebApplicationFactory<Program> factory = sqlite
                ? new NativeTenantSqliteFactory(root, new ScriptedLanguageModel(), Configure)
                : new NativeTenantMemoryFactory(root, new ScriptedLanguageModel(), Configure);
            using var human = factory.CreateClient(new WebApplicationFactoryClientOptions
                { BaseAddress = new("https://localhost"), AllowAutoRedirect = false });
            await Login(human, "owner@example.test", "explicit-synthetic-test-password");
            var tenant = (await Json(await human.GetAsync("/api/v2/account/tenants")))
                .GetProperty("items")[0].GetProperty("tenantId").GetGuid();
            var access = await Json(await human.GetAsync($"/api/v2/tenants/{tenant}/access/me"));
            var actor = access.GetProperty("principalId").GetGuid();
            var security = factory.Services.GetRequiredService<IIamStore>();
            var before = (await security.ReadTenantAsync(tenant))!;
            using var response = await human.GetAsync($"/api/v2/tenants/{tenant}/audit");
            Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
            var body = await response.Content.ReadAsStringAsync();
            using var problem = JsonDocument.Parse(body);
            Assert.Equal("InternalError", problem.RootElement.GetProperty("code").GetString());
            var id = problem.RootElement.GetProperty("diagnosticId").GetString();
            Assert.True(Guid.TryParse(id, out var diagnosticId));
            var failure = Assert.Single(capture.Entries, e => e.Category == typeof(DiagnosticExceptionHandler).FullName);
            Assert.Equal(diagnosticId, failure.Fields["DiagnosticId"]);
            Assert.Equal(tenant, failure.Fields["TenantId"]);
            Assert.Equal(actor, failure.Fields["ActorPrincipalId"]);
            Assert.NotNull(failure.Exception);
            Assert.Contains(nameof(FailingDiagnosticAudit), failure.Exception.ToString());
            Assert.Contains(hostile ? nameof(HostileDiagnosticException) : nameof(HttpRequestException), failure.Exception.ToString());
            Assert.Equal(before.Tenant.Revision, (await security.ReadTenantAsync(tenant))!.Tenant.Revision);
            foreach (var sentinel in new[] { "private-diagnostic-sentinel", "provider-secret-sentinel", "body-secret-sentinel" })
            {
                Assert.DoesNotContain(sentinel, body);
                foreach (var entry in capture.Entries)
                {
                    Assert.DoesNotContain(sentinel, entry.Message);
                    Assert.DoesNotContain(sentinel, entry.Exception?.ToString() ?? "");
                    Assert.DoesNotContain(sentinel, JsonSerializer.Serialize(entry.Fields));
                }
            }
            Assert.Equal(0, factory.Services.GetRequiredService<OutboundHttpProbe>().Attempts);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    private sealed class FailingDiagnosticAudit(bool hostile) : IAuthorizationAuditQueryStore
    {
        public ValueTask<AuthorizationAuditPage> QueryAuditAsync(Guid? tenantId, AuthorizationAuditQuery query, CancellationToken ct = default)
        {
            Exception error = hostile ? new AggregateException("private-diagnostic-sentinel", new HostileDiagnosticException())
                : new HttpRequestException("https://user:provider-secret-sentinel@example.test/private-diagnostic-sentinel?token=body-secret-sentinel",
                    new IOException("private-diagnostic-sentinel"), HttpStatusCode.ServiceUnavailable);
            error.Data["Authorization"] = "provider-secret-sentinel";
            throw error;
        }
    }

    private sealed class HostileDiagnosticException : Exception
    {
        public override string Message => "private-diagnostic-sentinel";
        public override string? StackTrace => "body-secret-sentinel";
        public override string ToString() => "provider-secret-sentinel";
    }

    private sealed record CapturedDiagnostic(string Category, string Message, Exception? Exception, IReadOnlyDictionary<string, object?> Fields);

    private sealed class DiagnosticSink : ILoggerProvider
    {
        public ConcurrentQueue<CapturedDiagnostic> Entries { get; } = new();
        public ILogger CreateLogger(string categoryName) => new SinkLogger(categoryName, Entries);
        public void Dispose() { }
        private sealed class SinkLogger(string category, ConcurrentQueue<CapturedDiagnostic> entries) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel level) => level >= LogLevel.Warning;
            public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (!IsEnabled(level)) return;
                var fields = state is IEnumerable<KeyValuePair<string, object?>> pairs
                    ? pairs.GroupBy(p => p.Key).ToDictionary(g => g.Key, g => g.Last().Value) : new Dictionary<string, object?>();
                entries.Enqueue(new(category, formatter(state, exception), exception, fields));
            }
        }
    }
}
