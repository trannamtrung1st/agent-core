using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Tools;
using AgentCore.Domain.Security;
using AgentCore.Domain.Conversation;
using AgentCore.Infrastructure.Workspaces;
using Microsoft.Extensions.Time.Testing;
using Fixture = AgentCore.Infrastructure.Tests.TenantCredentialIntegrationTests.Fixture;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantOperationAuthorizationTests
{
    [Theory]
    [InlineData(false, "sessions.read", false)] [InlineData(true, "sessions.read", false)]
    [InlineData(false, "spaces.read", false)] [InlineData(true, "spaces.read", false)]
    [InlineData(false, "workspaces.read", false)] [InlineData(true, "workspaces.read", false)]
    [InlineData(false, "sessions.read", true)] [InlineData(true, "sessions.read", true)]
    [InlineData(false, "spaces.read", true)] [InlineData(true, "spaces.read", true)]
    [InlineData(false, "workspaces.read", true)] [InlineData(true, "workspaces.read", true)]
    public async Task Tenant_workspace_Artifact_commit_independently_requires_each_raw_source_read_and_its_policy(bool sqlite, string permission, bool policy)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true, artifactAuthority: true); var s = await Setup(f, "general-assistant", 35);
        var frame = await ClaimArtifactRun(f, s); await GrantPublication(f, s, ["artifacts.create"]);
        await GrantWorkspaceRuntimeReads(f, s, new[] { "sessions.read", "spaces.read", "workspaces.read" }.Where(p => policy || p != permission).ToArray());
        if (!policy && permission == "spaces.read") await RevokeExactContextRead(f, s);
        await SeedRuntimeWorkspace(f, s, false, "source.bin", [0, 255, 128, 3]);
        var source = await WorkspaceReader(f).CaptureArtifactSourceTenantAsync(WorkspaceOwner(s), "/home/source.bin");
        if (policy) await Policies(f).SaveAsync(s.Tenant, s.Owner, permission, new(OperationApprovalMode.Always, false),
            (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision, (await f.Iam.ReadTenantAsync(s.Tenant))!.OperationPolicies.SingleOrDefault(p => p.PermissionKey == permission)?.Revision ?? 0);
        var run = (await ((ITenantRunStore)f.Iam).FindTenantRunAsync(s.Tenant, frame.RunId))!;
        var args = TenantWorkspaceArtifactContract.Parse("""{"path":"/home/source.bin","displayName":"published.bin"}""");
        var commit = new TenantArtifactCommit(f.Ids.NewId(), frame.RunId, frame.Generation, run.Revision, "source-read-bypass", args.CanonicalArguments,
            args.ParametersHash, s.Agent.Instance.Revision, TenantArtifactToolContract.ActionHash(frame.SessionId, args.ParametersHash), TenantArtifactSourceRules.Witness(source.Observation), source);
        var state = (await f.Iam.ReadTenantAsync(s.Tenant))!;
        var error = await Assert.ThrowsAsync<AccessDeniedException>(() => ((ITenantArtifactOperationStore)f.Iam).TryCommitArtifactAsync(
            state with { Tenant = state.Tenant with { Revision = state.Tenant.Revision + 1 } }, state.Tenant.Revision, commit,
            new(f.Ids.NewId(), s.Tenant, s.Agent.PrincipalId, TenantArtifactOperationRules.AuditAction, frame.SessionId.ToString("D"), "allowed", "operation_completed", f.Clock.GetUtcNow())).AsTask());
        Assert.Equal(policy ? "artifact_source_review_required" : permission == "spaces.read" ? "permission_denied" : "executor_read_required", error.ReasonCode);
        Assert.Empty((await ((ITenantArtifactStore)f.Artifacts).ListTenantAsync(s.Tenant, frame.SessionId, null, null, 20)).Items);
        Assert.Null(await ((ITenantArtifactOperationStore)f.Iam).FindDirectOperationReceiptAsync(s.Tenant, frame.RunId, commit.SourceCallId));
        Assert.Equal(run.Revision, (await ((ITenantRunStore)f.Iam).FindTenantRunAsync(s.Tenant, frame.RunId))!.Revision);
    }

    [Theory] [InlineData(false, "Disabled")] [InlineData(true, "Disabled")] [InlineData(false, "ReadOnly")] [InlineData(true, "ReadOnly")]
    public async Task Tenant_workspace_Artifact_commit_applies_current_HomeAccess_after_private_capture(bool sqlite, string policy)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true, artifactAuthority: true); var s = await Setup(f, "general-assistant", 35);
        var frame = await ClaimArtifactRun(f, s); await GrantPublication(f, s); await GrantWorkspaceRuntimeReads(f, s);
        byte[] bytes = [0, 255, 128, 3]; await SeedRuntimeWorkspace(f, s, false, "source.bin", bytes);
        var source = await WorkspaceReader(f).CaptureArtifactSourceTenantAsync(WorkspaceOwner(s), "/home/source.bin");
        var state = (await f.Iam.ReadTenantAsync(s.Tenant))!;
        Assert.NotNull(await ((ITenantAgentStore)f.Iam).TryChangeTenantAgentConfigurationAsync(state with { Tenant = state.Tenant with { Revision = state.Tenant.Revision + 1 } }, state.Tenant.Revision,
            new(s.Agent.Instance.InstanceId, s.Agent.Instance.Revision, AutomationPolicyChanges: [], SetSettingsOverrides: true, SettingsOverrides: new(HomeAccess: new(policy)),
                Initiator: new(s.Owner.AccountId, s.Owner.AuthenticationSessionId, s.Owner.PrincipalId)), f.Clock.GetUtcNow(),
            new(f.Ids.NewId(), s.Tenant, s.Owner.PrincipalId, "agents.settings.update", s.Agent.Instance.InstanceId.ToString("D"), "allowed", "settings_changed", f.Clock.GetUtcNow())));
        var agent = (await ((ITenantAgentStore)f.Iam).FindTenantAgentAsync(s.Tenant, s.Agent.Instance.InstanceId))!;
        var run = (await ((ITenantRunStore)f.Iam).FindTenantRunAsync(s.Tenant, frame.RunId))!; state = (await f.Iam.ReadTenantAsync(s.Tenant))!;
        var args = TenantWorkspaceArtifactContract.Parse("""{"path":"/home/source.bin","displayName":"published.bin"}""");
        var commit = new TenantArtifactCommit(f.Ids.NewId(), frame.RunId, frame.Generation, run.Revision, "current-home", args.CanonicalArguments, args.ParametersHash,
            agent.Instance.Revision, TenantArtifactToolContract.ActionHash(frame.SessionId, args.ParametersHash), TenantArtifactSourceRules.Witness(source.Observation), source);
        async Task<TenantOperationReceipt?> Execute() => await ((ITenantArtifactOperationStore)f.Iam).TryCommitArtifactAsync(
            state with { Tenant = state.Tenant with { Revision = state.Tenant.Revision + 1 } }, state.Tenant.Revision, commit,
            new(f.Ids.NewId(), s.Tenant, s.Agent.PrincipalId, TenantArtifactOperationRules.AuditAction, frame.SessionId.ToString("D"), "allowed", "operation_completed", f.Clock.GetUtcNow()));
        if (policy == "Disabled")
        {
            Assert.Equal("home_access_disabled", (await Assert.ThrowsAsync<AccessDeniedException>(Execute)).ReasonCode);
            Assert.Empty((await ((ITenantArtifactStore)f.Artifacts).ListTenantAsync(s.Tenant, frame.SessionId, null, null, 20)).Items);
        }
        else
        {
            var receipt = await Execute(); Assert.NotNull(receipt);
            Assert.Equal(bytes, await ((ITenantArtifactStore)f.Artifacts).ReadTenantContentAsync(s.Tenant, frame.SessionId, receipt.OperationId));
        }
    }

    [Theory] [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)] [InlineData(true, true)]
    public async Task Tenant_workspace_Artifact_publication_repeats_claim_and_cancellation_after_source_materialization(bool sqlite, bool cancel)
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow.AddSeconds(1));
        await using var f = await Fixture.Create(sqlite, time, builtInAuthority: true, artifactAuthority: true); var s = await Setup(f, "general-assistant", 35);
        var frame = await ClaimArtifactRun(f, s); await GrantPublication(f, s); await GrantWorkspaceRuntimeReads(f, s);
        await SeedRuntimeWorkspace(f, s, true, "source.bin", [0, 255, 128, 3]); using var cancellation = new CancellationTokenSource();
        var probe = new ArtifactSourceProbe(WorkspaceReader(f), beforeCurrent: call => { if (call == 2) { if (cancel) cancellation.Cancel(); else time.Advance(TimeSpan.FromMinutes(6)); } return ValueTask.CompletedTask; });
        var failure = await Record.ExceptionAsync(() => SourcePublicationService(f, files: probe).ExecuteWorkspaceAsync(new(s.Tenant, frame.RunId), frame.Generation,
            "late-source-gate", """{"path":"/working/source.bin","displayName":"published.bin"}""", cancellation.Token).AsTask());
        if (cancel) Assert.IsAssignableFrom<OperationCanceledException>(failure); else Assert.True(failure is AccessDeniedException or SecurityConflictException, failure?.ToString());
        Assert.Empty((await ((ITenantArtifactStore)f.Artifacts).ListTenantAsync(s.Tenant, frame.SessionId, null, null, 20)).Items);
        Assert.Null(await ((ITenantArtifactOperationStore)f.Iam).FindDirectOperationReceiptAsync(s.Tenant, frame.RunId, "late-source-gate"));
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_workspace_Artifact_publication_checks_frozen_copy_integrity_inside_the_store(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true, artifactAuthority: true); var s = await Setup(f, "general-assistant", 35);
        var frame = await ClaimArtifactRun(f, s); await GrantPublication(f, s); await GrantWorkspaceRuntimeReads(f, s);
        await SeedRuntimeWorkspace(f, s, true, "source.bin", [0, 255, 128, 3]);
        var probe = new ArtifactSourceProbe(WorkspaceReader(f), copy: bytes => { bytes[^1] = 4; return bytes; });
        Assert.Equal("workspace_source_changed", (await Assert.ThrowsAsync<SecurityConflictException>(() => SourcePublicationService(f, files: probe)
            .ExecuteWorkspaceAsync(new(s.Tenant, frame.RunId), frame.Generation, "corrupted-copy", """{"path":"/working/source.bin","displayName":"published.bin"}""").AsTask())).ReasonCode);
        Assert.Empty((await ((ITenantArtifactStore)f.Artifacts).ListTenantAsync(s.Tenant, frame.SessionId, null, null, 20)).Items);
    }

    [Fact]
    public async Task Tenant_workspace_Artifact_SQLite_revalidates_source_after_private_byte_staging_before_metadata_commit()
    {
        await using var f = await Fixture.Create(true, builtInAuthority: true, artifactAuthority: true); var s = await Setup(f, "general-assistant", 35);
        var frame = await ClaimArtifactRun(f, s); await GrantPublication(f, s); await GrantWorkspaceRuntimeReads(f, s);
        await SeedRuntimeWorkspace(f, s, true, "source.bin", [0, 255, 128, 3]);
        var physical = Path.Combine(AgentCore.Infrastructure.Workspaces.AgentWorkspacePhysicalPaths.WorkingDirectory(Path.Combine(f.Root, "workspaces"), s.Agent.Instance.InstanceId, frame.SessionId), "source.bin");
        var probe = new ArtifactSourceProbe(WorkspaceReader(f), beforeCurrent: call => call == 3 ? new(File.WriteAllBytesAsync(physical, [0, 255, 128, 4])) : ValueTask.CompletedTask);
        Assert.Equal("workspace_source_changed", (await Assert.ThrowsAsync<SecurityConflictException>(() => SourcePublicationService(f, files: probe)
            .ExecuteWorkspaceAsync(new(s.Tenant, frame.RunId), frame.Generation, "post-stage-source", """{"path":"/working/source.bin","displayName":"published.bin"}""").AsTask())).ReasonCode);
        Assert.Empty((await ((ITenantArtifactStore)f.Artifacts).ListTenantAsync(s.Tenant, frame.SessionId, null, null, 20)).Items);
        Assert.Null(await ((ITenantArtifactOperationStore)f.Iam).FindDirectOperationReceiptAsync(s.Tenant, frame.RunId, "post-stage-source"));
        Assert.Equal(4, (await File.ReadAllBytesAsync(physical))[^1]);
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_workspace_Artifact_source_capture_withholds_publication_after_read_authority_loss_during_IO(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true, artifactAuthority: true); var s = await Setup(f, "general-assistant", 35);
        var frame = await ClaimArtifactRun(f, s); await GrantPublication(f, s); var grant = await GrantWorkspaceRuntimeReads(f, s);
        await SeedRuntimeWorkspace(f, s, true, "source.bin", [0, 255, 128, 3]);
        var probe = new ArtifactSourceProbe(WorkspaceReader(f), afterCapture: async () => await f.Access.RevokeAssignmentAsync(s.Owner.PrincipalId, s.Tenant,
            grant.AssignmentId, (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision));
        Assert.Equal("executor_read_required", (await Assert.ThrowsAsync<AccessDeniedException>(() => SourcePublicationService(f, files: probe)
            .ExecuteWorkspaceAsync(new(s.Tenant, frame.RunId), frame.Generation, "source-IO-revocation", """{"path":"/working/source.bin","displayName":"published.bin"}""").AsTask())).ReasonCode);
        Assert.Empty((await ((ITenantArtifactStore)f.Artifacts).ListTenantAsync(s.Tenant, frame.SessionId, null, null, 20)).Items);
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_workspace_Artifact_publication_copies_opaque_bytes_above_the_Office_processing_limit(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true, artifactAuthority: true); var s = await Setup(f, "general-assistant", 35);
        var frame = await ClaimArtifactRun(f, s); await GrantPublication(f, s); await GrantWorkspaceRuntimeReads(f, s);
        await SeedRuntimeWorkspace(f, s, true, "source.bin", [0]);
        var physical = Path.Combine(AgentWorkspacePhysicalPaths.WorkingDirectory(Path.Combine(f.Root, "workspaces"), s.Agent.Instance.InstanceId, frame.SessionId), "source.bin");
        using (var file = new FileStream(physical, FileMode.Open)) file.SetLength(OfficeLimits.MaxPackageBytes + 1L);
        var result = await SourcePublicationService(f).ExecuteWorkspaceAsync(new(s.Tenant, frame.RunId), frame.Generation, "opaque-source",
            """{"path":"/working/source.bin","displayName":"opaque.data","contentType":"application/x-agent-test"}""");
        Assert.NotNull(result.Receipt); var artifacts = (ITenantArtifactStore)f.Artifacts;
        var artifact = (await artifacts.FindTenantAsync(s.Tenant, frame.SessionId, result.Receipt.OperationId))!;
        Assert.Equal(OfficeLimits.MaxPackageBytes + 1L, artifact.ByteSize); Assert.Equal("application/x-agent-test", artifact.ContentType);
        var content = await artifacts.ReadTenantContentAsync(s.Tenant, frame.SessionId, artifact.ArtifactId);
        Assert.Equal(artifact.ByteSize, content.LongLength); Assert.Equal(-1, content.AsSpan().IndexOfAnyExcept((byte)0));
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_workspace_Artifact_model_route_publishes_exact_owned_binary_bytes(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true, artifactAuthority: true); var s = await Setup(f, "general-assistant", 35);
        var frame = await ClaimArtifactRun(f, s); await GrantPublication(f, s); await GrantWorkspaceRuntimeReads(f, s);
        var executor = new SessionToolExecutor(tenantAuthorization: new((ITenantRunStore)f.Iam, Operations(f), f.Clock, artifactPublications: SourcePublicationService(f)));
        var admission = new ToolExecutionAdmission(false, TriggerKind.UserTurn, AgentInstanceId: s.Agent.Instance.InstanceId, OwnedSessionId: frame.SessionId,
            SupportsTools: true, AgentRunId: frame.RunId, TenantId: s.Tenant, AgentRunGeneration: frame.Generation);
        await SeedRuntimeWorkspace(f, s, true, "source.bin", [0, 255, 128, 3]);
        var call = new ModelToolCall("converted-export", ToolCatalog.ArtifactsCreateFromWorkspace, """{"path":"/working/source.bin","displayName":"published.bin"}""");
        using var output = System.Text.Json.JsonDocument.Parse((await executor.ExecuteAsync(s.Run.Admission.Configuration!.Definition, frame.SessionId, call, 16000, admission: admission)).Text);
        Assert.True(output.RootElement.GetProperty("applied").GetBoolean());
        var artifact = Assert.Single((await ((ITenantArtifactStore)f.Artifacts).ListTenantAsync(s.Tenant, frame.SessionId, null, null, 20)).Items);
        Assert.Equal(artifact.ArtifactId, output.RootElement.GetProperty("operationId").GetGuid());
        Assert.Equal(new byte[] { 0, 255, 128, 3 }, await ((ITenantArtifactStore)f.Artifacts).ReadTenantContentAsync(s.Tenant, frame.SessionId, artifact.ArtifactId));
    }

    [Theory] [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)] [InlineData(true, true)]
    public async Task Tenant_workspace_Artifact_commit_rejects_another_Sessions_source_capsule_or_a_substituted_witness(bool sqlite, bool otherSession)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true, artifactAuthority: true); var s = await Setup(f, "general-assistant", 35);
        var frame = await ClaimArtifactRun(f, s); await GrantPublication(f, s); await GrantWorkspaceRuntimeReads(f, s);
        var session = frame.SessionId;
        if (otherSession) session = (await f.Sessions.CreateAsync(s.Tenant, s.Agent.Instance.InstanceId, s.Owner, SessionMode.Text,
            (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision, s.Agent.Instance.Revision)).Owner.SessionId;
        _ = WorkspaceReader(f); // Establish the disposable canonical layout before fixture-owned native writes.
        var native = new CanonicalWorkspaceFilesystem(AgentWorkspacePhysicalPaths.WorkingDirectory(Path.Combine(f.Root, "workspaces"), s.Agent.Instance.InstanceId, session), "/working");
        await native.WriteAsync("/working/source.bin", new byte[] { 0, 255, 128, 3 }, 4096, 8192);
        var source = await WorkspaceReader(f).CaptureArtifactSourceTenantAsync(new TenantWorkspaceReference.SessionWorking(s.Tenant, session), "/working/source.bin");
        var witness = TenantArtifactSourceRules.Witness(source.Observation); if (!otherSession) witness = witness with { Sha256Hex = new('a', 64) };
        var args = TenantWorkspaceArtifactContract.Parse("""{"path":"/working/source.bin","displayName":"published.bin"}""");
        var run = (await ((ITenantRunStore)f.Iam).FindTenantRunAsync(s.Tenant, frame.RunId))!; var state = (await f.Iam.ReadTenantAsync(s.Tenant))!;
        var commit = new TenantArtifactCommit(f.Ids.NewId(), frame.RunId, frame.Generation, run.Revision, "substituted-source", args.CanonicalArguments, args.ParametersHash,
            s.Agent.Instance.Revision, TenantArtifactToolContract.ActionHash(frame.SessionId, args.ParametersHash), witness, source);
        Assert.Equal("operation_mismatch", (await Assert.ThrowsAsync<SecurityConflictException>(() => ((ITenantArtifactOperationStore)f.Iam).TryCommitArtifactAsync(
            state with { Tenant = state.Tenant with { Revision = state.Tenant.Revision + 1 } }, state.Tenant.Revision, commit,
            new(f.Ids.NewId(), s.Tenant, s.Agent.PrincipalId, TenantArtifactOperationRules.AuditAction, frame.SessionId.ToString("D"), "allowed", "operation_completed", f.Clock.GetUtcNow())).AsTask())).ReasonCode);
        Assert.Empty((await ((ITenantArtifactStore)f.Artifacts).ListTenantAsync(s.Tenant, frame.SessionId, null, null, 20)).Items);
    }

    private sealed class ArtifactSourceProbe(ITenantWorkspaceArtifactSourceStore inner, Func<int, ValueTask>? beforeCurrent = null,
        Func<byte[], byte[]>? copy = null, Func<ValueTask>? afterCapture = null) : ITenantWorkspaceArtifactSourceStore
    {
        public async ValueTask<ITenantWorkspaceArtifactSource> CaptureArtifactSourceTenantAsync(TenantWorkspaceReference owner, string path, CancellationToken ct = default)
        {
            var source = await inner.CaptureArtifactSourceTenantAsync(owner, path, ct).ConfigureAwait(false);
            if (afterCapture is not null) await afterCapture().ConfigureAwait(false);
            return new Source(source, beforeCurrent, copy);
        }
        private sealed class Source(ITenantWorkspaceArtifactSource inner, Func<int, ValueTask>? before, Func<byte[], byte[]>? copy) : ITenantWorkspaceArtifactSource
        {
            private int calls;
            public TenantWorkspaceArtifactObservation Observation => inner.Observation;
            public byte[] CopyContent() => copy is null ? inner.CopyContent() : copy(inner.CopyContent());
            public async ValueTask RequireCurrentAsync(CancellationToken ct = default)
            { var call = Interlocked.Increment(ref calls); if (before is not null) await before(call).ConfigureAwait(false); await inner.RequireCurrentAsync(ct).ConfigureAwait(false); }
        }
    }
}
