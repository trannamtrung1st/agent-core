using System.Diagnostics;
using AgentCore.Api.Http;
using AgentCore.Api.Security;
using AgentCore.Application.Ports;
using AgentCore.Infrastructure.Persistence;
using AgentCore.Infrastructure.Providers.Synthetic;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AgentCore.Api.Tests;

public sealed partial class TenantSessionAccessTests
{
    private static void NativeTenantIdentity(IServiceCollection services, string root)
    {
        ((IdentityHttpOptions)services.Single(d => d.ServiceType == typeof(IdentityHttpOptions)).ImplementationInstance!).AllowInsecureLocalHttp = true;
        services.Configure<CookieAuthenticationOptions>(IdentityHttp.Scheme, o => o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest);
        services.Configure<AntiforgeryOptions>(o => o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest);
        var persistence = (PersistenceOptions)services.Last(d => d.ServiceType == typeof(PersistenceOptions)).ImplementationInstance!;
        persistence.WorkspaceRoot = Path.Combine(root, "workspaces"); persistence.ArtifactRoot = Path.Combine(root, "artifacts");
        persistence.AttachmentRoot = Path.Combine(root, "attachments"); persistence.DefinitionResourceRoot = Path.Combine(root, "resources");
        persistence.CredentialProtectionKeyRoot = Path.Combine(root, "keys");
    }
    private sealed class NativeTenantMemoryFactory(string root, ILanguageModel model, Action<IServiceCollection>? configure = null) : AgentCoreApiFactory
    {
        protected override bool EnableTenantRunExecution => true;
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(services => { NativeTenantIdentity(services, root); services.Replace(ServiceDescriptor.Singleton(model)); configure?.Invoke(services); });
        }
    }
    private sealed class NativeTenantSqliteFactory(string root, ILanguageModel model, Action<IServiceCollection>? configure = null) : DurableSqliteHostFactory(Path.Combine(root, "store.db"), languageModel: model)
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder); builder.ConfigureTestServices(services => { NativeTenantIdentity(services, root); configure?.Invoke(services); });
        }
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Tenant_live_MessagePack_JavaScript_receives_actual_worker_text_with_current_auth_reconnect_switch_and_logout(bool sqlite)
    {
        var root = Path.Combine(Path.GetTempPath(), "tenant-live-wire-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            await using WebApplicationFactory<Program> factory = sqlite ? new NativeTenantSqliteFactory(root, new ScriptedLanguageModel()) : new NativeTenantMemoryFactory(root, new ScriptedLanguageModel());
            factory.UseKestrel(0); using var ready = factory.CreateClient();
            var repo = new DirectoryInfo(AppContext.BaseDirectory);
            while (repo is not null && !File.Exists(Path.Combine(repo.FullName, "AgentCore.sln"))) repo = repo.Parent;
            Assert.NotNull(repo);
            var start = new ProcessStartInfo("node", "tenant-live.mjs") { WorkingDirectory = Path.Combine(repo.FullName, "tests", "realtime-js"),
                UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            start.Environment["BASE_URL"] = ready.BaseAddress!.ToString().TrimEnd('/');
            using var process = Process.Start(start)!;
            var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            try { await process.WaitForExitAsync(deadline.Token); }
            finally { if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); } }
            Assert.True(process.ExitCode == 0, await stdout + await stderr);
            Assert.Contains("Tenant live wire passed", await stdout);
            Assert.Equal(0, factory.Services.GetRequiredService<OutboundHttpProbe>().Attempts);
        }
        finally { SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
