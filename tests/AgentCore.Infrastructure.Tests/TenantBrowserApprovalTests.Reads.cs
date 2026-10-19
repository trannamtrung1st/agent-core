using System.Text.Json;
using AgentCore.Application.Execution;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using Fixture = AgentCore.Infrastructure.Tests.TenantCredentialIntegrationTests.Fixture;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantBrowserApprovalTests
{
    private sealed class ReadProbe(TargetProbe target, BrowserProfileMode? profileMode = null) : IBrowser, ITenantBrowserCommandTarget, ITenantBrowserBinding
    {
        public int Bindings, Executions, Reads;
        public Func<Task>? BeforeGate, AfterRead;
        public BrowserProviderDescriptor Provider => target.Provider;
        public BrowserHostPolicy HostPolicy => profileMode is { } mode ? target.HostPolicy with { ProfileMode = mode } : target.HostPolicy;
        public bool IsAvailable => true;
        public void BindTenantSession(TenantBrowserOwner owner) => Bindings++;
        public ValueTask<string?> EnsureTenantIsolationAsync(TenantBrowserOwner owner, BrowserProfileMode mode, CancellationToken ct = default) { Bindings++; return ValueTask.FromResult<string?>(null); }
        public ValueTask<string?> BindTenantIsolationAsync(TenantBrowserOwner owner, BrowserProfileMode mode, CancellationToken ct = default) => throw new InvalidOperationException("Reads cannot transition isolation.");
        public ValueTask ReleaseTenantAsync(TenantBrowserOwner owner, CancellationToken ct = default) => throw new InvalidOperationException("Reads cannot release views.");
        public ValueTask<TenantBrowserCommandPreparation> PrepareTenantCommandAsync(TenantBrowserOwner owner, BrowserCommand command, CancellationToken ct = default) => target.PrepareTenantCommandAsync(owner, command, ct);
        public async ValueTask<BrowserResult> ExecuteAsync(BrowserRequest request, CancellationToken ct = default)
        {
            Executions++; Assert.True(request.TenantReadOnly); Assert.NotNull(request.TenantDispatchCheck); Assert.True(TenantBrowserInspectionContract.ReadOnly(request.Command));
            if (BeforeGate is { } before) await before();
            if (await request.TenantDispatchCheck(ct) is { } denied) return new(denied);
            Reads++; target.CommandRevision++;
            if (AfterRead is { } after) await after();
            return new(null, new("http://127.0.0.1:5091/private", "Owned page", "Scoped private observation", false, []));
        }
    }
    private static TenantBrowserReadService ReadService(Fixture f, IBrowser browser) => new((ITenantRunStore)f.Iam, (ITenantSessionStore)f.Iam, f.Iam,
        Operations(f, f.Iam), new(f.Iam), f.RunConfiguration(), new((ITenantSessionStore)f.Iam, f.RunConfiguration(), f.Iam, new(f.Iam), browser), browser, f.Clock);
    private static TenantRuntimeFrame Frame(Setup s) => new(s.Run.Owner, s.Run.SessionId, s.Run.AgentRunId, s.Run.Claim!.Generation);
    private static ModelToolCall ReadCall(string id = "read") => new(id, ToolCatalog.BrowserSnapshot, "{}");
    private static async Task RequireReadReview(Fixture f, Setup s, string permission) => await new TenantOperationPolicyService(f.Iam, (ITenantOperationPolicyStore)f.Iam,
        new(f.Iam), f.Identity, f.Ids, f.Clock).SaveAsync(s.Tenant, s.Human, permission, new(OperationApprovalMode.Always, false), (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision, 0);

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_Browser_Allowed_read_requires_no_use_grant_or_durable_effect_and_may_read_again_under_current_policy(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true); var s = await Prepare(f, permissions: false); var probe = new ReadProbe(Probe(s)); var reads = ReadService(f, probe);
        var first = await reads.InspectAsync(Frame(s), ReadCall(), 32768); var second = await reads.InspectAsync(Frame(s), ReadCall("again"), 32768);
        Assert.Contains("Scoped private observation", first); Assert.Equal(2, probe.Reads); Assert.Equal(2, probe.Executions); Assert.Equal(2, probe.Bindings);
        using var result = JsonDocument.Parse(second); Assert.True(result.RootElement.GetProperty("readAuthorized").GetBoolean()); Assert.False(result.RootElement.GetProperty("effectAttempted").GetBoolean());
        var current = (await ((ITenantRunStore)f.Iam).FindTenantRunAsync(s.Tenant, s.Run.AgentRunId))!;
        Assert.Equal(s.Run.Revision, current.Revision); Assert.Equal(AgentRunSideEffectDisposition.None, current.SideEffect.Disposition); Assert.Null(current.ApprovalRequestId);
        Assert.DoesNotContain(await f.Iam.ReadAuditAsync(s.Tenant, 200), a => a.Action.StartsWith("operations.browser", StringComparison.Ordinal));
    }

    [Theory] [InlineData(false, "browser.read")] [InlineData(true, "browser.read")] [InlineData(false, "sessions.read")] [InlineData(true, "sessions.read")]
    public async Task Tenant_Browser_direct_read_Review_policy_blocks_before_native_target_or_binding_and_uses_existing_consent(bool sqlite, string permission)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true); var s = await Prepare(f, permissions: false); var target = Probe(s); var probe = new ReadProbe(target);
        await RequireReadReview(f, s, permission);
        var error = await Assert.ThrowsAsync<AccessDeniedException>(() => ReadService(f, probe).InspectAsync(Frame(s), ReadCall(), 32768).AsTask());
        Assert.Equal("browser_read_review_required", error.ReasonCode); Assert.Equal(0, probe.Executions); Assert.Equal(0, probe.Bindings); Assert.Equal(0, target.Reads);
        var request = (await Request(Service(f, probe), s, ToolCatalog.BrowserSnapshot, "{}")).Request!;
        Assert.Equal(ApprovalKind.Review, request.Kind); Assert.Equal("browser.read", request.Permission); Assert.True(request.Operation.BrowserCommand!.ReadOnly);
        Assert.Null(await ((ITenantApprovalStore)f.Iam).FindOperationGrantAsync(s.Tenant, request.RequestId)); Assert.Equal(0, probe.Reads);
    }

    [Theory] [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)] [InlineData(true, true)]
    public async Task Tenant_Browser_read_rechecks_role_revocation_at_native_gate_and_after_IO_without_disclosing_output(bool sqlite, bool after)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true); var s = await Prepare(f, permissions: false); var probe = new ReadProbe(Probe(s));
        async Task Revoke() => await f.Access.RevokeAssignmentAsync(s.Human.PrincipalId, s.Tenant, s.Use.AssignmentId, (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision);
        if (after) probe.AfterRead = Revoke; else probe.BeforeGate = Revoke;
        var denied = await Assert.ThrowsAsync<AccessDeniedException>(() => ReadService(f, probe).InspectAsync(Frame(s), ReadCall(), 32768).AsTask());
        Assert.Equal("permission_denied", denied.ReasonCode); Assert.Equal(after ? 1 : 0, probe.Reads);
        Assert.Contains(await f.Iam.ReadAuditAsync(s.Tenant, 200), a => a.Execution?.Permission == "browser.read" && a.Decision == "denied");
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_Browser_read_rejects_mutation_identity_and_unconverted_arguments_before_provider_access(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true); var s = await Prepare(f, permissions: false); var probe = new ReadProbe(Probe(s));
        foreach (var (tool, json) in new[] { ("browser.tabs", """{"operation":"new","url":"http://127.0.0.1:5091"}"""), ("browser.dialog", """{"operation":"accept"}"""),
            ("browser.navigate", """{"url":"http://127.0.0.1:5091"}"""), ("browser.cookies", """{"operation":"list"}"""),
            ("browser.get_config", """{"tenantId":"foreign"}"""), ("browser.tabs", """{"operation":"list","tabRef":"foreign"}""") })
        {
            var denied = await Record.ExceptionAsync(() => ReadService(f, probe).InspectAsync(Frame(s), new("bad", tool, json), 32768).AsTask());
            Assert.True(denied is ArgumentException or AccessDeniedException);
        }
        Assert.Equal(0, probe.Bindings); Assert.Equal(0, probe.Executions);
    }

    [Theory] [InlineData(false, "foreign")] [InlineData(true, "foreign")] [InlineData(false, "generation")] [InlineData(true, "generation")]
    [InlineData(false, "session-read")] [InlineData(true, "session-read")]
    [InlineData(false, "tenant")] [InlineData(true, "tenant")]
    public async Task Tenant_Browser_read_requires_actual_frame_and_independent_Session_scope(bool sqlite, string boundary)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true); var s = await Prepare(f, permissions: false); var probe = new ReadProbe(Probe(s)); var frame = Frame(s);
        if (boundary == "foreign") frame = frame with { SessionId = f.Ids.NewId() };
        if (boundary == "tenant") frame = frame with { Owner = AgentRunOwner.ForTenant(f.Ids.NewId(), s.Run.AgentInstanceId, s.Agent.PrincipalId) };
        if (boundary == "generation") frame = frame with { Generation = f.Ids.NewId() };
        if (boundary == "session-read")
        {
            var state = (await f.Iam.ReadTenantAsync(s.Tenant))!; var assignment = state.Assignments.Single(a => a.PrincipalId == s.Agent.PrincipalId && a.Scope.ResourceType == "session");
            await f.Access.RevokeAssignmentAsync(s.Human.PrincipalId, s.Tenant, assignment.AssignmentId, state.Tenant.Revision);
        }
        await Assert.ThrowsAsync<AccessDeniedException>(() => ReadService(f, probe).InspectAsync(frame, ReadCall(), 32768).AsTask());
        Assert.Equal(0, probe.Bindings); Assert.Equal(0, probe.Executions);
    }

    [Theory] [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)] [InlineData(true, true)]
    public async Task Tenant_Browser_read_compacted_history_keeps_current_scope_and_policy_before_later_generation(bool sqlite, bool policy)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true); var s = await Prepare(f, permissions: false); var probe = new ReadProbe(Probe(s)); var reads = ReadService(f, probe);
        var one = ReadCall("one"); var two = ReadCall("two"); var first = await reads.InspectAsync(Frame(s), one, 32768); var second = await reads.InspectAsync(Frame(s), two, 32768);
        var messages = new List<ModelMessage> { new(ModelRole.Assistant, "", ToolCalls: [one]), new(ModelRole.Tool, first, ToolCallId: one.Id, Name: one.Name),
            new(ModelRole.Assistant, "", ToolCalls: [two]), new(ModelRole.Tool, second, ToolCallId: two.Id, Name: two.Name) };
        BrowserSnapshotCompaction.Compact(messages); Assert.Contains("contentExcerpt", messages[1].Text); Assert.Contains("tenantBrowserRead", messages[1].Text);
        var run = await ((ITenantRuntimeStore)f.Iam).ApplyTenantRuntimeCommandAsync(s.Run.Owner, s.Run.AgentRunId,
            new AgentRunCommand.Checkpoint(s.Run.Revision, f.Clock.GetUtcNow(), s.Run.Claim!.Generation,
                new(AgentRunToolCallCheckpoint.Write(messages), 2, first.Length + second.Length, 10000, 100), null), f.Ids.NewId());
        await reads.RequireStoredObservationsAsync(run);
        if (policy) await RequireReadReview(f, s, "browser.read");
        else await f.Access.RevokeAssignmentAsync(s.Human.PrincipalId, s.Tenant, s.Use.AssignmentId, (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision);
        await Assert.ThrowsAsync<AccessDeniedException>(() => reads.RequireStoredObservationsAsync(run).AsTask());
        var authority = new TenantRunExecutionAuthority(Operations(f, f.Iam), f.Iam, f.Clock, f.RunConfiguration(), browserReads: reads);
        Assert.Null(await authority.CurrentDefinitionAsync(run)); Assert.Equal(2, probe.Reads); Assert.Equal(2, probe.Executions);
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_Browser_read_cancellation_discards_a_late_noncooperative_observation_without_effect_receipt(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true); var s = await Prepare(f, permissions: false); var probe = new ReadProbe(Probe(s));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var proceed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var returned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        probe.AfterRead = async () => { entered.TrySetResult(); await proceed.Task; returned.TrySetResult(); };
        using var cancel = new CancellationTokenSource(); var pending = ReadService(f, probe).InspectAsync(Frame(s), ReadCall(), 32768, cancel.Token).AsTask();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10)); cancel.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        proceed.TrySetResult(); await returned.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var run = (await ((ITenantRunStore)f.Iam).FindTenantRunAsync(s.Tenant, s.Run.AgentRunId))!;
        Assert.Equal(AgentRunSideEffectDisposition.None, run.SideEffect.Disposition); Assert.Null(run.ApprovalRequestId); Assert.Equal(1, probe.Reads);
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_Browser_read_Run_cancellation_after_IO_withholds_the_observation(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true); var s = await Prepare(f, permissions: false); var probe = new ReadProbe(Probe(s));
        probe.AfterRead = async () => await ((ITenantRuntimeStore)f.Iam).ApplyTenantRuntimeCommandAsync(s.Run.Owner, s.Run.AgentRunId,
            new AgentRunCommand.RequestCancellation(s.Run.Revision, f.Clock.GetUtcNow(), null), f.Ids.NewId());
        var denied = await Assert.ThrowsAsync<AccessDeniedException>(() => ReadService(f, probe).InspectAsync(Frame(s), ReadCall(), 32768).AsTask());
        Assert.Equal("tenant_runtime_unavailable", denied.ReasonCode); Assert.Equal(1, probe.Reads);
        var run = (await ((ITenantRunStore)f.Iam).FindTenantRunAsync(s.Tenant, s.Run.AgentRunId))!;
        Assert.True(run.CancellationRequested); Assert.Equal(AgentRunSideEffectDisposition.None, run.SideEffect.Disposition);
    }

    [Fact]
    public async Task Tenant_Browser_read_small_output_budget_keeps_valid_JSON_without_leaking_a_partial_observation()
    {
        await using var f = await Fixture.Create(false, builtInAuthority: true); var s = await Prepare(f, permissions: false); var probe = new ReadProbe(Probe(s));
        var text = await ReadService(f, probe).InspectAsync(Frame(s), ReadCall(), 64);
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(text) <= 64); Assert.DoesNotContain("Scoped private observation", text);
        using var result = JsonDocument.Parse(text); Assert.Equal("output_limit", result.RootElement.GetProperty("error").GetString()); Assert.Equal(1, probe.Reads);
    }

}
