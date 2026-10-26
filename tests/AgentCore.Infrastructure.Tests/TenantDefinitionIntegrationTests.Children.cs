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
