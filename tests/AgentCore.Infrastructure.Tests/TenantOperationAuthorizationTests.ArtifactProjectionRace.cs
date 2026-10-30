using System.Text.Json;
using AgentCore.Application.Execution;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using Fixture = AgentCore.Infrastructure.Tests.TenantCredentialIntegrationTests.Fixture;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantOperationAuthorizationTests
{
    [Theory]
    [InlineData(false, "renew")] [InlineData(true, "renew")]
    [InlineData(false, "revoke")] [InlineData(true, "revoke")]
    [InlineData(false, "cancel")] [InlineData(true, "cancel")]
    [InlineData(false, "churn")] [InlineData(true, "churn")]
    public async Task Artifact_projection_rechecks_one_raced_read_without_recovering_revocation_cancellation_or_churn(bool sqlite, string boundary)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true, artifactAuthority: true);
        var s = await Setup(f, "customer-support", 3); var frame = await ClaimArtifactRun(f, s);
        var artifacts = (ITenantArtifactStore)f.Artifacts; var runtime = (ITenantRuntimeStore)f.Iam; var runs = (ITenantRunStore)f.Iam;
        var artifact = await artifacts.CreateTenantAsync(frame, f.Ids.NewId(), "retained.md", "text/plain", new byte[] { 1 });
        var role = await f.Access.SaveRoleAsync(s.Owner.PrincipalId, s.Tenant, null, "Read exact metadata",
            ["sessions.read", "artifacts.read"], (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision);
        var grant = await f.Access.AssignRoleAsync(s.Owner.PrincipalId, s.Tenant, s.Agent.PrincipalId, role.RoleId,
            AccessScope.ForTenant(s.Tenant), (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision);
        var call = new ModelToolCall("verify-retained", ToolCatalog.ArtifactsVerify, JsonSerializer.Serialize(new { artifactId = artifact.ArtifactId }));
        var metadata = await ArtifactReads(f).InspectAsync(frame, call.ArgumentsJson, 16000);
        var run = (await runs.FindTenantRunAsync(s.Tenant, frame.RunId))!;
        run = await runtime.ApplyTenantRuntimeCommandAsync(run.Owner, run.AgentRunId,
            new AgentRunCommand.Checkpoint(run.Revision, f.Clock.GetUtcNow(), frame.Generation,
                new(AgentRunToolCallCheckpoint.Write([new(ModelRole.Assistant, "", ToolCalls: [call]),
                    new(ModelRole.Tool, metadata, ToolCallId: call.Id, Name: call.Name)]), 1, metadata.Length, 10000, 100), null), f.Ids.NewId());
        var changed = false;
        var probe = new ArtifactProjectionProbe(artifacts, async read => {
            if (read == 1 || read == 3 && boundary == "churn")
            {
                var current = (await runs.FindTenantRunAsync(s.Tenant, frame.RunId))!;
                await runtime.ApplyTenantRuntimeCommandAsync(current.Owner, current.AgentRunId,
                    new AgentRunCommand.Renew(current.Revision, f.Clock.GetUtcNow(), frame.Generation,
                        current.Claim!.LeaseExpiresAtUtc.AddMinutes(1)), f.Ids.NewId());
            }
            if (read != 3 || boundary is "renew" or "churn") return;
            changed = true;
            if (boundary == "revoke")
                await f.Access.RevokeAssignmentAsync(s.Owner.PrincipalId, s.Tenant, grant.AssignmentId,
                    (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision);
            else
            {
                var current = (await runs.FindTenantRunAsync(s.Tenant, frame.RunId))!;
                await runtime.ApplyTenantRuntimeCommandAsync(current.Owner, current.AgentRunId,
                    new AgentRunCommand.RequestCancellation(current.Revision, f.Clock.GetUtcNow(), null), f.Ids.NewId());
            }
        });
        var operations = Operations(f); var reads = ArtifactReads(f, probe);
        var sources = new TenantRunSourceAuthority(
            new((ITenantRunProfileStore)f.Iam, operations),
            new((ITenantLearnedMemoryStore)f.Iam, operations, f.Ids, f.Clock),
            new((ITenantContinuityStore)f.Iam, (ITenantLearnedMemoryStore)f.Iam, operations, f.Authorizer, f.Clock),
            new((ITenantAutomationReadStore)f.Iam, operations),
            new((ITenantManagementStore)f.Iam, operations, f.Iam), artifacts: reads);
        var authority = new TenantRunExecutionAuthority(operations, f.Iam, f.Clock, f.RunConfiguration(), artifactReads: reads, sourceAuthority: sources);
        var currentPolicy = await authority.CurrentDefinitionAsync(run);
        if (boundary == "renew") Assert.NotNull(currentPolicy);
        else Assert.Null(currentPolicy);
        Assert.InRange(probe.Reads, 3, 4);
        if (boundary is "revoke" or "cancel") Assert.True(changed);
        Assert.Single(await runs.ListTenantRunIdsAsync(s.Tenant, frame.SessionId));
        var retained = (await runs.FindTenantRunAsync(s.Tenant, frame.RunId))!;
        Assert.Equal(JsonSerializer.Serialize(run.Checkpoint), JsonSerializer.Serialize(retained.Checkpoint));
        Assert.Equal(JsonSerializer.Serialize(run.Admission), JsonSerializer.Serialize(retained.Admission));
        if (boundary != "cancel") Assert.Equal(frame.Generation, retained.Claim!.Generation);
    }

    private sealed class ArtifactProjectionProbe(ITenantArtifactStore inner, Func<int, Task> afterRead) : ITenantArtifactStore
    {
        public int Reads;
        public async ValueTask<ArtifactRecord?> FindTenantAsync(Guid tenant, Guid session, Guid artifact, CancellationToken ct = default)
        { var value = await inner.FindTenantAsync(tenant, session, artifact, ct); await afterRead(++Reads); return value; }
        public ValueTask<ArtifactRecord> CreateTenantAsync(TenantRuntimeFrame frame, Guid id, string name, string type, ReadOnlyMemory<byte> bytes, CancellationToken ct = default) => throw new InvalidOperationException("Read validation cannot create files.");
        public ValueTask<ArtifactPage> ListTenantAsync(Guid tenant, Guid session, IReadOnlyList<Guid>? allowed, Guid? before, int limit, Guid? run = null, CancellationToken ct = default) => throw new InvalidOperationException("Read validation cannot list files.");
        public ValueTask<byte[]> ReadTenantContentAsync(Guid tenant, Guid session, Guid artifact, CancellationToken ct = default) => throw new InvalidOperationException("Read validation cannot read bytes.");
    }
}
