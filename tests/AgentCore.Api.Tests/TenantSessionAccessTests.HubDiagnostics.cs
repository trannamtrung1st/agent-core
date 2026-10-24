using System.Diagnostics;
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
    [Theory(Timeout = 120000)]
    [InlineData(false, "json")]
    [InlineData(true, "json")]
    [InlineData(false, "messagepack")]
    [InlineData(true, "messagepack")]
    public async Task Unexpected_tenant_Hub_fault_keeps_private_content_out_of_all_sinks_and_recovers_without_duplicate_work(bool sqlite, string protocol)
    {
        var root = Path.Combine(Path.GetTempPath(), "tenant-hub-diagnostic-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root); using var capture = new DiagnosticSink();
        try
        {
            void Configure(IServiceCollection services)
            {
                services.Replace(ServiceDescriptor.Singleton<ITenantConversationStore>(p => new FirstFailingHubContent((ITenantConversationStore)p.GetRequiredService<IIamStore>())));
                services.AddLogging(builder => builder.AddProvider(capture));
            }
            await using WebApplicationFactory<Program> factory = sqlite
                ? new NativeTenantSqliteFactory(root, new ScriptedLanguageModel(), Configure)
                : new NativeTenantMemoryFactory(root, new ScriptedLanguageModel(), Configure);
            factory.UseKestrel(0); using var ready = factory.CreateClient();
            var repo = new DirectoryInfo(AppContext.BaseDirectory);
            while (repo is not null && !File.Exists(Path.Combine(repo.FullName, "AgentCore.sln"))) repo = repo.Parent;
            var start = new ProcessStartInfo("node", "tenant-hub-diagnostics.mjs")
            { WorkingDirectory = Path.Combine(repo!.FullName, "tests", "realtime-js"), UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            start.Environment["BASE_URL"] = ready.BaseAddress!.ToString().TrimEnd('/'); start.Environment["HUB_PROTOCOL"] = protocol;
            using var process = Process.Start(start)!; var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            try { await process.WaitForExitAsync(deadline.Token); }
            finally { if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); } }
            Assert.True(process.ExitCode == 0, await stdout + await stderr);
            using var outcome = JsonDocument.Parse((await stdout).Trim());
            var clientFailure = outcome.RootElement.GetProperty("failure").GetString()!;
            foreach (var sentinel in new[] { "private-hub-sentinel", "hub-provider-secret", "hub-body-secret" })
            {
                Assert.DoesNotContain(sentinel, clientFailure);
                foreach (var entry in capture.Entries)
                {
                    Assert.DoesNotContain(sentinel, entry.Message);
                    Assert.DoesNotContain(sentinel, entry.Exception?.ToString() ?? "");
                    Assert.DoesNotContain(sentinel, JsonSerializer.Serialize(entry.Fields));
                }
            }
            var failure = Assert.Single(capture.Entries, e => e.Fields.GetValueOrDefault("Operation") as string == "hub.command");
            Assert.Contains(((Guid)failure.Fields["DiagnosticId"]!).ToString("D"), clientFailure);
            Assert.Equal(outcome.RootElement.GetProperty("tenant").GetGuid(), failure.Fields["TenantId"]);
            Assert.Equal(outcome.RootElement.GetProperty("actor").GetGuid(), failure.Fields["ActorPrincipalId"]);
            Assert.Equal(outcome.RootElement.GetProperty("session").GetGuid(), failure.Fields["SessionId"]);
            Assert.Contains(nameof(FirstFailingHubContent), failure.Exception!.ToString());
            Assert.Equal(0, factory.Services.GetRequiredService<OutboundHttpProbe>().Attempts);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    private sealed class FirstFailingHubContent(ITenantConversationStore inner) : ITenantConversationStore
    {
        private int reads;
        public ValueTask<TenantConversationPage?> ReadTenantConversationAsync(Guid tenant, Guid session, long? after, long? before, int limit, CancellationToken ct = default)
        {
            if (Interlocked.Increment(ref reads) == 1)
                throw new HttpRequestException("https://user:hub-provider-secret@example.test/private-hub-sentinel?token=hub-body-secret",
                    new IOException("private-hub-sentinel"), HttpStatusCode.ServiceUnavailable);
            return inner.ReadTenantConversationAsync(tenant, session, after, before, limit, ct);
        }
    }
}
