using AgentCore.Application.Admin;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Persistence;
using AgentCore.Infrastructure.Security;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantDefinitionIntegrationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Resource_batches_and_required_evaluation_use_owned_snapshots_and_commit_order(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite);
        var owner = await f.Identity.BootstrapAsync("owner@example.test", "Owner", "strong-owner-password", "Alpha", "alpha");
        async Task<long> Revision() => (await f.Iam.ReadTenantAsync(owner.TenantId))!.Tenant.Revision;
        var draft = await f.Service.CreateAsync(owner.OwnerPrincipalId, owner.TenantId, "Resources", 1);
        var bytes = System.Text.Encoding.UTF8.GetBytes("Durable owned bytes.");
        var bound = await f.Service.BindResourcesAsync(owner.OwnerPrincipalId, owner.TenantId, draft.DraftId, await Revision(), 1,
            [new(null, "first.txt", AgentDefinitionResourceKind.Reference, "text/plain", bytes), new(null, "second.txt", AgentDefinitionResourceKind.Reference, "text/plain", [7, 8, 9])]);
        var first = bound.Resources.Single(r => r.LogicalPath == "first.txt"); var second = bound.Resources.Single(r => r.LogicalPath == "second.txt");
        var swapped = await f.Service.BindResourcesAsync(owner.OwnerPrincipalId, owner.TenantId, draft.DraftId, await Revision(), 2,
            [new(first.ResourceId, "second.txt", first.Kind, first.MediaType, null), new(second.ResourceId, "first.txt", second.Kind, second.MediaType, null)]);
        Assert.Equal("second.txt", swapped.Resources.Single(r => r.ResourceId == first.ResourceId).LogicalPath);
        var scenario = await f.Service.SaveScenarioAsync(owner.OwnerPrincipalId, owner.TenantId, draft.DraftId, await Revision(), 3,
            new(draft.DraftId, "no-email", "No email", "Attempt external email.", DefinitionEvaluationRequirementLevel.Required, DefinitionEvaluationCheckType.ExternalActionDenied, "email.send", DateTimeOffset.MinValue));
        var pass = await f.Service.RunScenarioAsync(owner.OwnerPrincipalId, owner.TenantId, draft.DraftId, "no-email", await Revision(), scenario.Revision);
        Assert.True(pass.Passed); Assert.NotNull(pass.CommitRevision);
        var state = (await f.Iam.ReadTenantAsync(owner.TenantId))!;
        var next = state with { Tenant = state.Tenant with { Revision = state.Tenant.Revision + 1 } };
        var failed = await f.Definitions.TrySaveDefinitionEvaluationAsync(next, state.Tenant.Revision,
            pass with { Passed = false, Findings = ["Later failure at the exact same timestamp."], CommitRevision = 999_999 },
            f.Audit(owner.TenantId, owner.OwnerPrincipalId, Guid.Parse(draft.DefinitionId)));
        Assert.Equal(next.Tenant.Revision, failed!.CommitRevision);
        var snapshot = (await f.Service.ReadDraftStateAsync(owner.OwnerPrincipalId, owner.TenantId, draft.DraftId))!;
        Assert.False(Assert.Single(snapshot.Results).Passed); Assert.Equal(pass.RecordedAt.ToUnixTimeMilliseconds(), failed.RecordedAt.ToUnixTimeMilliseconds());
        await Assert.ThrowsAsync<DefinitionPublicationNotReadyException>(() => f.Service.PublishAsync(owner.OwnerPrincipalId, owner.TenantId, draft.DraftId, next.Tenant.Revision, scenario.Revision).AsTask());
        var rerun = await f.Service.RunScenarioAsync(owner.OwnerPrincipalId, owner.TenantId, draft.DraftId, "no-email", await Revision(), scenario.Revision);
        Assert.True(rerun.Passed); Assert.True(rerun.CommitRevision > failed.CommitRevision);
        var published = await f.Service.PublishAsync(owner.OwnerPrincipalId, owner.TenantId, draft.DraftId, await Revision(), scenario.Revision);
        var read = await f.Service.ReadPublicationResourceAsync(owner.OwnerPrincipalId, owner.TenantId, Guid.Parse(draft.DefinitionId), published.Version, first.ResourceId);
        Assert.Equal(bytes, read.Content);
        var fork = await f.Service.ForkAsync(owner.OwnerPrincipalId, owner.TenantId, Guid.Parse(draft.DefinitionId), 1, await Revision());
        var advisory = new DefinitionEvaluationScenarioUpsert(fork.DraftId, "advisory", "Advisory", "Attempt external email.", DefinitionEvaluationRequirementLevel.Advisory,
            DefinitionEvaluationCheckType.ExternalActionDenied, "email.send", DateTimeOffset.MinValue);
        var added = await f.Service.SaveScenarioAsync(owner.OwnerPrincipalId, owner.TenantId, fork.DraftId, await Revision(), 1, advisory);
        await f.Service.RunScenarioAsync(owner.OwnerPrincipalId, owner.TenantId, fork.DraftId, "advisory", await Revision(), added.Revision);
        var changed = await f.Service.SaveScenarioAsync(owner.OwnerPrincipalId, owner.TenantId, fork.DraftId, await Revision(), added.Revision, advisory with { Title = "Changed advisory" });
        Assert.Equal(2, changed.Scenario.ScenarioVersion);
        Assert.Single((await f.Service.ReadDraftStateAsync(owner.OwnerPrincipalId, owner.TenantId, fork.DraftId)).Results);
        var removed = await f.Service.RemoveScenarioAsync(owner.OwnerPrincipalId, owner.TenantId, fork.DraftId, "advisory", await Revision(), changed.Revision);
        var afterRemove = await f.Service.ReadDraftStateAsync(owner.OwnerPrincipalId, owner.TenantId, fork.DraftId);
        Assert.Equal(removed.Revision, afterRemove.Draft.Revision); Assert.Empty(afterRemove.Scenarios); Assert.Empty(afterRemove.Results);
        if (sqlite)
        {
            var reopened = new SqliteIamStore(f.Contexts!, f.Content);
            var bindings = await reopened.ListOwnedPublicationResourcesAsync(owner.TenantId, Guid.Parse(draft.DefinitionId), 1);
            Assert.Equal(first.ContentSha256, bindings.Single(r => r.ResourceId == first.ResourceId).ContentSha256);
            Assert.Equal(bytes, await f.Content.ReadAsync(first.ContentSha256));
        }
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 0)]
    [InlineData(false, 1)]
    [InlineData(true, 1)]
    [InlineData(false, 2)]
    [InlineData(true, 2)]
    [InlineData(false, 3)]
    [InlineData(true, 3)]
    public async Task Evaluation_completion_loses_changed_draft_suspension_cancellation_or_retirement_without_evidence(bool sqlite, int mode)
    {
        await using var f = await Fixture.Create(sqlite);
        var owner = await f.Identity.BootstrapAsync("owner@example.test", "Owner", "strong-owner-password", "Alpha", "alpha");
        var actor = owner.OwnerPrincipalId; Guid? accountId = null;
        if (mode == 1)
        {
            var account = await f.Identity.RegisterAsync("evaluator@example.test", "Evaluator", "strong-evaluator-password"); accountId = account.AccountId;
            var state = (await f.Iam.ReadTenantAsync(owner.TenantId))!;
            var invite = await f.Access.InviteAsync(actor, owner.TenantId, account.LoginKey, state.Roles.Single(r => r.Key == "contributor").RoleId, state.Tenant.Revision);
            actor = (await f.Access.AcceptInvitationAsync(account.AccountId, owner.TenantId, invite.Invitation.InvitationId, invite.Token)).PrincipalId;
        }
        async Task<long> Revision() => (await f.Iam.ReadTenantAsync(owner.TenantId))!.Tenant.Revision;
        var draft = await f.Service.CreateAsync(actor, owner.TenantId, "Race", await Revision());
        var saved = await f.Service.SaveScenarioAsync(actor, owner.TenantId, draft.DraftId, await Revision(), 1,
            new(draft.DraftId, "no-email", "No email", "Attempt external email.", DefinitionEvaluationRequirementLevel.Required, DefinitionEvaluationCheckType.ExternalActionDenied, "email.send", DateTimeOffset.MinValue));
        var paused = new PausedEvaluation(f.Evaluator); using var cancellation = new CancellationTokenSource();
        var evaluation = f.WithEvaluator(paused).RunScenarioAsync(actor, owner.TenantId, draft.DraftId, "no-email", await Revision(), saved.Revision, cancellation.Token).AsTask();
        await paused.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        if (mode == 0)
            await f.Service.BindResourcesAsync(owner.OwnerPrincipalId, owner.TenantId, draft.DraftId, await Revision(), saved.Revision,
                [new(null, "changed.txt", AgentDefinitionResourceKind.Reference, "text/plain", [1, 2, 3])]);
        else if (mode == 1)
        {
            var system = (await f.Iam.ReadSystemAsync()).Principals.Single(p => p.AccountId == owner.AccountId);
            await new AccountAdministrationService(f.Iam, f.Authorizer, f.Tokens, f.Ids, TimeProvider.System).SetStatusAsync(system.PrincipalId, accountId!.Value, IdentityStatus.Suspended);
        }
        else if (mode == 2) cancellation.Cancel();
        else await f.Service.DeleteDraftAsync(actor, owner.TenantId, draft.DraftId, await Revision(), saved.Revision);
        paused.Release.TrySetResult(true);
        if (mode is 0 or 3) await Assert.ThrowsAsync<SecurityConflictException>(() => evaluation);
        else if (mode == 1) Assert.Equal("account_unavailable", (await Assert.ThrowsAsync<AccessDeniedException>(() => evaluation)).ReasonCode);
        else await Assert.ThrowsAnyAsync<OperationCanceledException>(() => evaluation);
        if (mode == 3)
        {
            Assert.Null(await f.Definitions.ReadDefinitionDraftSnapshotAsync(owner.TenantId, draft.DraftId));
            await AssertPurged(f, draft.DefinitionId, [draft.DraftId]);
        }
        else Assert.Empty((await f.Service.ReadDraftStateAsync(owner.OwnerPrincipalId, owner.TenantId, draft.DraftId)).Results);
        Assert.DoesNotContain(await f.Iam.ReadAuditAsync(owner.TenantId, 200), a => a.Action == "agent-definitions.evaluations.run");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Resource_batch_conflicts_and_competing_writes_leave_one_atomic_manifest(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite);
        var owner = await f.Identity.BootstrapAsync("owner@example.test", "Owner", "strong-owner-password", "Alpha", "alpha");
        var draft = await f.Service.CreateAsync(owner.OwnerPrincipalId, owner.TenantId, "Atomic manifest", 1);
        var auditCount = (await f.Iam.ReadAuditAsync(owner.TenantId, 200)).Count;
        await Assert.ThrowsAsync<AgentCore.Application.Sessions.AgentCoreException>(() => f.Service.BindResourcesAsync(owner.OwnerPrincipalId, owner.TenantId, draft.DraftId, 2, 1,
            [new(null, "duplicate.txt", AgentDefinitionResourceKind.Reference, "text/plain", [1]), new(null, "duplicate.txt", AgentDefinitionResourceKind.Reference, "text/plain", [2])]).AsTask());
        await Assert.ThrowsAsync<ArgumentException>(() => f.Service.BindResourcesAsync(owner.OwnerPrincipalId, owner.TenantId, draft.DraftId, 2, 1,
            Enumerable.Range(0, 65).Select(i => new TenantDefinitionResourceUpload(null, $"resource-{i}.txt", AgentDefinitionResourceKind.Reference, "text/plain", [1])).ToArray()).AsTask());
        Assert.Empty((await f.Service.ReadDraftStateAsync(owner.OwnerPrincipalId, owner.TenantId, draft.DraftId)).Resources);
        Assert.Equal(auditCount, (await f.Iam.ReadAuditAsync(owner.TenantId, 200)).Count);
        var bytes = new byte[] { 3, 4, 5 }; var hash = DefinitionResourceContentHasher.ComputeSha256Hex(bytes); await f.Content.StoreVerifiedAsync(hash, bytes);
        var state = (await f.Iam.ReadTenantAsync(owner.TenantId))!; var next = state with { Tenant = state.Tenant with { Revision = state.Tenant.Revision + 1 } };
        var one = new AgentDefinitionDraftResource(f.Ids.NewId(), draft.DraftId, "one.txt", AgentDefinitionResourceKind.Reference, "text/plain", hash, bytes.Length, DateTimeOffset.UtcNow);
        var two = one with { ResourceId = f.Ids.NewId(), LogicalPath = "two.txt" };
        var results = await Task.WhenAll(f.Definitions.TryBindDefinitionResourcesAsync(next, 2, draft.DraftId, 1, [one], f.Audit(owner.TenantId, owner.OwnerPrincipalId, Guid.Parse(draft.DefinitionId))).AsTask(),
            f.Definitions.TryBindDefinitionResourcesAsync(next, 2, draft.DraftId, 1, [two], f.Audit(owner.TenantId, owner.OwnerPrincipalId, Guid.Parse(draft.DefinitionId))).AsTask());
        Assert.Single(results, result => result is not null);
        var saved = await f.Service.ReadDraftStateAsync(owner.OwnerPrincipalId, owner.TenantId, draft.DraftId);
        Assert.Equal(2, saved.Draft.Revision); Assert.Single(saved.Resources);
        Assert.Equal(auditCount + 1, (await f.Iam.ReadAuditAsync(owner.TenantId, 200)).Count);
    }

    [Fact]
    public async Task Concurrent_identical_file_content_puts_are_immutable_idempotent_and_leave_no_partial_files()
    {
        var root = Path.Combine(Path.GetTempPath(), "definition-content-race-" + Guid.NewGuid().ToString("N"));
        try
        {
            var content = new FileDefinitionResourceContentStore(root); var bytes = new byte[16 * 1024]; Array.Fill(bytes, (byte)42);
            var hash = DefinitionResourceContentHasher.ComputeSha256Hex(bytes);
            await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => content.StoreVerifiedAsync(hash, bytes).AsTask()));
            Assert.Equal(bytes, await content.ReadAsync(hash)); Assert.Single(Directory.GetFiles(root));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }
    private sealed class PausedEvaluation(IDefinitionDraftSyntheticBehaviorEvaluator actual) : IDefinitionDraftSyntheticBehaviorEvaluator
    {
        public TaskCompletionSource<bool> Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask<DefinitionDraftSyntheticBehaviorResult> EvaluateAsync(AgentDefinitionCandidate candidate,
            IReadOnlyList<DefinitionDraftSyntheticResourceSnapshot> resources, DefinitionEvaluationScenario scenario, CancellationToken cancellationToken = default)
        { Entered.TrySetResult(true); await Release.Task.WaitAsync(cancellationToken); return await actual.EvaluateAsync(candidate, resources, scenario, cancellationToken); }
    }
}
