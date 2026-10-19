using System.Text.Json;
using AgentCore.Application.Execution;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Attachments;
using AgentCore.Infrastructure.Office;
using Fixture = AgentCore.Infrastructure.Tests.TenantCredentialIntegrationTests.Fixture;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantRunAdmissionTests
{
    private static TenantOperationAuthorizer AttachmentOperations(Fixture f) => new(f.Iam, (ITenantRunStore)f.Iam, (ITenantAgentStore)f.Iam, f.Authorizer,
        new((ITenantAuthorizationAuditStore)f.Iam, f.Ids, f.Clock));
    private static TenantAttachmentRuntimeContextService ContextService(Fixture f, ITenantAttachmentStore? files = null, IAttachmentProcessor? processor = null) =>
        new(AttachmentOperations(f), (ITenantSessionStore)f.Iam, files ?? (ITenantAttachmentStore)f.Attachments, f.RunConfiguration(), f.Authorizer,
            processor ?? new AttachmentProcessor(f.Attachments, new OfficeProcessor()), f.Clock, (ITenantApprovalStore)f.Iam, (ITenantApprovedOperationStore)f.Iam, f.Ids, AgentCore.Infrastructure.Providers.ModelCatalogFactory.Synthetic(), new OfficeProcessor());
    private static async Task<(Environment Setup, AgentRun Run, SessionSnapshot Snapshot, Guid Id, RoleAssignment Grant)> ContextCase(Fixture f, string[]? permissions = null, bool denyReadCapability = false)
    {
        var s = await Setup(f, denyReadCapability ? "general-assistant" : "examiner", denyReadCapability ? 32 : 1); var id = await Upload(f, s);
        var role = await f.Access.SaveRoleAsync(s.Owner.PrincipalId, s.Tenant, null, "Agent input reads", permissions ?? ["sessions.read", "attachments.read"], (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision);
        var grant = await f.Access.AssignRoleAsync(s.Owner.PrincipalId, s.Tenant, s.Agent.PrincipalId, role.RoleId, AccessScope.ForTenant(s.Tenant), (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision);
        var receipt = await f.RunService().SubmitMessageAsync(s.Tenant, s.Session, s.Owner, f.Ids.NewId(), "Read the file", [id], (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision, 1);
        var run = (await ((ITenantRunStore)f.Iam).FindTenantRunAsync(s.Tenant, receipt.RunId))!;
        run = await ((ITenantRuntimeStore)f.Iam).ApplyTenantRuntimeCommandAsync(run.Owner, run.AgentRunId,
            new AgentRunCommand.Claim(run.Revision, f.Clock.GetUtcNow(), f.Ids.NewId(), f.Clock.GetUtcNow().AddMinutes(5)), f.Ids.NewId());
        var snapshot = (await ((ITenantRuntimeStore)f.Iam).LoadTenantRuntimeAsync(new(run.Owner, s.Session, run.AgentRunId, run.Claim!.Generation)))!;
        return (s, run, snapshot, id, grant);
    }
    private sealed class ContextFileProbe(ITenantAttachmentStore inner, Func<Task>? afterRead = null) : ITenantAttachmentStore
    {
        public int Reads { get; private set; }
        public ValueTask<TenantAttachmentUpload> PrepareUploadTenantAsync(TenantSessionRoot owner, Guid id, Guid request, Guid actor, string name, string type, Stream content, CancellationToken ct = default) => inner.PrepareUploadTenantAsync(owner, id, request, actor, name, type, content, ct);
        public ValueTask<bool> OwnsTenantAsync(Guid tenant, Guid session, Guid attachment, CancellationToken ct = default) => inner.OwnsTenantAsync(tenant, session, attachment, ct);
        public ValueTask<AttachmentRecord?> FindTenantAsync(Guid tenant, Guid session, Guid attachment, CancellationToken ct = default) => inner.FindTenantAsync(tenant, session, attachment, ct);
        public ValueTask<TenantAttachmentPage> ListTenantAsync(Guid tenant, Guid session, IReadOnlyList<Guid>? allowed, Guid? before, int limit, CancellationToken ct = default, bool boundOnly = false) => inner.ListTenantAsync(tenant, session, allowed, before, limit, ct, boundOnly);
        public async ValueTask<byte[]> ReadTenantContentAsync(Guid tenant, Guid session, Guid attachment, CancellationToken ct = default)
        { Reads++; var bytes = await inner.ReadTenantContentAsync(tenant, session, attachment, ct); if (afterRead is not null) await afterRead(); return bytes; }
    }
    [Theory] [InlineData(false, "sessions.read")] [InlineData(true, "sessions.read")]
    [InlineData(false, "attachments.read")] [InlineData(true, "attachments.read")]
    public async Task Tenant_attachment_context_missing_independent_executor_read_hides_source_facts_but_preserves_submitted_selectors(bool sqlite, string missing)
    {
        await using var f = await Fixture.Create(sqlite, attachmentAuthority: true); var v = await ContextCase(f, new[] { "sessions.read", "attachments.read" }.Where(p => p != missing).ToArray());
        var probe = new ContextFileProbe((ITenantAttachmentStore)f.Attachments); var result = await ContextService(f, probe).PrepareAsync(v.Run, v.Snapshot, true);
        Assert.Equal(0, probe.Reads); Assert.Empty(result.Manifest); Assert.Empty(result.Contents); Assert.Empty(result.Sources);
        var json = JsonSerializer.Serialize(result.History); Assert.DoesNotContain("Private source.txt", json); Assert.Contains(v.Id.ToString("D"), json); Assert.Contains("User-provided attachment references", json);
        Assert.Contains("unavailable under current access", json);
        Assert.NotNull(Assert.Single(v.Snapshot.Entries).Attachments); // Prompt projection must never rewrite durable bindings.
    }
    [Theory] [InlineData(false, "sessions.read")] [InlineData(true, "sessions.read")]
    [InlineData(false, "attachments.read")] [InlineData(true, "attachments.read")]
    public async Task Tenant_attachment_context_policy_required_review_reads_no_bytes_or_source_labels(bool sqlite, string permission)
    {
        await using var f = await Fixture.Create(sqlite, attachmentAuthority: true); var v = await ContextCase(f);
        var policies = new TenantOperationPolicyService(f.Iam, (ITenantOperationPolicyStore)f.Iam, f.Authorizer, f.Identity, f.Ids, f.Clock);
        await policies.SaveAsync(v.Setup.Tenant, v.Setup.Owner, permission, new(OperationApprovalMode.Always, false), (await f.Iam.ReadTenantAsync(v.Setup.Tenant))!.Tenant.Revision, 0);
        var probe = new ContextFileProbe((ITenantAttachmentStore)f.Attachments); var result = await ContextService(f, probe).PrepareAsync(v.Run, v.Snapshot, true);
        Assert.Equal(0, probe.Reads); Assert.Empty(result.Sources); Assert.Empty(result.Contents); Assert.Empty(result.Manifest);
        Assert.DoesNotContain("Private source.txt", JsonSerializer.Serialize(result.History));
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_attachment_context_source_and_authority_fences_withhold_processed_results_after_read_revocation(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite, attachmentAuthority: true); var v = await ContextCase(f);
        var probe = new ContextFileProbe((ITenantAttachmentStore)f.Attachments, async () =>
            await f.Access.RevokeAssignmentAsync(v.Setup.Owner.PrincipalId, v.Setup.Tenant, v.Grant.AssignmentId, (await f.Iam.ReadTenantAsync(v.Setup.Tenant))!.Tenant.Revision));
        await Assert.ThrowsAsync<SecurityConflictException>(() => ContextService(f, probe).PrepareAsync(v.Run, v.Snapshot, true).AsTask()); Assert.Equal(1, probe.Reads);
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_attachment_context_checkpoint_sources_are_provenance_and_require_current_reads_and_exact_bytes(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite, attachmentAuthority: true); var v = await ContextCase(f); var service = ContextService(f);
        var result = await service.PrepareAsync(v.Run, v.Snapshot, true); Assert.Equal("Private attachment body", Assert.Single(result.Contents).Text);
        var witness = Assert.Single(result.Sources); Assert.NotNull(witness.ProcessorVersion); await service.RequireSourcesAsync(v.Run, result.Sources);
        await Assert.ThrowsAsync<AccessDeniedException>(() => service.RequireSourcesAsync(v.Run, [witness with { Sha256Hex = new string('a', 64) }]).AsTask());
        await Assert.ThrowsAsync<AccessDeniedException>(() => service.RequireSourcesAsync(v.Run, [witness, witness]).AsTask());
        await Assert.ThrowsAsync<AccessDeniedException>(() => service.RequireSourcesAsync(v.Run, [null!]).AsTask());
        await Assert.ThrowsAsync<AccessDeniedException>(() => service.RequireSourcesAsync(v.Run, [witness with { ContentType = null! }]).AsTask());
        var payload = AgentRunToolCallCheckpoint.Write([], skillState: new(v.Run.ActiveSkillKeys, v.Run.SkillLoadCount),
            executionState: new(TranscriptId: v.Run.ResponseId, TenantAttachmentSources: result.Sources));
        var stored = await ((ITenantRuntimeStore)f.Iam).ApplyTenantRuntimeCommandAsync(v.Run.Owner, v.Run.AgentRunId,
            new AgentRunCommand.Checkpoint(v.Run.Revision, f.Clock.GetUtcNow(), v.Run.Claim!.Generation, new(payload, 0, 0, 60000), null), f.Ids.NewId());
        Assert.Equal(witness, Assert.Single(AgentRunToolCallCheckpoint.ReadExecutionState(stored.Checkpoint).TenantAttachmentSources!));
        var authority = new TenantRunExecutionAuthority(AttachmentOperations(f), f.Iam, f.Clock, f.RunConfiguration(), attachmentContext: service);
        Assert.NotNull(await authority.CurrentDefinitionAsync(stored));
        if (sqlite)
        {
            var physical = Path.Combine(f.Root, "attachments", "tenants", v.Setup.Tenant.ToString("N"), v.Setup.Session.ToString("N"), v.Id.ToString("N"));
            await File.WriteAllTextAsync(physical, "Changed bytes"); Assert.Null(await authority.CurrentDefinitionAsync(stored));
            await File.WriteAllTextAsync(physical, "Private attachment body"); Assert.NotNull(await authority.CurrentDefinitionAsync(stored));
        }
        await f.Access.RevokeAssignmentAsync(v.Setup.Owner.PrincipalId, v.Setup.Tenant, v.Grant.AssignmentId, (await f.Iam.ReadTenantAsync(v.Setup.Tenant))!.Tenant.Revision);
        await Assert.ThrowsAsync<AccessDeniedException>(() => service.RequireStoredAsync(stored).AsTask()); Assert.Null(await authority.CurrentDefinitionAsync(stored));
    }
    [Fact]
    public async Task Tenant_attachment_context_checks_native_source_again_after_processing_before_returning_a_projection()
    {
        await using var f = await Fixture.Create(true, attachmentAuthority: true); var v = await ContextCase(f);
        var physical = Path.Combine(f.Root, "attachments", "tenants", v.Setup.Tenant.ToString("N"), v.Setup.Session.ToString("N"), v.Id.ToString("N"));
        var probe = new ContextFileProbe((ITenantAttachmentStore)f.Attachments, () => File.WriteAllTextAsync(physical, "Changed while processing"));
        await Assert.ThrowsAsync<AccessDeniedException>(() => ContextService(f, probe).PrepareAsync(v.Run, v.Snapshot, true).AsTask());
        Assert.Equal(2, probe.Reads);
    }

}
