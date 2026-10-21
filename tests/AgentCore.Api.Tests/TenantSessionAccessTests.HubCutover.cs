using System.Diagnostics;
using System.Reflection;
using AgentCore.Api.Realtime;
using AgentCore.Application.Ports;
using AgentCore.Infrastructure.Providers.Synthetic;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace AgentCore.Api.Tests;

public sealed partial class TenantSessionAccessTests
{
    [Fact]
    public void Tenant_Hub_declares_only_authenticated_canonical_methods_without_owner_host_dispatch()
    {
        Assert.NotNull(typeof(SessionHub).GetCustomAttribute<AuthorizeAttribute>());
        Assert.DoesNotContain(typeof(SessionHub).GetConstructors().SelectMany(c => c.GetParameters()), p => p.ParameterType.FullName == "AgentCore.Api.Realtime.SessionHost");
        var methods = typeof(SessionHub).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => m.Name != nameof(SessionHub.OnDisconnectedAsync)).Select(m => m.Name).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(new[] { "AttachTenant", "CancelResponse", "PlaybackCompleted", "PlaybackProgress", "PlaybackStarted", "PlaybackStopped",
            "ResponseReceived", "SendAudio", "SendText", "SetMode", "SetMuted", "SpeechEnded", "SpeechEvidence", "SpeechStarted" }.Order(StringComparer.Ordinal), methods);
    }

    [Theory(Timeout = 120000)]
    [InlineData(false, "messagepack")] [InlineData(true, "messagepack")]
    [InlineData(false, "json")] [InlineData(true, "json")]
    public async Task Tenant_Hub_real_JavaScript_rejects_old_tokens_and_unattached_controls_then_executes_only_canonical_work(bool sqlite, string protocol)
    {
        var root = Path.Combine(Path.GetTempPath(), "tenant-hub-cutover-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            await using WebApplicationFactory<Program> factory = sqlite
                ? new NativeTenantSqliteFactory(root, new ScriptedLanguageModel()) : new NativeTenantMemoryFactory(root, new ScriptedLanguageModel());
            factory.UseKestrel(0); using var ready = factory.CreateClient();
            var repo = new DirectoryInfo(AppContext.BaseDirectory);
            while (repo is not null && !File.Exists(Path.Combine(repo.FullName, "AgentCore.sln"))) repo = repo.Parent;
            Assert.NotNull(repo);
            var start = new ProcessStartInfo("node", "tenant-hub-cutover.mjs") { WorkingDirectory = Path.Combine(repo.FullName, "tests", "realtime-js"),
                UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            start.Environment["BASE_URL"] = ready.BaseAddress!.ToString().TrimEnd('/'); start.Environment["HUB_PROTOCOL"] = protocol;
            start.Environment["OLD_OWNER_TOKEN"] = RetiredOwnerHeader.Token;
            using var process = Process.Start(start)!; var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            try { await process.WaitForExitAsync(deadline.Token); }
            finally { if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); } }
            Assert.True(process.ExitCode == 0, await stdout + await stderr); Assert.Contains("Tenant Hub cutover passed", await stdout);
            Assert.Equal(0, factory.Services.GetRequiredService<AgentCore.Api.Http.OutboundHttpProbe>().Attempts);
        }
        finally { SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
