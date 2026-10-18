using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AgentCore.Application.Execution;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Credentials;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Browser;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AgentCore.Api.Tests;

public sealed partial class TenantSessionAccessTests
{
    private sealed class PasswordResolutionRace
    {
        public Func<Task>? AfterResolution;
        public int Resolutions;
    }
    private sealed class PasswordRaceBrowser(IBrowser native, PasswordResolutionRace race) : IBrowser
    {
        public BrowserProviderDescriptor Provider => native.Provider;
        public BrowserHostPolicy HostPolicy => native.HostPolicy;
        public bool IsAvailable => native.IsAvailable;
        public ValueTask<BrowserResult> ExecuteAsync(BrowserRequest request, CancellationToken ct = default)
        {
            if (request.Command is BrowserFillCredential && request.TenantPasswordResolver is { } resolver)
                request = request with { TenantPasswordResolver = async (origin, token) => {
                    var value = await resolver(origin, token); Interlocked.Increment(ref race.Resolutions);
                    if (Interlocked.Exchange(ref race.AfterResolution, null) is { } after) await after();
                    return value;
                } };
            return native.ExecuteAsync(request, ct);
        }
    }
    [TenantBrowserChromiumTheory] [InlineData(false, false, false)] [InlineData(true, false, false)] [InlineData(false, true, false)] [InlineData(true, true, false)]
    [InlineData(false, false, true)] [InlineData(true, false, true)] [InlineData(false, true, true)] [InlineData(true, true, true)]
    public async Task Tenant_Browser_model_protected_fill_requires_exact_Credential_consent_and_returns_only_redacted_facts(bool sqlite, bool revokeAfterResolution, bool exceptional)
    {
        var root = Path.Combine(Path.GetTempPath(), "tenant-browser-password-model-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        const string password = "model-owned-private-password-7625";
        try
        {
            var model = new ApprovalRecordingModel(); var race = new PasswordResolutionRace();
            await using WebApplicationFactory<Program> baseFactory = sqlite ? new TenantApprovalSqliteFactory(root, model) : new ApprovalExecutionFactory();
            if (baseFactory is ApprovalExecutionFactory memory) model = memory.Model;
            await using var factory = baseFactory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services => {
                var options = (BrowserOptions)services.Single(d => d.ServiceType == typeof(BrowserOptions)).ImplementationInstance!;
                options.Enabled = true; options.Headless = true; options.Channel = null; options.FixtureEnabled = true; options.FixturePort = 0;
                options.ProfileRoot = Path.Combine(root, "browser"); options.PolicyMode = "Restricted"; options.ProfileMode = "EphemeralSession";
                options.NavigationOrigins = ["http://127.0.0.1:5091"]; options.TargetOrigins = null; options.InteractionOrigins = null; options.ResourceOrigins = [];
                services.Replace(ServiceDescriptor.Singleton<TenantBrowserEffectService>(p => new(p.GetRequiredService<IIamStore>(), p.GetRequiredService<ITenantRunStore>(),
                    p.GetRequiredService<ITenantApprovalStore>(), p.GetRequiredService<ITenantApprovedOperationStore>(), p.GetRequiredService<TenantBrowserOperationTarget>(),
                    p.GetRequiredService<IamAuthorizer>(), new PasswordRaceBrowser(p.GetRequiredService<IBrowser>(), race), p.GetRequiredService<TenantOperationAuthorizer>(),
                    p.GetRequiredService<IIdGenerator>(), p.GetRequiredService<TimeProvider>())));
            }));
            using var owner = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost"), AllowAutoRedirect = false });
            await Login(owner, "owner@example.test", "explicit-synthetic-test-password");
            var tenant = (await Json(await owner.GetAsync("/api/v2/account/tenants"))).GetProperty("items")[0].GetProperty("tenantId").GetGuid(); var api = $"/api/v2/tenants/{tenant}";
            var human = (await Json(await owner.GetAsync(api + "/access/me"))).GetProperty("principalId").GetGuid();
            var agent = await Json(await owner.PostAsJsonAsync(api + "/agents", new { templateId = "general-assistant", version = 32, expectedRevision = await Revision(owner, tenant) }));
            var instance = agent.GetProperty("instance").GetProperty("instanceId").GetGuid(); var executor = agent.GetProperty("principalId").GetGuid();
            var native = Assert.IsType<NativePlaywrightBrowser>(factory.Services.GetRequiredService<IBrowser>());
            var credential = await factory.Services.GetRequiredService<TenantCredentialService>().CreateAsync(human, tenant,
                new("Local login", CredentialKind.Password, CredentialPurpose.General, null, [native.Fixture.Origin!], password), await Revision(owner, tenant));
            await factory.Services.GetRequiredService<TenantCredentialBindingService>().BindAsync(human, tenant, instance, credential.Credential.CredentialId,
                "account", await Revision(owner, tenant), 1, 1);
            var session = await Json(await owner.PostAsJsonAsync(api + "/sessions", new { agentInstanceId = instance, expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = 2 }));
            var sessionId = session.GetProperty("sessionId").GetGuid(); var access = factory.Services.GetRequiredService<TenantAccessService>();
            var role = await access.SaveRoleAsync(human, tenant, null, "Reviewed Browser", ["browser.read", "browser.use"], await Revision(owner, tenant));
            var browserRole = role;
            await access.AssignRoleAsync(human, tenant, executor, role.RoleId, AccessScope.ForResource(tenant, "agent", instance.ToString("D")), await Revision(owner, tenant));
            role = await access.SaveRoleAsync(human, tenant, null, "Read conversation", ["sessions.read"], await Revision(owner, tenant));
            await access.AssignRoleAsync(human, tenant, executor, role.RoleId, TenantSessionRules.Scope(tenant, sessionId), await Revision(owner, tenant));
            role = await access.SaveRoleAsync(human, tenant, null, "Use exact login", exceptional ? ["credentials.read"] : ["credentials.read", "credentials.use"], await Revision(owner, tenant));
            await access.AssignRoleAsync(human, tenant, executor, role.RoleId, TenantCredentialRules.Scope(tenant, credential.Credential.CredentialId), await Revision(owner, tenant));
            if (revokeAfterResolution) race.AfterResolution = async () => {
                var state = (await factory.Services.GetRequiredService<IIamStore>().ReadTenantAsync(tenant))!;
                var changed = exceptional ? browserRole : role;
                await access.SaveRoleAsync(human, tenant, changed.RoleId, changed.Name, exceptional ? ["browser.read"] : ["credentials.read"], state.Tenant.Revision);
            };
            var scope = new TenantBrowserOwner(tenant, instance, executor, sessionId); Assert.Null(await native.BindTenantIsolationAsync(scope, BrowserProfileMode.EphemeralSession));
            var navigation = new BrowserNavigate(native.Fixture.Origin + "/credential-login?reflect=1"); var target = await native.PrepareTenantCommandAsync(scope, navigation);
            // Trusted local fixture setup. The actual protected fill below is proposed by the model and authorized through HTTP.
            Assert.Null((await native.ExecuteAsync(new(sessionId, navigation) { TenantOwner = scope, TenantWitness = target.Witness })).ErrorCode);
            var page = native.ContextFor(sessionId)!.Pages.Single(); Assert.Equal("", await page.GetByLabel("Password", new() { Exact = true }).InputValueAsync());
            model.ProposedBrowserCommand = """{"operation":"run_browser_command","command":{"tool":"browser.fill_credential","arguments":{"credentialRef":"account","target":{"by":"label","value":"Password"}}}}""";
            model.ProposedBrowserRead = new("discover-credential", ToolCatalog.CredentialsList, "{}");
            var source = (await Json(await owner.PostAsJsonAsync(api + $"/sessions/{sessionId}/messages", new { messageId = Guid.NewGuid(), text = "Fill the existing Password using the bound account Credential; do not submit.",
                expectedRevision = await Revision(owner, tenant), expectedSessionRevision = 1 }))).GetProperty("runId").GetGuid();
            if (baseFactory is ApprovalExecutionFactory enabled) Volatile.Write(ref enabled.Enabled, true);
            var runs = factory.Services.GetRequiredService<ITenantRunStore>(); var waiting = await AwaitBrowserRestartRun(runs, tenant, source, true);
            Assert.True(waiting.Status == AgentRunStatus.WaitingForApproval, $"{waiting.Status}: {waiting.Failure?.Code}: {waiting.Failure?.Summary}");
            var request = (await factory.Services.GetRequiredService<ITenantApprovalStore>().FindApprovalAsync(tenant, waiting.ApprovalRequestId!.Value))!;
            Assert.Equal("credentials.use", request.Permission); Assert.Equal("credential", request.ResourceType); Assert.Equal(exceptional ? ApprovalKind.ExceptionalGrant : ApprovalKind.Review, request.Kind);
            Assert.Equal(credential.Credential.CredentialId, request.Operation.BrowserCommand!.Credential!.CredentialId);
            Assert.Equal("", await page.GetByLabel("Password", new() { Exact = true }).InputValueAsync());
            var view = await Json(await owner.GetAsync(api + $"/approvals/{request.RequestId}")); Assert.True(view.GetProperty("review").GetProperty("canDecide").GetBoolean());
            Assert.Contains("Local login", view.GetRawText()); Assert.DoesNotContain(password, view.GetRawText());
            Assert.Equal(HttpStatusCode.OK, (await owner.PostAsJsonAsync(api + $"/approvals/{request.RequestId}/decision", new { decision = "approve", actionHash = request.Operation.ActionHash,
                expectedRevision = view.GetProperty("securityRevision").GetInt64(), expectedRequestRevision = request.Revision })).StatusCode);
            var completed = await AwaitBrowserRestartRun(runs, tenant, source, false);
            Assert.True(completed.Status == AgentRunStatus.Completed, $"{completed.Status}: {completed.Failure?.Code}: {completed.Failure?.Summary}");
            Assert.Equal(revokeAfterResolution ? "" : password, await page.GetByLabel("Password", new() { Exact = true }).InputValueAsync()); Assert.Equal(1, race.Resolutions);
            Assert.True(AgentRunToolCallCheckpoint.TryRead(completed.Checkpoint, out var messages));
            var result = Assert.Single(messages!, m => m.Role == ModelRole.Tool && m.Name == ToolCatalog.RequestAuthorization).Text;
            using var output = JsonDocument.Parse(result); Assert.Equal(!revokeAfterResolution, output.RootElement.GetProperty("commandCompleted").GetBoolean());
            Assert.False(output.RootElement.GetProperty("applicationOutcomeVerified").GetBoolean()); Assert.False(output.RootElement.GetProperty("replayAllowed").GetBoolean());
            Assert.Contains(model.Requests, r => r.Messages.Any(m => m.Role == ModelRole.Tool && m.Text == result));
            Assert.Contains(model.Requests, r => r.Messages.Any(m => m.Role == ModelRole.Tool && m.Name == ToolCatalog.CredentialsList && m.Text.Contains("tenantCredentialRead", StringComparison.Ordinal)));
            Assert.DoesNotContain(password, result); Assert.DoesNotContain(password, JsonSerializer.Serialize(model.Requests));
            var retained = JsonSerializer.Serialize(new { completed.Admission, completed.Checkpoint, completed.SideEffect });
            Assert.DoesNotContain(password, retained); Assert.DoesNotContain("ProtectedPayload", retained);
            var receipt = (await factory.Services.GetRequiredService<ITenantApprovedOperationStore>().FindOperationReceiptAsync(tenant, request.Operation.OperationId))!;
            Assert.Equal(revokeAfterResolution ? TenantOperationOutcome.Denied : TenantOperationOutcome.Completed, receipt.Outcome);
            if (revokeAfterResolution) Assert.Equal("permission_denied", receipt.ReasonCode);
            var grant = await factory.Services.GetRequiredService<ITenantApprovalStore>().FindOperationGrantAsync(tenant, request.RequestId);
            if (exceptional) { Assert.NotNull(grant); Assert.Equal(revokeAfterResolution ? OperationGrantStatus.Revoked : OperationGrantStatus.Consumed, grant.Status); Assert.Equal(3, grant.Revision); }
            else Assert.Null(grant);
            var current = (await factory.Services.GetRequiredService<IIamStore>().ReadTenantAsync(tenant))!;
            Assert.Equal(!exceptional && !revokeAfterResolution, (await factory.Services.GetRequiredService<IamAuthorizer>().CheckTenantAsync(current, executor,
                PermissionCatalog.Get("credentials.use"), TenantCredentialRules.Scope(tenant, credential.Credential.CredentialId), CancellationToken.None)).Allowed);
            var audits = await factory.Services.GetRequiredService<IIamStore>().ReadAuditAsync(tenant, 200);
            Assert.DoesNotContain(password, JsonSerializer.Serialize(audits)); Assert.Single(audits, a => a.Action == "operations.browser.command.dispatch");
            Assert.Single(audits, a => a.Action == "operations.browser.command");
            Assert.All(audits.Where(a => a.Action is "operations.browser.command" or "operations.browser.command.dispatch"), a => Assert.Equal(grant?.GrantId, a.Execution!.GrantId));
        }
        finally { SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
