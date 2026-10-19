using System.Text.Json;
using AgentCore.Application.Execution;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Tools;
using AgentCore.Domain.Security;
using Fixture = AgentCore.Infrastructure.Tests.TenantCredentialIntegrationTests.Fixture;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantBrowserApprovalTests
{
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_Browser_capture_requires_independent_creation_before_target_or_consent_and_filters_inbox(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true, artifactAuthority: true);
        var s = await Prepare(f); var target = Probe(s); var service = Service(f, target);
        await Assert.ThrowsAsync<AccessDeniedException>(() => Request(service, s, ToolCatalog.BrowserScreenshot, "{}").AsTask());
        Assert.Equal(0, target.Reads); await GrantBrowserPublication(f, s);
        var request = (await Request(service, s, ToolCatalog.BrowserScreenshot, "{}")).Request!;
        Assert.Equal(0, (await ((ITenantApprovalStore)f.Iam).ListApprovalsAsync(s.Tenant, null, null, new(), artifactCreateSessions: [])).Total);
        Assert.Equal(1, (await ((ITenantApprovalStore)f.Iam).ListApprovalsAsync(s.Tenant, null, null, new(), artifactCreateSessions: [s.Run.SessionId])).Total);
        Assert.Contains((await service.ReadViewAsync(s.Tenant, request.RequestId, s.Human)).Review!.Changes,
            c => c.Field == "browserArguments" && c.After!.ToString()!.Contains("creation does not grant file read access"));
    }
    [Theory] [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)] [InlineData(true, true)]
    public async Task Tenant_Browser_capture_creation_revocation_preserves_distinct_native_and_publication_facts(bool sqlite, bool after)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true, artifactAuthority: true);
        var s = await Prepare(f); var assignment = await GrantBrowserPublication(f, s);
        var probe = new EffectProbe(Probe(s)) { Result = new(null, Bytes: CapturePng, ContentType: "image/png") };
        (s, var request) = await ApprovedEffect(f, s, probe, tool: ToolCatalog.BrowserScreenshot, args: "{}");
        async Task Revoke() => await f.Access.RevokeAssignmentAsync(s.Human.PrincipalId, s.Tenant, assignment.AssignmentId,
            (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision);
        if (after) probe.AfterEffect = Revoke; else probe.BeforeGate = Revoke;
        var result = await ExecuteEffect(Effects(f, probe), s, request);
        Assert.Equal(after ? TenantOperationOutcome.Completed : TenantOperationOutcome.Denied, result.Receipt.Outcome);
        Assert.Equal(after ? 1 : 0, probe.NativeEffects); Assert.Null(result.Result);
        Assert.Equal("unavailable", TenantBrowserArtifactRules.Read(result.Receipt.BrowserArtifactsJson!).Status);
        Assert.Empty((await ((ITenantArtifactStore)f.Artifacts).ListTenantAsync(s.Tenant, s.Run.SessionId, null, null, 100)).Items);
    }
    [Theory] [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)] [InlineData(true, true)]
    public async Task Tenant_Browser_download_publication_is_explicit_and_keeps_original_bytes_in_owned_storage_only(bool sqlite, bool requested)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true, artifactAuthority: true);
        var s = await Prepare(f); if (requested) await GrantBrowserPublication(f, s);
        var bytes = "Private download thử nghiệm"u8.ToArray();
        var probe = new EffectProbe(Probe(s)) { Result = new(null, Downloads: [new(null, "private-report.txt", "text/plain", bytes)],
            EffectAttempted: true, EffectConfirmedBySdk: true) };
        (s, var request) = await ApprovedEffect(f, s, probe, downloads: requested);
        var effect = await ExecuteEffect(Effects(f, probe), s, request); Assert.Equal(TenantOperationOutcome.Completed, effect.Receipt.Outcome);
        Assert.Null(effect.Result!.Downloads); Assert.Null(effect.Result.Bytes);
        var files = (await ((ITenantArtifactStore)f.Artifacts).ListTenantAsync(s.Tenant, s.Run.SessionId, null, null, 100)).Items;
        if (requested)
        {
            var file = Assert.Single(files); var publication = TenantBrowserArtifactRules.Read(effect.Receipt.BrowserArtifactsJson!);
            Assert.Equal(file.ArtifactId, Assert.Single(publication.Files).ArtifactId); Assert.Equal("download", publication.Files[0].Kind);
            Assert.Equal(bytes, await ((ITenantArtifactStore)f.Artifacts).ReadTenantContentAsync(s.Tenant, s.Run.SessionId, file.ArtifactId));
            Assert.DoesNotContain("private-report", effect.Receipt.BrowserArtifactsJson); Assert.DoesNotContain("Private download", effect.Receipt.BrowserArtifactsJson);
        }
        else { Assert.Empty(files); Assert.Null(effect.Receipt.BrowserArtifactsJson); }
        Assert.Equal(effect.Receipt, (await ExecuteEffect(Effects(f, probe), s, request)).Receipt); Assert.Equal(1, probe.NativeEffects);
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_Browser_download_budget_survives_another_approval_in_the_same_task(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true, artifactAuthority: true);
        var s = await Prepare(f); await GrantBrowserPublication(f, s);
        var probe = new EffectProbe(Probe(s)) { Result = new(null, Downloads: [new(null, "one.txt", "text/plain", [1]), new(null, "two.txt", "text/plain", [2])],
            EffectAttempted: true, EffectConfirmedBySdk: true) };
        (s, var first) = await ApprovedEffect(f, s, probe, downloads: true);
        var initial = await ExecuteEffect(Effects(f, probe), s, first); Assert.Equal(2, TenantBrowserArtifactRules.Read(initial.Receipt.BrowserArtifactsJson!).Files.Count);
        s = s with { Run = (await ((ITenantRunStore)f.Iam).FindTenantRunAsync(s.Tenant, s.Run.AgentRunId))! };
        var denied = await Assert.ThrowsAsync<AccessDeniedException>(() => Request(Service(f, probe), s,
            args: """{"url":"http://127.0.0.1:5091/browser-native.html?next=1"}""", downloads: true).AsTask());
        Assert.Equal("download_limit", denied.ReasonCode); Assert.Equal(1, probe.NativeEffects);
        Assert.Equal(1, (await ((ITenantApprovalStore)f.Iam).ListApprovalsAsync(s.Tenant, null, null, new())).Total);
        Assert.Equal(2, (await ((ITenantArtifactStore)f.Artifacts).ListTenantAsync(s.Tenant, s.Run.SessionId, null, null, 100)).Items.Count);
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_Browser_capture_model_images_require_current_independent_read_and_stay_out_of_checkpoints(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true, artifactAuthority: true);
        var s = await Prepare(f); await GrantBrowserPublication(f, s);
        var probe = new EffectProbe(Probe(s)) { Result = new(null, Bytes: CapturePng, ContentType: "image/png") };
        (s, var request) = await BrowserModelApproval(f, s, probe, capture: true);
        await BrowserContinuations(f, probe).PrepareExternalEffectAsync(s.Run);
        var saved = (await ((ITenantRunStore)f.Iam).FindTenantRunAsync(s.Tenant, s.Run.AgentRunId))!;
        var resumed = (await BrowserContinuations(f, probe).ResumeAsync(saved)).Run;
        Assert.True(AgentRunToolCallCheckpoint.TryRead(resumed.Checkpoint, out var messages));
        var noRead = await Effects(f, probe).RehydrateCapturesAsync(resumed, messages!.ToList(), true);
        Assert.DoesNotContain(noRead, m => m.Parts?.OfType<ModelImageContent>().Any() == true);
        var role = await f.Access.SaveRoleAsync(s.Human.PrincipalId, s.Tenant, null, "Read captured image", ["artifacts.read"], (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision);
        var grant = await f.Access.AssignRoleAsync(s.Human.PrincipalId, s.Tenant, s.Agent.PrincipalId, role.RoleId,
            TenantArtifactRules.Scope(s.Tenant, request.Operation.OperationId), (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision);
        var withRead = await Effects(f, probe).RehydrateCapturesAsync(resumed, messages!.ToList(), true);
        Assert.Equal(CapturePng, Assert.Single(withRead.SelectMany(m => m.Parts?.OfType<ModelImageContent>() ?? [])).Bytes.ToArray());
        Assert.DoesNotContain(Convert.ToBase64String(CapturePng), AgentRunToolCallCheckpoint.Write(withRead));
        await Effects(f, probe).RequireImageSourcesAsync(resumed, withRead);

        await f.Access.RevokeAssignmentAsync(s.Human.PrincipalId, s.Tenant, grant.AssignmentId, (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision);
        await Assert.ThrowsAsync<AccessDeniedException>(() => Effects(f, probe).RequireImageSourcesAsync(resumed, withRead).AsTask());
        var revoked = await Effects(f, probe).RehydrateCapturesAsync(resumed, withRead, true);
        Assert.DoesNotContain(revoked, m => m.Parts?.OfType<ModelImageContent>().Any() == true); Assert.Equal(1, probe.NativeEffects);
    }
}
