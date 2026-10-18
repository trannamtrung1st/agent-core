using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;
using AgentCore.Infrastructure.Persistence;
using AgentCore.Infrastructure.Identity;
using AgentCore.Infrastructure.Sandbox;
using AgentCore.Infrastructure.Workspaces;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantDefinitionIntegrationTests
{
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_instance_sandbox_verification_SDK_lost_create_and_cleanup_keep_exact_instance_ownership(bool sqlite)
    {
        var root = Path.Combine(Path.GetTempPath(), "instance-verification-sdk-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var f = await Fixture.Create(sqlite);
            var owner = await f.Identity.BootstrapAsync("owner@example.test", "Owner", "strong-owner-password", "Alpha", "alpha");
            var human = await SetupHuman(f, owner.AccountId, owner.OwnerPrincipalId); var store = (ITenantSandboxVerificationStore)f.Iam;
            async ValueTask<long> Revision() => (await f.Iam.ReadTenantAsync(owner.TenantId))!.Tenant.Revision;
            var builder = WebApplication.CreateSlimBuilder(); builder.Logging.ClearProviders(); await using var server = builder.Build(); server.Urls.Add("http://127.0.0.1:0");
            var creates = 0; var retirements = 0; string? profileId = null; Guid instance = default; Func<Task>? duringCreate = null;
            server.Run(async http =>
            {
                Assert.Equal("synthetic-only-key", http.Request.Headers["OPEN-SANDBOX-API-KEY"]);
                var attempt = (await store.FindSandboxVerificationAsync(owner.TenantId, profileId!, instanceId: instance))!;
                var label = owner.TenantId.ToString("N") + "." + attempt.AttemptId.ToString("N");
                if (http.Request.Path == "/agent-core/generations/capabilities")
                    await http.Response.WriteAsJsonAsync(new { contractVersion = 1, verificationFencing = true });
                else if (http.Request.Path == "/agent-core/verifications/retire")
                {
                    retirements++;
                    using var body = await System.Text.Json.JsonDocument.ParseAsync(http.Request.Body);
                    Assert.Equal(owner.TenantId.ToString("N"), body.RootElement.GetProperty("tenant").GetString());
                    Assert.Equal(attempt.AttemptId.ToString("N"), body.RootElement.GetProperty("attempt").GetString());
                    await http.Response.WriteAsJsonAsync(new { contractVersion = 1, tenant = owner.TenantId.ToString("N"), attempt = attempt.AttemptId.ToString("N"), retired = true });
                }
                else
                {
                    Assert.Equal("POST", http.Request.Method);
                    creates++;
                    using var body = await System.Text.Json.JsonDocument.ParseAsync(http.Request.Body);
                    Assert.Equal(label, body.RootElement.GetProperty("metadata").GetProperty("agent-core-image-verification").GetString());
                    if (duringCreate is { } change) await change();
                    http.Response.StatusCode = 500; await http.Response.WriteAsync("private-instance-upstream-body");
                }
            });
            await server.StartAsync();
            var p = SetupProfiles(f, new OpenSandboxOptions { Enabled = true, AllowLocalImageIds = true, CanonicalWorkspaceRoot = root, WorkspaceDockerHostRoot = root,
                Endpoint = server.Urls.Single(), ApiKey = "synthetic-only-key" });
            var profile = (await p.Profiles.SaveAsync(owner.TenantId, null, human, SetupInput("SDK setup") with { ImageReference = "sha256:" + new string('a', 64) }, await Revision(), 0)).Profile;
            var s = await SetupAgent(f, owner.TenantId, human.PrincipalId, new(profile.Id, [profile.Id]), p.Images, p.Ready); instance = s.Agent.Instance.InstanceId; profileId = profile.Id;
            var guard = new TenantSandboxImageAuthorization(f.Iam, (ITenantSandboxProfileStore)f.Iam, f.Identity, f.Authorizer, SetupResolver(f));
            var real = new OpenSandboxService(p.Options, new InMemorySandboxStore(), null!, f.SessionStore, new InMemoryAgentRunStore(new InMemoryMemoryStore(), new SystemDiagnosticIdSource()),
                TimeProvider.System, imageAuthority: p.Images, tenantVerifications: store, verificationAuthority: guard);
            duringCreate = async () => { _ = await s.Configuration.PatchSettingsAsync(human.PrincipalId, owner.TenantId, instance, "sandboxPolicy", await Revision(), 1, Fields(new { cpu = .5 }), [], initiator: human); };
            var result = await InstanceVerification(f, p, real).VerifyAsync(owner.TenantId, instance, profile.Id, human, await Revision(), 1, 1);
            Assert.Equal(1, creates); Assert.Equal(1, retirements); Assert.Equal(SandboxVerificationStatus.Retired, result.Attempt!.Status); Assert.Null(result.Observation);
            Assert.DoesNotContain("private-instance-upstream-body", System.Text.Json.JsonSerializer.Serialize(result));
            Assert.Empty(Directory.EnumerateDirectories(Path.Combine(root, ".verification"))); Assert.Equal(0, p.Runner.Calls);
            p.Runner.Clean = false;
            var pending = (await InstanceVerification(f, p).VerifyAsync(owner.TenantId, instance, profile.Id, human, await Revision(), 2, 1)).Attempt!;
            Assert.Equal(SandboxVerificationStatus.CleanupPending, pending.Status);
            var mounts = new CanonicalSandboxMountResolver(p.Options).Verification(pending.AttemptId); var sibling = new CanonicalSandboxMountResolver(p.Options).Verification(Guid.NewGuid());
            var claim = Assert.Single(await store.ClaimSandboxVerificationCleanupAsync(DateTimeOffset.UtcNow, f.Ids.NewId()));
            Assert.False(await real.CleanupAsync(claim with { InstanceId = Guid.NewGuid() })); Assert.False(await real.CleanupAsync(claim with { InstanceId = null })); Assert.Equal(1, retirements);
            await f.Identity.SignOutAsync(human.AuthenticationSessionId);
            Assert.True(await real.CleanupAsync(claim)); Assert.Equal(2, retirements); Assert.Equal(1, creates);
            Assert.False(Directory.Exists(mounts.VerificationRoot)); Assert.True(Directory.Exists(sibling.VerificationRoot));
            Assert.True(await store.FinishSandboxVerificationCleanupAsync(claim, true, DateTimeOffset.UtcNow)); Assert.Equal(1, p.Runner.Calls);
            await server.StopAsync();
        }
        finally { if (Directory.Exists(root)) CanonicalWorkspaceFilesystem.PurgeOwner(root, default); }
    }
}
