using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using AgentCore.Application.Execution;
using AgentCore.Application.Ports;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Persistence;
using AgentCore.Infrastructure.Providers.Synthetic;
using AgentCore.Infrastructure.Workspaces;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AgentCore.Api.Tests;

public sealed partial class TenantSessionAccessTests
{
    public static IEnumerable<object[]> FileFinalReadAuthorityCases() =>
        from sqlite in new[] { false, true }
        from action in new[] { "artifact-content", "artifact-preview", "attachment-content", "attachment-preview", "attachment-copy", "home-list", "working-list", "workspace-access", "composer" }
        from boundary in new[] { "logout", "relogin", "password", "security" }
        select new object[] { sqlite, action, boundary };

    [Theory]
    [MemberData(nameof(FileFinalReadAuthorityCases))]
    public async Task File_HTTP_read_rechecks_original_human_after_the_final_source_comparison(bool sqlite, string action, string boundary)
    {
        var root = Path.Combine(Path.GetTempPath(), "tenant-file-final-human-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var race = new FileFinalReadRace();
        try
        {
            void Configure(IServiceCollection services)
            {
                if (sqlite) services.Replace(ServiceDescriptor.Singleton<IArtifactStore>(p => new SqliteArtifactStore(
                    p.GetRequiredService<IDbContextFactory<AgentCoreDbContext>>(), p.GetRequiredService<TimeProvider>(), Path.Combine(root, "artifacts"))));
                services.Replace(ServiceDescriptor.Singleton<ITenantRunExecution>(p => new MetadataExecutionGate(() => false,
                    new TenantRunExecution(p.GetRequiredService<ITenantRuntimeStore>(), p.GetRequiredService<ITenantRunStore>(),
                        p.GetRequiredService<ITenantRunDispatcher>(), p.GetRequiredService<IIdGenerator>(), p.GetRequiredService<TimeProvider>()))));
                services.Replace(ServiceDescriptor.Singleton<ITenantSessionStore>(p => new FileFinalReadSessionStore((ITenantSessionStore)p.GetRequiredService<IIamStore>(), race)));
                services.Replace(ServiceDescriptor.Singleton<ITenantAgentStore>(p => new FileFinalReadAgentStore((ITenantAgentStore)p.GetRequiredService<IIamStore>(), race)));
            }
            await using WebApplicationFactory<Program> factory = sqlite
                ? new NativeTenantSqliteFactory(root, new ScriptedLanguageModel(), Configure)
                : new NativeTenantMemoryFactory(root, new ScriptedLanguageModel(), Configure);
            using var owner = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost"), AllowAutoRedirect = false });
            await Login(owner, "owner@example.test", "explicit-synthetic-test-password");
            var tenant = (await Json(await owner.GetAsync("/api/v2/account/tenants"))).GetProperty("items")[0].GetProperty("tenantId").GetGuid();
            var api = $"/api/v2/tenants/{tenant}";
            var agent = await Json(await owner.PostAsJsonAsync(api + "/agents", new { templateId = "examiner", version = 1, expectedRevision = await Revision(owner, tenant) }));
            var instance = agent.GetProperty("instance").GetProperty("instanceId").GetGuid();
            var session = (await Json(await owner.PostAsJsonAsync(api + "/sessions", new { retentionKind = "saved", agentInstanceId = instance, expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = 1 }))).GetProperty("sessionId").GetGuid();
            Assert.True((await owner.PatchAsJsonAsync(api + $"/sessions/{session}", new { title = "Private file retirement sentinel", expectedRevision = await Revision(owner, tenant), expectedSessionRevision = 1 })).IsSuccessStatusCode);
            var office = factory.Services.GetRequiredService<IOfficeProcessor>();
            var bytes = office.CreateDocument(new("Private file retirement sentinel", [new("Section", ["Private file body retirement sentinel"], null)]), default).Bytes;
            string path;
            if (action.StartsWith("artifact-", StringComparison.Ordinal))
            {
                var runId = (await Json(await owner.PostAsJsonAsync(api + $"/sessions/{session}/messages", new { messageId = Guid.NewGuid(), text = "Prepare the exact private fixture", expectedRevision = await Revision(owner, tenant), expectedSessionRevision = 2 }))).GetProperty("runId").GetGuid();
                var run = (await factory.Services.GetRequiredService<ITenantRunStore>().FindTenantRunAsync(tenant, runId))!;
                var at = factory.Services.GetRequiredService<TimeProvider>().GetUtcNow();
                run = await factory.Services.GetRequiredService<ITenantRuntimeStore>().ApplyTenantRuntimeCommandAsync(run.Owner, runId, new AgentRunCommand.Claim(run.Revision, at, Guid.NewGuid(), at.AddMinutes(5)), Guid.NewGuid());
                var id = Guid.NewGuid();
                await factory.Services.GetRequiredService<ITenantArtifactStore>().CreateTenantAsync(new(run.Owner, session, runId, run.Claim!.Generation), id, "Private file retirement sentinel.docx", OfficeLimits.DocumentMime, bytes);
                path = api + $"/sessions/{session}/artifacts/{id}/" + (action == "artifact-preview" ? "office-preview" : "content");
                race.SessionReads = 2;
            }
            else if (action.StartsWith("attachment-", StringComparison.Ordinal))
            {
                using var body = new MultipartFormDataContent(); var file = new ByteArrayContent(bytes);
                file.Headers.ContentType = new MediaTypeHeaderValue(OfficeLimits.DocumentMime); body.Add(file, "file", "Private file retirement sentinel.docx");
                var id = (await Json(await owner.PostAsync(api + $"/sessions/{session}/attachments?uploadId={Guid.NewGuid()}&expectedRevision={await Revision(owner, tenant)}&expectedSessionRevision=2", body))).GetProperty("attachmentId").GetGuid();
                path = api + $"/sessions/{session}/attachments/{id}/" + (action == "attachment-copy" ? "materialize" : action == "attachment-preview" ? "office-preview" : "content");
                race.SessionReads = 2;
            }
            else
            {
                var working = action is "working-list" or "workspace-access";
                var logical = working ? "/working" : "/home";
                var physical = working ? AgentWorkspacePhysicalPaths.WorkingDirectory(Path.Combine(root, "workspaces"), instance, session)
                    : AgentWorkspacePhysicalPaths.SpaceHomeDirectory(Path.Combine(root, "workspaces"), tenant, (await factory.Services.GetRequiredService<ITenantSpaceStore>().FindPrivateSpaceAsync(tenant,instance))!.SpaceId);
                var files = new CanonicalWorkspaceFilesystem(physical, logical); files.Ensure();
                await files.WriteAsync(logical + "/Private file retirement sentinel.txt", "Private file body retirement sentinel"u8.ToArray(), 1024, 4096);
                path = action == "composer" ? api + $"/agents/{instance}/composer?category=homeFile"
                    : api + (working ? $"/sessions/{session}/workspace" : $"/agents/{instance}/workspace") + (action == "workspace-access" ? "/access" : "");
                race.AgentReads = action == "workspace-access" ? 2 : 3;
            }
            race.AfterRead = async () => {
                if (boundary == "security")
                    Assert.Equal(HttpStatusCode.Created, (await owner.PostAsJsonAsync(api + "/roles", new { name = "Concurrent file reader role", permissions = new[] { "sessions.read" }, expectedRevision = await Revision(owner, tenant) })).StatusCode);
                else if (boundary == "password")
                    Assert.True((await owner.PostAsJsonAsync("/api/v2/account/password", new { currentPassword = "explicit-synthetic-test-password", newPassword = "replacement-synthetic-password" })).IsSuccessStatusCode);
                else {
                    Assert.Equal(HttpStatusCode.NoContent, (await owner.PostAsync("/api/v2/auth/logout", null)).StatusCode);
                    if (boundary == "relogin") await Login(owner, "owner@example.test", "explicit-synthetic-test-password");
                }
            };
            using var refused = await owner.GetAsync(path);
            Assert.Null(race.AfterRead); Assert.Equal(0, race.SessionReads); Assert.Equal(0, race.AgentReads);
            Assert.Equal(boundary == "security" ? HttpStatusCode.Conflict : HttpStatusCode.Forbidden, refused.StatusCode);
            var failure = await Json(refused);
            Assert.Equal(boundary == "security" ? action == "composer" ? "composer_source_changed" : "stale_revision" : "initiator_unavailable", failure.GetProperty("reasonCode").GetString());
            Assert.DoesNotContain("sentinel", failure.GetRawText());
            foreach (var field in new[] { "items", "displayName", "workspace", "agentName", "format" }) Assert.False(failure.TryGetProperty(field, out _));
            if (boundary is "logout" or "password") await Login(owner, "owner@example.test", boundary == "password" ? "replacement-synthetic-password" : "explicit-synthetic-test-password");
            using var fresh = await owner.GetAsync(path); Assert.Equal(HttpStatusCode.OK, fresh.StatusCode);
            if (action.EndsWith("-content", StringComparison.Ordinal)) Assert.Equal(bytes, await fresh.Content.ReadAsByteArrayAsync());
            else if (action.EndsWith("-preview", StringComparison.Ordinal)) {
                var preview = await Json(fresh); Assert.Equal("docx", preview.GetProperty("format").GetString());
                Assert.Equal(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes)), preview.GetProperty("sha256").GetString());
                Assert.False(preview.GetProperty("emptyBody").GetBoolean());
            }
            else Assert.Contains("sentinel", await fresh.Content.ReadAsStringAsync());
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private sealed class FileFinalReadRace
    {
        public int SessionReads; public int AgentReads; public Func<Task>? AfterRead;
        public async ValueTask After(bool agent)
        {
            ref var remaining = ref (agent ? ref AgentReads : ref SessionReads);
            if (Volatile.Read(ref remaining) > 0 && Interlocked.Decrement(ref remaining) == 0
                && Interlocked.Exchange(ref AfterRead, null) is { } hook) await hook();
        }
    }
    private sealed class FileFinalReadSessionStore(ITenantSessionStore inner, FileFinalReadRace race) : ITenantSessionStore
    {
        public ValueTask<IReadOnlyList<TenantSessionRetentionTarget>> ListDueSessionsAsync(TenantSessionRetentionTarget? after, int limit, CancellationToken ct = default) => inner.ListDueSessionsAsync(after, limit, ct);
        public ValueTask<TenantSessionRetentionTarget?> FindExpiredSessionAsync(Guid tenant, Guid session, CancellationToken ct = default) => inner.FindExpiredSessionAsync(tenant, session, ct);
        public ValueTask<bool> RetireDueSessionAsync(TenantSessionRetentionTarget target, Guid auditId, CancellationToken ct = default) => inner.RetireDueSessionAsync(target, auditId, ct);

        public async ValueTask<TenantSession?> FindTenantSessionAsync(Guid tenantId, Guid sessionId, CancellationToken ct = default)
        { var value = await inner.FindTenantSessionAsync(tenantId, sessionId, ct); await race.After(false); return value; }
        public ValueTask<TenantSessionPage> ListTenantSessionsAsync(Guid tenantId, IReadOnlyList<Guid>? allowedIds, int page, int pageSize, CancellationToken ct = default, bool includeArchived = true, SessionSurface? surface = null, Guid? instanceId = null, bool recentFirst = false) => inner.ListTenantSessionsAsync(tenantId, allowedIds, page, pageSize, ct, includeArchived, surface, instanceId, recentFirst);
        public ValueTask<TenantSession?> TryChangeTenantSessionAsync(TenantSecurityState next, long expectedSecurityRevision, TenantSessionChange change, TenantSessionInitiator initiator, DateTimeOffset at, AuthorizationAudit audit, CancellationToken ct = default) => inner.TryChangeTenantSessionAsync(next, expectedSecurityRevision, change, initiator, at, audit, ct);
        public ValueTask<bool> TryCreateTenantSessionAsync(TenantSecurityState next, long expectedSecurityRevision, TenantSessionRoot owner, SessionSnapshot snapshot, TenantSessionInitiator initiator, long expectedInstanceRevision, AuthorizationAudit audit, CancellationToken ct = default) => inner.TryCreateTenantSessionAsync(next, expectedSecurityRevision, owner, snapshot, initiator, expectedInstanceRevision, audit, ct);
    }
    private sealed class FileFinalReadAgentStore(ITenantAgentStore inner, FileFinalReadRace race) : ITenantAgentStore
    {
        public ValueTask<IReadOnlyList<Guid>> ListAssistantTenantPageAsync(int page, int limit, CancellationToken ct = default) => inner.ListAssistantTenantPageAsync(page, limit, ct);
        public ValueTask<AgentCore.Domain.Security.AuthorizationAudit?> FindAgentOperationAsync(Guid tenant, Guid operation, CancellationToken ct = default) => inner.FindAgentOperationAsync(tenant, operation, ct);
        public ValueTask<bool> TryProvisionGeneralAssistantAsync(AgentCore.Domain.Security.TenantSecurityState next, long expected, AgentCore.Domain.Definitions.AgentInstance instance, IReadOnlyList<AgentCore.Domain.Definitions.SkillSpec> skills, AgentCore.Domain.Security.AuthorizationAudit audit, CancellationToken ct = default) => inner.TryProvisionGeneralAssistantAsync(next, expected, instance, skills, audit, ct);
        public ValueTask<TenantAgent?> TryAdoptGeneralAssistantAsync(AgentCore.Domain.Security.TenantSecurityState next, long expected, TenantAgentConfigurationChange change, DateTimeOffset at, AgentCore.Domain.Security.AuthorizationAudit audit, CancellationToken ct = default) => inner.TryAdoptGeneralAssistantAsync(next, expected, change, at, audit, ct);

        public async ValueTask<TenantAgent?> FindTenantAgentAsync(Guid tenantId, Guid instanceId, CancellationToken ct = default)
        { var value = await inner.FindTenantAgentAsync(tenantId, instanceId, ct); await race.After(true); return value; }
        public ValueTask<TenantAgentConfigurationSnapshot?> ReadTenantAgentConfigurationAsync(Guid tenantId, Guid instanceId, CancellationToken ct = default) => inner.ReadTenantAgentConfigurationAsync(tenantId, instanceId, ct);
        public ValueTask<TenantAgent?> TryChangeTenantAgentConfigurationAsync(TenantSecurityState next, long expectedSecurityRevision, TenantAgentConfigurationChange change, DateTimeOffset at, AuthorizationAudit audit, CancellationToken ct = default) => inner.TryChangeTenantAgentConfigurationAsync(next, expectedSecurityRevision, change, at, audit, ct);
        public ValueTask<TenantAgentPage> ListTenantAgentsAsync(Guid tenantId, IReadOnlyList<Guid>? allowedInstanceIds, int page, int pageSize, CancellationToken ct = default) => inner.ListTenantAgentsAsync(tenantId, allowedInstanceIds, page, pageSize, ct);
        public ValueTask<bool> TryCreateTenantAgentAsync(TenantSecurityState next, long expectedSecurityRevision, AgentInstance instance, IReadOnlyList<SkillSpec> skills, AuthorizationAudit audit, CancellationToken ct = default, HumanSessionReference? initiator = null) => inner.TryCreateTenantAgentAsync(next, expectedSecurityRevision, instance, skills, audit, ct, initiator);
        public ValueTask<TenantAgent?> TrySetTenantAgentLifecycleAsync(TenantSecurityState next, long expectedSecurityRevision, Guid instanceId, long expectedInstanceRevision, AgentInstanceLifecycle lifecycle, DateTimeOffset at, AuthorizationAudit audit, CancellationToken ct = default, HumanSessionReference? initiator = null) => inner.TrySetTenantAgentLifecycleAsync(next, expectedSecurityRevision, instanceId, expectedInstanceRevision, lifecycle, at, audit, ct, initiator);
    }
}
