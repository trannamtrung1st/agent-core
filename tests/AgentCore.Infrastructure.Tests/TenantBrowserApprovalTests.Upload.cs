using System.Text;
using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Tools;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Security;
using Fixture = AgentCore.Infrastructure.Tests.TenantCredentialIntegrationTests.Fixture;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantBrowserApprovalTests
{
    private static string UploadArguments(Guid artifact) => JsonSerializer.Serialize(new { target = new { by = "label", value = "Product image" }, artifactIds = new[] { artifact.ToString("D") } });
    private static async Task<(ArtifactRecord File, RoleAssignment Read)> UploadSource(Fixture f, Setup s, bool permit = true)
    {
        var file = await ((ITenantArtifactStore)f.Artifacts).CreateTenantAsync(new(s.Run.Owner, s.Run.SessionId, s.Run.AgentRunId, s.Run.Claim!.Generation),
            f.Ids.NewId(), "exact-upload.txt", "text/plain", "Private original thử nghiệm"u8.ToArray());
        var role = await f.Access.SaveRoleAsync(s.Human.PrincipalId, s.Tenant, null, "Read upload source", permit ? ["artifacts.read"] : ["agents.read"],
            (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision);
        var read = await f.Access.AssignRoleAsync(s.Human.PrincipalId, s.Tenant, s.Agent.PrincipalId, role.RoleId,
            TenantArtifactRules.Scope(s.Tenant, file.ArtifactId), (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision);
        return (file, read);
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_Browser_upload_reviews_actual_file_facts_then_dispatches_once_and_reopens_without_bytes(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true, artifactAuthority: true);
        var s = await Prepare(f); var (file, _) = await UploadSource(f, s); var probe = new EffectProbe(Probe(s));
        var service = Service(f, probe); var pending = await Request(service, s, ToolCatalog.BrowserUpload, UploadArguments(file.ArtifactId));
        var request = Assert.IsType<TenantApprovalRequest>(pending.Request); var value = request.Operation.BrowserCommand!;
        Assert.Equal(TenantBrowserUploadRules.Source(file), Assert.Single(TenantBrowserUploadRules.Sources(value)));
        Assert.Equal("http://127.0.0.1:5091", value.FileInputOrigin); Assert.Equal("browser.use", request.Permission);
        Assert.DoesNotContain("Private original", JsonSerializer.Serialize(request));
        var view = await service.ReadViewAsync(s.Tenant, request.RequestId, s.Human);
        Assert.Contains(view.Review!.Changes, c => c.Field == "browserArguments" && c.After!.ToString()!.Contains(file.DisplayName)
            && c.After.ToString()!.Contains(file.Sha256Hex) && c.After.ToString()!.Contains("server submission is not confirmed"));
        Assert.Equal(0, (await ((ITenantApprovalStore)f.Iam).ListApprovalsAsync(s.Tenant, null, null, new(), artifactReadVisibility: new([], null))).Total);
        Assert.Equal(1, (await ((ITenantApprovalStore)f.Iam).ListApprovalsAsync(s.Tenant, null, null, new(), artifactReadVisibility: new([file.ArtifactId], null))).Total);
        request = await service.DecideAsync(s.Tenant, request.RequestId, s.Human, true, request.Operation.ActionHash, view.SecurityRevision, request.Revision);
        await service.DispatchPendingAsync(); s = s with { Run = await ClaimResumed(f, s) };
        var effect = await ExecuteEffect(Effects(f, probe), s, request);
        Assert.Equal(TenantOperationOutcome.Completed, effect.Receipt.Outcome); Assert.NotNull(effect.Result);
        Assert.Equal("Private original thử nghiệm", Encoding.UTF8.GetString(Assert.Single(probe.Uploads!).Content.Span));
        Assert.Equal(1, probe.NativeEffects); Assert.Null(await ((ITenantApprovalStore)f.Iam).FindOperationGrantAsync(s.Tenant, request.RequestId));
        IIamStore reopened = sqlite ? new SqliteIamStore(f.Contexts!, builtIns: f.BuiltIns, clock: f.Clock) : f.Iam;
        var replay = await ExecuteEffect(Effects(f, probe, reopened), s, request);
        Assert.Equal(effect.Receipt, replay.Receipt); Assert.Null(replay.Result); Assert.Equal(1, probe.NativeEffects);
        Assert.DoesNotContain("Private original", JsonSerializer.Serialize(await reopened.ReadAuditAsync(s.Tenant, 200)));
    }
    private static async Task<AgentCore.Domain.Conversation.AgentRun> ClaimResumed(Fixture f, Setup s)
    {
        var run = (await ((ITenantRunStore)f.Iam).FindTenantRunAsync(s.Tenant, s.Run.AgentRunId))!;
        return await ((ITenantRuntimeStore)f.Iam).ApplyTenantRuntimeCommandAsync(run.Owner, run.AgentRunId,
            new AgentCore.Application.Execution.AgentRunCommand.Claim(run.Revision, f.Clock.GetUtcNow(), f.Ids.NewId(), f.Clock.GetUtcNow().AddMinutes(5)), f.Ids.NewId());
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_Browser_upload_requires_raw_file_read_before_target_preparation(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true, artifactAuthority: true);
        var s = await Prepare(f); var (file, _) = await UploadSource(f, s, false); var probe = Probe(s);
        await Assert.ThrowsAsync<AccessDeniedException>(() => Request(Service(f, probe), s, ToolCatalog.BrowserUpload, UploadArguments(file.ArtifactId)).AsTask());
        Assert.Equal(0, probe.Reads);
    }

    [Theory] [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)] [InlineData(true, true)]
    public async Task Tenant_Browser_upload_rechecks_file_read_at_native_gate_and_withholds_results_after_read_loss(bool sqlite, bool after)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true, artifactAuthority: true);
        var s = await Prepare(f); var (file, read) = await UploadSource(f, s); var probe = new EffectProbe(Probe(s));
        (s, var request) = await ApprovedEffect(f, s, probe, tool: ToolCatalog.BrowserUpload, args: UploadArguments(file.ArtifactId));
        async Task Revoke() => await f.Access.RevokeAssignmentAsync(s.Human.PrincipalId, s.Tenant, read.AssignmentId, (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision);
        if (after) probe.AfterEffect = Revoke; else probe.BeforeGate = Revoke;
        var result = await ExecuteEffect(Effects(f, probe), s, request);
        Assert.Equal(after ? TenantOperationOutcome.Completed : TenantOperationOutcome.Denied, result.Receipt.Outcome);
        Assert.Equal(after ? 1 : 0, probe.NativeEffects); Assert.Null(result.Result);
        if (!after) Assert.Null(probe.Uploads);
    }
}
