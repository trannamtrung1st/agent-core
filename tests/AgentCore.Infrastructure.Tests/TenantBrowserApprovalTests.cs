using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Execution;
using AgentCore.Application.Security;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Providers;
using AgentCore.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Fixture = AgentCore.Infrastructure.Tests.TenantCredentialIntegrationTests.Fixture;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantBrowserApprovalTests
{
    private sealed record Setup(Guid Tenant, TenantSessionInitiator Human, TenantAgent Agent, AgentRun Run, RoleAssignment Use,
        TenantCredentialBinding? Password = null, RoleAssignment? CredentialUse = null);
    private sealed class TargetProbe(TenantBrowserOwner owner) : IBrowser, ITenantBrowserCommandTarget
    {
        public Guid Context = Guid.NewGuid();
        public long CommandRevision;
        public int Reads;
        public BrowserProfileMode Mode = BrowserProfileMode.EphemeralSession;
        public string? Error { get; set; }
        public BrowserProviderDescriptor Provider => new("controlled", "Controlled target", new HashSet<BrowserFeature>());
        public bool IsAvailable => true;
        public BrowserHostPolicy HostPolicy => new(true, true, BrowserInteractionMode.InteractiveDemo, ["http://127.0.0.1:5091"]);
        public ValueTask<BrowserResult> ExecuteAsync(BrowserRequest request, CancellationToken ct = default) => throw new InvalidOperationException("Review must not execute Browser work.");
        public ValueTask<TenantBrowserCommandPreparation> PrepareTenantCommandAsync(TenantBrowserOwner actual, BrowserCommand command, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested(); Assert.Equal(owner, actual); Reads++;
            return ValueTask.FromResult(Error is not null ? new TenantBrowserCommandPreparation(Error) : new(null,
                new(actual, Guid.NewGuid(), 0, CommandRevision, Mode, Context, "pg_owned", 1, null, new string('a', 64), BrowserCommandIdentity.Hash(command),
                    command is BrowserFillCredential ? "http://127.0.0.1:5091" : null), "http://127.0.0.1:5091"));
        }
    }
    private static async Task<Setup> Prepare(Fixture f, bool permissions = true, bool password = false, bool credentialUseAllowed = true)
    {
        var owner = await f.Identity.BootstrapAsync("owner@example.test", "Owner", "strong-owner-password", "Alpha", "alpha");
        var login = (await f.Identity.SignInAsync("owner@example.test", "strong-owner-password"))!.Session;
        var human = new TenantSessionInitiator(owner.AccountId, login.SessionId, owner.OwnerPrincipalId);
        async Task<long> Revision() => (await f.Iam.ReadTenantAsync(owner.TenantId))!.Tenant.Revision;
        var agent = await f.Agents.CreateAsync(human.PrincipalId, owner.TenantId, "general-assistant", 32, await Revision());
        TenantCredentialBinding? bound = null; RoleAssignment? credentialUse = null;
        if (password)
        {
            var credential = await f.Service.CreateAsync(human.PrincipalId, owner.TenantId, new("Browser account", AgentCore.Domain.Credentials.CredentialKind.Password,
                AgentCore.Domain.Credentials.CredentialPurpose.General, null, ["http://127.0.0.1:5091"], "tenant-bound-private-password"), await Revision());
            await f.BindingService.BindAsync(human.PrincipalId, owner.TenantId, agent.Instance.InstanceId, credential.Credential.CredentialId, "account", await Revision(), agent.Instance.Revision, 1);
            agent = (await ((ITenantAgentStore)f.Iam).FindTenantAgentAsync(owner.TenantId, agent.Instance.InstanceId))!;
            bound = Assert.Single((await ((ITenantCredentialBindingStore)f.Iam).ReadTenantCredentialBindingsAsync(owner.TenantId, agent.Instance.InstanceId))!.Bindings);
            var credentialRole = await f.Access.SaveRoleAsync(human.PrincipalId, owner.TenantId, null, "Use bound Browser password",
                credentialUseAllowed ? ["credentials.read", "credentials.use"] : ["credentials.read"], await Revision());
            credentialUse = await f.Access.AssignRoleAsync(human.PrincipalId, owner.TenantId, agent.PrincipalId, credentialRole.RoleId,
                TenantCredentialRules.Scope(owner.TenantId, credential.Credential.CredentialId), await Revision());
        }
        var session = await f.Sessions.CreateAsync(owner.TenantId, agent.Instance.InstanceId, human, SessionMode.Text, await Revision(), agent.Instance.Revision);
        var role = await f.Access.SaveRoleAsync(human.PrincipalId, owner.TenantId, null, "Browser command", permissions ? ["browser.read", "browser.use"] : ["browser.read"], await Revision());
        var use = await f.Access.AssignRoleAsync(human.PrincipalId, owner.TenantId, agent.PrincipalId, role.RoleId,
            AccessScope.ForResource(owner.TenantId, "agent", agent.Instance.InstanceId.ToString("D")), await Revision());
        var read = await f.Access.SaveRoleAsync(human.PrincipalId, owner.TenantId, null, "Read own Session", ["sessions.read"], await Revision());
        await f.Access.AssignRoleAsync(human.PrincipalId, owner.TenantId, agent.PrincipalId, read.RoleId, TenantSessionRules.Scope(owner.TenantId, session.Owner.SessionId), await Revision());
        var admitted = await f.RunService().SubmitTextAsync(owner.TenantId, session.Owner.SessionId, human, f.Ids.NewId(), "Inspect the Browser", await Revision(), 1);
        var run = (await ((ITenantRunStore)f.Iam).FindTenantRunAsync(owner.TenantId, admitted.RunId))!;
        var at = f.Clock.GetUtcNow();
        run = await ((ITenantRuntimeStore)f.Iam).ApplyTenantRuntimeCommandAsync(run.Owner, run.AgentRunId,
            new AgentRunCommand.Claim(run.Revision, at, f.Ids.NewId(), at.AddMinutes(5)), f.Ids.NewId());
        return new(owner.TenantId, human, agent, run, use, bound, credentialUse);
    }
    private static TargetProbe Probe(Setup s) => new(new(s.Tenant, s.Agent.Instance.InstanceId, s.Agent.PrincipalId, s.Run.SessionId));
    private static TenantOperationAuthorizer Operations(Fixture f, IIamStore store) => new(store, (ITenantRunStore)store, (ITenantAgentStore)store,
        new(store), new((ITenantAuthorizationAuditStore)store, f.Ids, f.Clock));
    private static TenantApprovalService Service(Fixture f, IBrowser browser, IIamStore? store = null)
    {
        store ??= f.Iam;
        return new(store, (ITenantApprovalStore)store, (ITenantAgentStore)store, Operations(f, store), new(store), f.Identity, f.Ids, f.Clock,
            f.RunConfiguration(), ModelCatalogFactory.Synthetic(), SyntheticProviderAliases.Default, (ITenantApprovedOperationStore)store,
            browserTargets: new((ITenantSessionStore)store, f.RunConfiguration(), store, new(store), browser,
                new((ITenantCredentialBindingStore)store, f.RunConfiguration(), f.Protector, (ITenantRunStore)store)));
    }
    private const string Navigate = """{"url":"http://127.0.0.1:5091/browser-native.html"}""";
    private static ValueTask<TenantApprovalPreparationResult> Request(TenantApprovalService service, Setup s, string tool = ToolCatalog.BrowserNavigate, string args = Navigate) =>
        service.PrepareBrowserAsync(new(s.Tenant, s.Run.AgentRunId), tool, args, s.Run.Revision, s.Run.Claim!.Generation);

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_Browser_Review_retains_exact_consent_across_reopen_and_never_dispatches_or_grants(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true); var s = await Prepare(f); var probe = Probe(s); var service = Service(f, probe);
        var result = await Request(service, s); Assert.True(result.Request is not null, result.Decision + ": " + result.ReasonCode);
        var request = result.Request!;
        Assert.Equal(OperationDecisionKind.RequiresApproval, result.Decision); Assert.Equal(ApprovalKind.Review, request.Kind);
        Assert.Equal("browser.use", request.Permission); Assert.Equal(TenantOperationKind.BrowserCommand, request.Operation.Kind);
        Assert.Equal("http://127.0.0.1:5091", request.Snapshot!.Browser!.Origin);
        Assert.DoesNotContain("ProviderEpoch", JsonSerializer.Serialize(request)); Assert.DoesNotContain("TenantWitness", JsonSerializer.Serialize(request));
        var waiting = (await ((ITenantRunStore)f.Iam).FindTenantRunAsync(s.Tenant, s.Run.AgentRunId))!;
        Assert.Equal(AgentRunStatus.WaitingForApproval, waiting.Status); Assert.Null(waiting.Claim);
        IIamStore reopened = sqlite ? new SqliteIamStore(f.Contexts!, builtIns: f.BuiltIns, clock: f.Clock) : f.Iam;
        var saved = await Service(f, probe, reopened).ReadViewAsync(s.Tenant, request.RequestId, s.Human);
        Assert.Equal(request, saved.Request); Assert.True(saved.Review!.CanDecide); Assert.Contains(saved.Review.Changes, c => c.Field == "browserArguments" && c.After is string text && text.Contains("URL: http://127.0.0.1:5091/browser-native.html", StringComparison.Ordinal));
        // A different native process epoch alone is not durable consent identity; the retained physical target is.
        Assert.True((await service.ReadViewAsync(s.Tenant, request.RequestId, s.Human)).Review!.CanDecide);
        await service.DecideAsync(s.Tenant, request.RequestId, s.Human, true, request.Operation.ActionHash, saved.SecurityRevision, request.Revision);
        Assert.Null(await ((ITenantApprovalStore)reopened).FindOperationGrantAsync(s.Tenant, request.RequestId));
        Assert.Equal(1, await service.DispatchPendingAsync()); Assert.Equal(0, await service.DispatchPendingAsync());
        var resumed = (await ((ITenantRunStore)f.Iam).FindTenantRunAsync(s.Tenant, s.Run.AgentRunId))!; var at = f.Clock.GetUtcNow();
        resumed = await ((ITenantRuntimeStore)f.Iam).ApplyTenantRuntimeCommandAsync(resumed.Owner, resumed.AgentRunId,
            new AgentRunCommand.Claim(resumed.Revision, at, f.Ids.NewId(), at.AddMinutes(5)), f.Ids.NewId());
        var effects = new TenantApprovedOperationService(f.Iam, (ITenantRunStore)f.Iam, (ITenantAgentStore)f.Iam, (ITenantApprovalStore)f.Iam,
            (ITenantApprovedOperationStore)f.Iam, Operations(f, f.Iam), f.RunConfiguration(), f.Ids, f.Clock, ModelCatalogFactory.Synthetic(), SyntheticProviderAliases.Default);
        Assert.Equal("tenant_effect_unavailable", (await Assert.ThrowsAsync<AccessDeniedException>(() => effects.ExecuteAsync(new(s.Tenant, resumed.AgentRunId), request.RequestId,
            request.Operation.OperationId, resumed.Claim!.Generation, request.Operation.ActionHash).AsTask())).ReasonCode);
        Assert.Null(await ((ITenantApprovedOperationStore)f.Iam).FindOperationReceiptAsync(s.Tenant, request.Operation.OperationId));
        probe.Context = Guid.NewGuid(); probe.CommandRevision++;
        var changedTarget = request.Operation.BrowserCommand! with { TargetJson = TenantApprovalRules.BrowserTarget(new(new(s.Tenant, s.Agent.Instance.InstanceId, s.Agent.PrincipalId, s.Run.SessionId),
            Guid.NewGuid(), 0, 10, BrowserProfileMode.EphemeralSession, Guid.NewGuid(), "pg_changed", 2, null, null, ""), "http://127.0.0.1:5091") };
        var correlated = TenantApprovalRules.PrepareBrowser(f.Ids.NewId(), s.Agent.Instance.InstanceId, changedTarget, 99, 99);
        Assert.Equal(request.Fingerprint, correlated.OperationFingerprint); Assert.NotEqual(request.Operation.ActionHash, correlated.ActionHash);
        Assert.Equal(request.RequestId, (await ((ITenantApprovalStore)reopened).FindRootApprovalAsync(s.Tenant, request.RootActivationId, s.Agent.PrincipalId, correlated))!.RequestId);
    }

    [Theory] [InlineData(false, "target")] [InlineData(true, "target")] [InlineData(false, "revoke")] [InlineData(true, "revoke")]
    [InlineData(false, "isolation")] [InlineData(true, "isolation")]
    public async Task Tenant_Browser_Review_rechecks_target_mode_and_raw_executor_permission_before_decision(bool sqlite, string change)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true); var s = await Prepare(f); var probe = Probe(s); var service = Service(f, probe);
        var request = (await Request(service, s)).Request!;
        if (change == "target") probe.CommandRevision++;
        if (change == "isolation") probe.Mode = BrowserProfileMode.PersistentAgent;
        if (change == "revoke") await f.Access.RevokeAssignmentAsync(s.Human.PrincipalId, s.Tenant, s.Use.AssignmentId, (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision);
        var view = await service.ReadViewAsync(s.Tenant, request.RequestId, s.Human); Assert.False(view.Review!.CanDecide); Assert.True(view.Review.CanCancel);
        if (change == "target") await Assert.ThrowsAsync<SecurityConflictException>(() => service.DecideAsync(s.Tenant, request.RequestId, s.Human, true,
            request.Operation.ActionHash, view.SecurityRevision, request.Revision).AsTask());
        else await Assert.ThrowsAsync<AccessDeniedException>(() => service.DecideAsync(s.Tenant, request.RequestId, s.Human, true,
            request.Operation.ActionHash, view.SecurityRevision, request.Revision).AsTask());
        Assert.Empty(await ((ITenantApprovalStore)f.Iam).PendingApprovalSignalsAsync(10));
        await service.CancelAsync(s.Tenant, request.RequestId, s.Human, request.Operation.ActionHash, view.SecurityRevision, request.Revision);
        Assert.Null(await ((ITenantApprovalStore)f.Iam).FindOperationGrantAsync(s.Tenant, request.RequestId));
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_Browser_preparation_requires_raw_use_before_any_provider_target_read(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true); var s = await Prepare(f, permissions: false); var probe = Probe(s);
        Assert.Equal("permission_denied", (await Assert.ThrowsAsync<AccessDeniedException>(() => Request(Service(f, probe), s).AsTask())).ReasonCode);
        Assert.Equal(0, probe.Reads); Assert.Empty((await ((ITenantApprovalStore)f.Iam).ListApprovalsAsync(s.Tenant, null, null, new())).Items);
        var target = new PreparedBrowserCommand(s.Run.SessionId, ToolCatalog.BrowserNavigate, Navigate, BrowserCommandIdentity.Hash(new BrowserNavigate("http://127.0.0.1:5091/browser-native.html")),
            JsonSerializer.Serialize(new BrowserReviewTarget(BrowserProfileMode.EphemeralSession, null, null, null, null, null, 0, null)), false);
        var op = TenantApprovalRules.PrepareBrowser(f.Ids.NewId(), s.Agent.Instance.InstanceId, target, s.Agent.Instance.Revision, s.Agent.Instance.PersonaRevision);
        var at = f.Clock.GetUtcNow(); var state = (await f.Iam.ReadTenantAsync(s.Tenant))!;
        var request = new TenantApprovalRequest(f.Ids.NewId(), s.Tenant, s.Run.AgentRunId, s.Run.SessionId, s.Run.Admission.Activation.RootActivationId,
            s.Agent.PrincipalId, s.Human.PrincipalId, ApprovalKind.Review, op, TenantApprovalStatus.Pending, 1, at, at.AddHours(1), TenantBrowserOperationRules.Snapshot(s.Agent, target));
        // Bypassing Application preparation cannot bypass transactional executor checks.
        await Assert.ThrowsAsync<AccessDeniedException>(() => ((ITenantApprovalStore)f.Iam).TryCreateApprovalAsync(state with { Tenant = state.Tenant with { Revision = state.Tenant.Revision + 1 } },
            state.Tenant.Revision, request, s.Run.Revision, s.Run.Claim!.Generation, new(f.Ids.NewId(), s.Tenant, s.Agent.PrincipalId, "approvals.request", request.RequestId.ToString("D"), "allowed", "approval_pending", at)).AsTask());
        Assert.Equal(AgentRunStatus.Running, (await ((ITenantRunStore)f.Iam).FindTenantRunAsync(s.Tenant, s.Run.AgentRunId))!.Status);
    }
}
