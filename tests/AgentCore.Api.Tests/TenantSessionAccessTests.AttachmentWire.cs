using System.Diagnostics;
using AgentCore.Api.Http;
using AgentCore.Infrastructure.Providers.Synthetic;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace AgentCore.Api.Tests;

public sealed partial class TenantSessionAccessTests
{
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_attachment_MessagePack_admission_checks_sources_dedupe_reconnect_and_current_sender_read(bool sqlite)
    {
        var root = Path.Combine(Path.GetTempPath(), "tenant-attachment-wire-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            await using WebApplicationFactory<Program> factory = sqlite ? new NativeTenantSqliteFactory(root, new ScriptedLanguageModel()) : new NativeTenantMemoryFactory(root, new ScriptedLanguageModel());
            factory.UseKestrel(0); using var client = factory.CreateClient();
            var repo = new DirectoryInfo(AppContext.BaseDirectory); while (repo is not null && !File.Exists(Path.Combine(repo.FullName, "AgentCore.sln"))) repo = repo.Parent; Assert.NotNull(repo);
            var start = new ProcessStartInfo("node", "tenant-attachment-messages.mjs") { WorkingDirectory = Path.Combine(repo.FullName, "tests", "realtime-js"), UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            start.Environment["BASE_URL"] = client.BaseAddress!.ToString().TrimEnd('/'); using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync(); using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(120));
            try { await process.WaitForExitAsync(deadline.Token); }
            finally { if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); } }
            Assert.True(process.ExitCode == 0, await output + await error); Assert.Contains("Tenant Attachment MessagePack passed", await output);
            Assert.Equal(0, factory.Services.GetRequiredService<OutboundHttpProbe>().Attempts);
        }
        finally { SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
