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
    private static TenantArtifactRuntimeReadService ArtifactReads(Fixture f, ITenantArtifactStore? store = null) => new(Operations(f),
        (ITenantRunStore)f.Iam, (ITenantSessionStore)f.Iam, store ?? (ITenantArtifactStore)f.Artifacts, f.RunConfiguration(), f.Iam,
        f.Authorizer, f.Clock, (ITenantApprovalStore)f.Iam, f.Ids, (ITenantApprovedOperationStore)f.Iam);

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Artifact_verification_tool_uses_actual_source_and_independent_executor_read_without_exposing_bytes_or_provenance(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true, artifactAuthority: true);
        var s = await Setup(f, "customer-support", 3); var frame = await ClaimArtifactRun(f, s);
        var artifact = await ((ITenantArtifactStore)f.Artifacts).CreateTenantAsync(frame, f.Ids.NewId(), "private.md", "text/plain", "Unreturned file bytes"u8.ToArray());
        var reads = ArtifactReads(f);
        var executor = new SessionToolExecutor(tenantAuthorization: new((ITenantRunStore)f.Iam, Operations(f), f.Clock, artifactReads: reads));
        var admission = new ToolExecutionAdmission(false, TriggerKind.UserTurn, AgentInstanceId: s.Agent.Instance.InstanceId,
            OwnedSessionId: frame.SessionId, SupportsTools: true, AgentRunId: frame.RunId, TenantId: s.Tenant, AgentRunGeneration: frame.Generation);
        var call = new ModelToolCall("verify-owned", ToolCatalog.ArtifactsVerify, JsonSerializer.Serialize(new { artifactId = artifact.ArtifactId }));
        async Task<JsonDocument> Execute(ModelToolCall? changed = null, ToolExecutionAdmission? actual = null) => JsonDocument.Parse(
            (await executor.ExecuteAsync(s.Run.Admission.Configuration!.Definition, frame.SessionId, changed ?? call, 16000, admission: actual ?? admission)).Text);
        using (var denied = await Execute()) Assert.Equal("executor_read_required", denied.RootElement.GetProperty("reasonCode").GetString());
        var role = await f.Access.SaveRoleAsync(s.Owner.PrincipalId, s.Tenant, null, "Read immutable files", ["sessions.read", "artifacts.read"], (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision);
        var assignment = await f.Access.AssignRoleAsync(s.Owner.PrincipalId, s.Tenant, s.Agent.PrincipalId, role.RoleId, AccessScope.ForTenant(s.Tenant), (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision);
        using (var metadata = await Execute())
        {
            Assert.Equal(artifact.ArtifactId, metadata.RootElement.GetProperty("artifactId").GetGuid());
            Assert.Equal(artifact.Sha256Hex, metadata.RootElement.GetProperty("sha256Hex").GetString());
            Assert.Equal(artifact.ContentType, metadata.RootElement.GetProperty("contentType").GetString());
            Assert.Equal(artifact.CreatedAt, metadata.RootElement.GetProperty("createdAt").GetDateTimeOffset());
            Assert.True(metadata.RootElement.GetProperty("tenantArtifactRead").GetBoolean());
            Assert.DoesNotContain("Unreturned file bytes", metadata.RootElement.GetRawText());
            foreach (var hidden in new[] { "sourceAttachmentId", "workspaceLogicalPath", "blobKey", "content", "bytes" }) Assert.False(metadata.RootElement.TryGetProperty(hidden, out _));
        }
        using (var invalid = await Execute(call with { ArgumentsJson = "{\"artifactId\":\"" + artifact.ArtifactId + "\",\"artifactId\":\"" + artifact.ArtifactId + "\"}" }))
            Assert.Equal("invalid", invalid.RootElement.GetProperty("error").GetString());
        using (var unknown = await Execute(call with { ArgumentsJson = JsonSerializer.Serialize(new { artifactId = f.Ids.NewId() }) }))
            Assert.Equal("resource_unavailable", unknown.RootElement.GetProperty("reasonCode").GetString());
        var otherSession = await f.Sessions.CreateAsync(s.Tenant, s.Agent.Instance.InstanceId, s.Owner, SessionMode.Text, (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision, 1);
        var submitted = await f.RunService().SubmitTextAsync(s.Tenant, otherSession.Owner.SessionId, s.Owner, f.Ids.NewId(), "Other file owner", (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision, 1);
        var otherRun = (await ((ITenantRunStore)f.Iam).FindTenantRunAsync(s.Tenant, submitted.RunId))!;
        var otherFrame = await ClaimArtifactRun(f, s with { Run = otherRun });
        var foreign = await ((ITenantArtifactStore)f.Artifacts).CreateTenantAsync(otherFrame, f.Ids.NewId(), "other.md", "text/plain", new byte[] { 2 });
        using (var other = await Execute(call with { ArgumentsJson = JsonSerializer.Serialize(new { artifactId = foreign.ArtifactId }) }))
            Assert.Equal("resource_unavailable", other.RootElement.GetProperty("reasonCode").GetString());
        using (var wrongTenant = await Execute(actual: admission with { TenantId = f.Ids.NewId() })) Assert.True(wrongTenant.RootElement.TryGetProperty("error", out _));
        using (var wrongClaim = await Execute(actual: admission with { AgentRunGeneration = f.Ids.NewId() })) Assert.True(wrongClaim.RootElement.TryGetProperty("error", out _));
        Assert.Equal("{\"error\":\"output_limit\"}", await reads.InspectAsync(frame, call.ArgumentsJson, 16));
        await f.Access.RevokeAssignmentAsync(s.Owner.PrincipalId, s.Tenant, assignment.AssignmentId, (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision);
        using (var revoked = await Execute()) Assert.Equal("executor_read_required", revoked.RootElement.GetProperty("reasonCode").GetString());
    }

    [Theory] [InlineData(false, "sessions.read")] [InlineData(true, "sessions.read")]
    [InlineData(false, "artifacts.read")] [InlineData(true, "artifacts.read")]
    public async Task Artifact_verification_retained_metadata_requires_current_compound_read_policy(bool sqlite, string permission)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true, artifactAuthority: true);
        var s = await Setup(f, "customer-support", 3); var frame = await ClaimArtifactRun(f, s);
        var artifact = await ((ITenantArtifactStore)f.Artifacts).CreateTenantAsync(frame, f.Ids.NewId(), "private.md", "text/plain", new byte[] { 1 });
        var role = await f.Access.SaveRoleAsync(s.Owner.PrincipalId, s.Tenant, null, "Read exact metadata", ["sessions.read", "artifacts.read"], (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision);
        await f.Access.AssignRoleAsync(s.Owner.PrincipalId, s.Tenant, s.Agent.PrincipalId, role.RoleId, AccessScope.ForTenant(s.Tenant), (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision);
        var reads = ArtifactReads(f); var call = new ModelToolCall("verify-owned", ToolCatalog.ArtifactsVerify, JsonSerializer.Serialize(new { artifactId = artifact.ArtifactId }));
        var text = await reads.InspectAsync(frame, call.ArgumentsJson, 16000);
        var run = (await ((ITenantRunStore)f.Iam).FindTenantRunAsync(s.Tenant, frame.RunId))!;
        var messages = new ModelMessage[] { new(ModelRole.Assistant, "", ToolCalls: [call]), new(ModelRole.Tool, text, ToolCallId: call.Id, Name: call.Name) };
        run = await ((ITenantRuntimeStore)f.Iam).ApplyTenantRuntimeCommandAsync(run.Owner, run.AgentRunId,
            new AgentRunCommand.Checkpoint(run.Revision, f.Clock.GetUtcNow(), frame.Generation, new(AgentRunToolCallCheckpoint.Write(messages), 1, text.Length, 10000, 100), null), f.Ids.NewId());
        await reads.RequireStoredAsync(run);
        await Policies(f).SaveAsync(s.Tenant, s.Owner, permission, new(OperationApprovalMode.Always, false), (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision, 0);
        Assert.Equal("artifact_verification_review_required", (await Assert.ThrowsAsync<AccessDeniedException>(() => reads.RequireStoredAsync(run).AsTask())).ReasonCode);
        Assert.Null(await new TenantRunExecutionAuthority(Operations(f), f.Iam, f.Clock, f.RunConfiguration(), artifactReads: reads).CurrentDefinitionAsync(run));
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Artifact_verification_withholds_metadata_when_read_authority_changes_during_source_IO(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true, artifactAuthority: true);
        var s = await Setup(f, "customer-support", 3); var frame = await ClaimArtifactRun(f, s);
        var item = await ((ITenantArtifactStore)f.Artifacts).CreateTenantAsync(frame, f.Ids.NewId(), "raced.md", "text/plain", new byte[] { 1 });
        var role = await f.Access.SaveRoleAsync(s.Owner.PrincipalId, s.Tenant, null, "Current file reader", ["sessions.read", "artifacts.read"], (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision);
        var grant = await f.Access.AssignRoleAsync(s.Owner.PrincipalId, s.Tenant, s.Agent.PrincipalId, role.RoleId, AccessScope.ForTenant(s.Tenant), (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision);
        var probe = new ArtifactMetadataProbe((ITenantArtifactStore)f.Artifacts, async () => await f.Access.RevokeAssignmentAsync(s.Owner.PrincipalId, s.Tenant, grant.AssignmentId, (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision));
        var denied = await Assert.ThrowsAsync<SecurityConflictException>(() => ArtifactReads(f, probe).InspectAsync(frame, JsonSerializer.Serialize(new { artifactId = item.ArtifactId }), 16000).AsTask());
        Assert.Equal("stale_revision", denied.ReasonCode); Assert.True(probe.Reads >= 1);
    }
    private sealed class ArtifactMetadataProbe(ITenantArtifactStore inner, Func<Task> afterFirstRead) : ITenantArtifactStore
    {
        public int Reads;
        public async ValueTask<ArtifactRecord?> FindTenantAsync(Guid tenant, Guid session, Guid artifact, CancellationToken ct = default)
        { var value = await inner.FindTenantAsync(tenant, session, artifact, ct); if (++Reads == 1) await afterFirstRead(); return value; }
        public ValueTask<ArtifactRecord> CreateTenantAsync(TenantRuntimeFrame frame, Guid id, string name, string type, ReadOnlyMemory<byte> bytes, CancellationToken ct = default) => throw new InvalidOperationException("Metadata inspection cannot create files.");
        public ValueTask<ArtifactPage> ListTenantAsync(Guid tenant, Guid session, IReadOnlyList<Guid>? allowed, Guid? before, int limit, Guid? run = null, CancellationToken ct = default) => throw new InvalidOperationException("Exact verification cannot list files.");
        public ValueTask<byte[]> ReadTenantContentAsync(Guid tenant, Guid session, Guid artifact, CancellationToken ct = default) => throw new InvalidOperationException("Metadata inspection cannot read bytes.");
    }
}
