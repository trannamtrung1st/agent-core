using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;
using AgentCore.Infrastructure.Persistence;
using AgentCore.Infrastructure.Sandbox;
using AgentCore.Infrastructure.Identity;
using AgentCore.Infrastructure.Workspaces;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantCredentialIntegrationTests
{
    [Theory] [InlineData(false, "success")] [InlineData(true, "success")] [InlineData(false, "pending")] [InlineData(true, "pending")]
    [InlineData(false, "foreignHandle")] [InlineData(true, "foreignHandle")]
    public async Task Tenant_sandbox_verification_sequential_SDK_probes_require_final_attempt_fence_before_promotion(bool sqlite, string mode)
    {
        var pending = mode == "pending"; var foreign = mode == "foreignHandle";
        await using var f = await Fixture.Create(sqlite);
        var owner = await f.Identity.BootstrapAsync("owner@example.test", "Owner", "strong-owner-password", "Alpha", "alpha");
        var human = await SandboxHuman(f, owner.OwnerPrincipalId);
        var builder = WebApplication.CreateSlimBuilder(); builder.Logging.ClearProviders(); await using var server = builder.Build(); server.Urls.Add("http://127.0.0.1:0");
        var creates = 0; var kills = 0; var fences = 0; var probes = 0; var scripts = 0; var owners = new HashSet<string>(); Dictionary<string,string> metadata = [];
        var root = Path.Combine(f.Root, "working");
        server.Run(async http =>
        {
            Assert.Equal("synthetic-only-key", http.Request.Headers["OPEN-SANDBOX-API-KEY"]);
            var path = http.Request.Path.Value!;
            if (path == "/agent-core/generations/capabilities")
                await http.Response.WriteAsJsonAsync(new { contractVersion = 1, verificationFencing = true });
            else if (path == "/agent-core/verifications/retire")
            {
                fences++; Assert.Equal(foreign ? 1 : 3, creates); Assert.Equal(foreign ? 0 : 3, kills);
                using var body = await JsonDocument.ParseAsync(http.Request.Body); var value = body.RootElement;
                Assert.Equal(Assert.Single(owners), value.GetProperty("tenant").GetString() + "." + value.GetProperty("attempt").GetString());
                await http.Response.WriteAsJsonAsync(new { contractVersion = 1, tenant = value.GetProperty("tenant").GetString(), attempt = value.GetProperty("attempt").GetString(), retired = !pending });
            }
            else if (http.Request.Method == "POST" && path == "/v1/sandboxes")
            {
                Assert.Equal(0, fences); creates++;
                using var body = await JsonDocument.ParseAsync(http.Request.Body); metadata = body.RootElement.GetProperty("metadata").Deserialize<Dictionary<string,string>>()!;
                owners.Add(metadata["agent-core-image-verification"]);
                await http.Response.WriteAsJsonAsync(new { id = "verify-environment", entrypoint = new[] { "/bin/sh" }, status = new { state = "Running" }, createdAt = DateTimeOffset.UtcNow, metadata });
            }
            else if (path.Contains("/endpoints/")) await http.Response.WriteAsJsonAsync(new { endpoint = new Uri(server.Urls.Single()).Authority });
            else if (path == "/ping") await http.Response.WriteAsJsonAsync(new { status = "ok" });
            else if (http.Request.Method == "GET" && path == "/v1/sandboxes/verify-environment")
                await http.Response.WriteAsJsonAsync(new { id = "verify-environment", entrypoint = new[] { "/bin/sh" }, status = new { state = "Running" }, createdAt = DateTimeOffset.UtcNow,
                    metadata = foreign ? new Dictionary<string,string>(metadata) { ["agent-core-image-verification"] = Guid.Empty.ToString("N") + "." + Guid.Empty.ToString("N") } : metadata });
            else if (http.Request.Method == "DELETE") { kills++; http.Response.StatusCode = 204; }
            else if (path.StartsWith("/agent-core/mounts/", StringComparison.Ordinal))
            {
                using var body = await JsonDocument.ParseAsync(http.Request.Body); var value = body.RootElement;
                Assert.Equal(metadata["agent-core-image-verification"], value.GetProperty("ownerValue").GetString());
                await http.Response.WriteAsJsonAsync(new { contractVersion = 4, runtimeUser = value.GetProperty("uid").GetInt32() + ":" + value.GetProperty("gid").GetInt32(), topology = "linux", imageId = "fixture", imageUser = "fixture" });
            }
            else if (path == "/command")
            {
                using var body = await JsonDocument.ParseAsync(http.Request.Body); var script = body.RootElement.GetProperty("argv").EnumerateArray().Last().GetString()!;
                if (script.Contains("stage=process_isolation", StringComparison.Ordinal))
                {
                    probes++;
                    // Controlled filesystem peer; actual shell and Docker remain separate qualification.
                    foreach (var directory in Directory.EnumerateDirectories(root, ".core-probe-*", SearchOption.AllDirectories))
                    {
                        var bytes = await File.ReadAllBytesAsync(Path.Combine(directory, "core"));
                        foreach (var name in new[] { "renamed", "hard", "symbolic" })
                        { var file = Path.Combine(directory, name); await File.WriteAllBytesAsync(file, bytes); if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite); }
                    }
                }
                if (script == "printf verification-init") scripts++;
                http.Response.ContentType = "text/event-stream";
                await http.Response.WriteAsync("data: {\"type\":\"init\",\"text\":\"verification-command\",\"timestamp\":0}\n\ndata: {\"type\":\"execution_complete\",\"execution_time\":5,\"timestamp\":1}\n\n");
            }
            else http.Response.StatusCode = 404;
        });
        await server.StartAsync();
        var options = new OpenSandboxOptions { Enabled = true, AllowLocalImageIds = true, CanonicalWorkspaceRoot = root, WorkspaceDockerHostRoot = root, Endpoint = server.Urls.Single(), ApiKey = "synthetic-only-key" };
        var seed = VerificationServices(f, options: options); var store = (ITenantSandboxVerificationStore)f.Iam;
        var guard = new TenantSandboxImageAuthorization(f.Iam, (ITenantSandboxProfileStore)f.Iam, f.Identity, f.Authorizer);
        var real = new OpenSandboxService(options, f.Sandboxes, null!, f.Memory, new InMemoryAgentRunStore(new InMemoryMemoryStore(), new SystemDiagnosticIdSource()), f.Clock,
            imageAuthority: seed.Images, tenantVerifications: store, verificationAuthority: guard);
        var v = VerificationServices(f, real, options);
        async Task<long> Revision() => (await f.Iam.ReadTenantAsync(owner.TenantId))!.Tenant.Revision;
        var p = (await v.Profiles.SaveAsync(owner.TenantId, null, human, SandboxInput() with { ImageReference = "sha256:" + new string('a', 64), InitializationScript = "printf verification-init" }, await Revision(), 0)).Profile;
        var result = await v.Service.VerifyAsync(owner.TenantId, p.Id, human, await Revision(), 1);
        Assert.Equal(foreign ? (1, 0, 1, 0, 0) : (3, 3, 1, 3, 1), (creates, kills, fences, probes, scripts));
        Assert.Equal(pending ? SandboxVerificationStatus.CleanupPending : foreign ? SandboxVerificationStatus.Failed : SandboxVerificationStatus.Succeeded, result.Attempt!.Status);
        Assert.Equal(!pending && !foreign, (await store.ReadSandboxVerificationAsync(owner.TenantId, p.Id, 1)).Observation?.Passed == true);
        Assert.Equal(pending, Directory.Exists(Path.Combine(root, ".verification", result.Attempt.AttemptId.ToString("N"))));
        await server.StopAsync();
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_sandbox_verification_cleanup_rejects_unproven_or_stale_provider_reply_without_purging(bool sqlite)
    {
        var clock = new FakeTimeProvider(); await using var f = await Fixture.Create(sqlite, clock);
        var owner = await f.Identity.BootstrapAsync("owner@example.test", "Owner", "strong-owner-password", "Alpha", "alpha"); var human = await SandboxHuman(f, owner.OwnerPrincipalId);
        var builder = WebApplication.CreateSlimBuilder(); builder.Logging.ClearProviders(); await using var server = builder.Build(); server.Urls.Add("http://127.0.0.1:0");
        var response = "pending"; Action? duringResponse = null;
        server.Run(async http =>
        {
            Assert.Equal("POST", http.Request.Method); Assert.Equal("/agent-core/verifications/retire", http.Request.Path);
            using var body = await JsonDocument.ParseAsync(http.Request.Body); var value = body.RootElement;
            if (response == "unavailable") { http.Response.StatusCode = 404; return; }
            if (response == "malformed") { await http.Response.WriteAsync("private-invalid-json"); return; }
            if (response == "oversized") { await http.Response.WriteAsync(new string('x', 4097)); return; }
            if (response == "expired") clock.Advance(TimeSpan.FromMinutes(5));
            duringResponse?.Invoke();
            await http.Response.WriteAsJsonAsync(new { contractVersion = response == "version" ? 2 : 1, tenant = response == "foreign" ? Guid.Empty.ToString("N") : value.GetProperty("tenant").GetString(),
                attempt = response == "attempt" ? Guid.Empty.ToString("N") : value.GetProperty("attempt").GetString(), retired = response != "pending" });
        });
        await server.StartAsync();
        var options = new OpenSandboxOptions { CanonicalWorkspaceRoot = Path.Combine(f.Root, "working"), WorkspaceDockerHostRoot = Path.Combine(f.Root, "working"), Endpoint = server.Urls.Single(), ApiKey = "synthetic-only-key" };
        var runner = new ScriptedSandboxVerifier(new SandboxVerificationHost(options).Scope, f.Clock) { Clean = false }; var v = VerificationServices(f, runner, options);
        async Task<long> Revision() => (await f.Iam.ReadTenantAsync(owner.TenantId))!.Tenant.Revision;
        var p = (await v.Profiles.SaveAsync(owner.TenantId, null, human, SandboxInput(), await Revision(), 0)).Profile;
        var attempt = (await v.Service.VerifyAsync(owner.TenantId, p.Id, human, await Revision(), 1)).Attempt!;
        var mounts = new CanonicalSandboxMountResolver(options).Verification(attempt.AttemptId);
        var store = (ITenantSandboxVerificationStore)f.Iam; var claim = Assert.Single(await store.ClaimSandboxVerificationCleanupAsync(clock.GetUtcNow(), f.Ids.NewId()));
        var real = new OpenSandboxService(options, f.Sandboxes, null!, f.Memory, null!, clock, tenantVerifications: store);
        foreach (var mode in new[] { "pending", "unavailable", "malformed", "oversized", "foreign", "attempt", "version", "hostChanged", "expired" })
        {
            var originalRoot = options.WorkspaceDockerHostRoot;
            duringResponse = () => { if (mode == "hostChanged") options.WorkspaceDockerHostRoot += "-changed"; };
            response = mode; Assert.False(await real.CleanupAsync(claim)); Assert.True(Directory.Exists(mounts.VerificationRoot));
            options.WorkspaceDockerHostRoot = originalRoot;
        }
        Assert.Equal(SandboxVerificationStatus.CleanupPending, (await store.FindSandboxVerificationAsync(owner.TenantId, p.Id))!.Status);
        Assert.Equal(1, runner.Calls); await server.StopAsync();
    }

    [Theory] [InlineData(false, "unsupported")] [InlineData(true, "unsupported")]
    [InlineData(false, "revoked")] [InlineData(true, "revoked")]
    [InlineData(false, "canceled")] [InlineData(true, "canceled")]
    public async Task Tenant_sandbox_verification_denies_old_server_and_interrupts_stalled_create_without_replay(bool sqlite, string mode)
    {
        await using var f = await Fixture.Create(sqlite);
        var owner = await f.Identity.BootstrapAsync("owner@example.test", "Owner", "strong-owner-password", "Alpha", "alpha"); var human = await SandboxHuman(f, owner.OwnerPrincipalId);
        var builder = WebApplication.CreateSlimBuilder(); builder.Logging.ClearProviders(); await using var server = builder.Build(); server.Urls.Add("http://127.0.0.1:0");
        var creates = 0; var fences = 0; var aborted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        server.Run(async http =>
        {
            Assert.Equal("synthetic-only-key", http.Request.Headers["OPEN-SANDBOX-API-KEY"]);
            if (http.Request.Path == "/agent-core/generations/capabilities")
                await http.Response.WriteAsJsonAsync(new { contractVersion = 1, retirementFencing = true, verificationFencing = mode != "unsupported" });
            else if (http.Request.Path == "/agent-core/verifications/retire")
            {
                fences++; using var body = await JsonDocument.ParseAsync(http.Request.Body); var value = body.RootElement;
                await http.Response.WriteAsJsonAsync(new { contractVersion = 1, tenant = value.GetProperty("tenant").GetString(), attempt = value.GetProperty("attempt").GetString(), retired = true });
            }
            else
            {
                Assert.Equal("/v1/sandboxes", http.Request.Path); creates++;
                using var body = await JsonDocument.ParseAsync(http.Request.Body);
                if (mode == "revoked") await f.Identity.SignOutAsync(human.AuthenticationSessionId);
                else cancellation.Cancel();
                try { await Task.Delay(Timeout.InfiniteTimeSpan, http.RequestAborted); }
                catch (OperationCanceledException) { aborted.TrySetResult(); }
            }
        });
        await server.StartAsync();
        var root = Path.Combine(f.Root, "working");
        var options = new OpenSandboxOptions { Enabled = true, AllowLocalImageIds = true, CanonicalWorkspaceRoot = root, WorkspaceDockerHostRoot = root, Endpoint = server.Urls.Single(), ApiKey = "synthetic-only-key" };
        var seed = VerificationServices(f, options: options); var store = (ITenantSandboxVerificationStore)f.Iam;
        var guard = new TenantSandboxImageAuthorization(f.Iam, (ITenantSandboxProfileStore)f.Iam, f.Identity, f.Authorizer);
        var real = new OpenSandboxService(options, f.Sandboxes, null!, f.Memory, new InMemoryAgentRunStore(new InMemoryMemoryStore(), new SystemDiagnosticIdSource()), f.Clock,
            imageAuthority: seed.Images, tenantVerifications: store, verificationAuthority: guard);
        var v = VerificationServices(f, real, options);
        async Task<long> Revision() => (await f.Iam.ReadTenantAsync(owner.TenantId))!.Tenant.Revision;
        var p = (await v.Profiles.SaveAsync(owner.TenantId, null, human, SandboxInput() with { ImageReference = "sha256:" + new string('a', 64) }, await Revision(), 0)).Profile;
        var work = v.Service.VerifyAsync(owner.TenantId, p.Id, human, await Revision(), 1, ct: cancellation.Token).AsTask();
        if (mode == "revoked") await Assert.ThrowsAsync<AccessDeniedException>(() => work.WaitAsync(TimeSpan.FromSeconds(10)));
        else if (mode == "canceled") await Assert.ThrowsAnyAsync<OperationCanceledException>(() => work.WaitAsync(TimeSpan.FromSeconds(10)));
        else Assert.Equal("retirementUnavailable", (await work.WaitAsync(TimeSpan.FromSeconds(10))).Attempt!.Result!.FailureCode);
        var receipt = (await store.FindSandboxVerificationAsync(owner.TenantId, p.Id))!;
        Assert.False(receipt.Active); Assert.False(receipt.Result!.Passed);
        if (mode == "unsupported")
        { Assert.Equal((0, 0), (creates, fences)); Assert.False(Directory.Exists(Path.Combine(root, ".verification"))); }
        else
        {
            await aborted.Task.WaitAsync(TimeSpan.FromSeconds(10)); Assert.Equal((1, 1), (creates, fences));
            Assert.False(Directory.Exists(Path.Combine(root, ".verification", receipt.AttemptId.ToString("N"))));
        }
        Assert.False((await store.ReadSandboxVerificationAsync(owner.TenantId, p.Id, 1)).Observation?.Passed == true);
        await server.StopAsync();
    }

}
