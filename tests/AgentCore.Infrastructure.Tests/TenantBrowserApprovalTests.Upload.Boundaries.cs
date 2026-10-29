using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Fixture = AgentCore.Infrastructure.Tests.TenantCredentialIntegrationTests.Fixture;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantBrowserApprovalTests
{
    [Fact]
    public void Tenant_Browser_upload_model_schema_offers_only_distinct_canonical_Artifact_ids()
    {
        var schema = TenantBrowserAuthorizationContract.Schema([ToolCatalog.BrowserUpload]).ToJsonString();
        using var doc = JsonDocument.Parse(schema);
        var ids = doc.RootElement.GetProperty("properties").GetProperty("command").GetProperty("oneOf")[0]
            .GetProperty("properties").GetProperty("arguments").GetProperty("properties").GetProperty("artifactIds");
        Assert.True(ids.GetProperty("uniqueItems").GetBoolean()); Assert.Equal(36, ids.GetProperty("items").GetProperty("maxLength").GetInt32());
        Assert.DoesNotContain("uploads", schema, StringComparison.OrdinalIgnoreCase);
    }

    [Theory] [InlineData("fixture-artifact-1")] [InlineData("00000000-0000-0000-0000-000000000000")]
    [InlineData("ABCDEF00-0000-0000-0000-000000000001")]
    [InlineData("/tmp/ac-keyboard.png")] [InlineData("https://files.example/ac-keyboard.png")]
    [InlineData("definition:product-image")]
    public void Tenant_Browser_upload_rejects_noncanonical_or_fixture_sources(string id)
    {
        Assert.Throws<ArgumentException>(() => TenantBrowserUploadRules.Ids(new(new("label", "File"), [id])));
    }
    [Theory] [InlineData("[null]")] [InlineData("[]")] [InlineData("{}")]
    public void Tenant_Browser_upload_rejects_incomplete_or_malformed_saved_sources(string sources)
    {
        var session = Guid.NewGuid(); var artifact = Guid.NewGuid();
        var value = new PreparedBrowserCommand(session, ToolCatalog.BrowserUpload, UploadArguments(artifact), "", "", false, ArtifactSourcesJson: sources);
        Assert.Throws<ArgumentException>(() => TenantBrowserUploadRules.Sources(value));
        Assert.Throws<ArgumentException>(() => TenantBrowserUploadRules.Ids(new(new("label", "File"), [artifact.ToString("D"), artifact.ToString("D")])));
        Assert.Throws<ArgumentException>(() => TenantBrowserUploadRules.Ids(new(new("label", "File"), [artifact.ToString("D")], [new("raw.txt", "text/plain", new byte[] { 1 })])));
    }

    [Theory] [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)] [InlineData(true, true)]
    public async Task Tenant_Browser_upload_model_continuation_retains_only_its_own_file_read_authority(bool sqlite, bool drop)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true, artifactAuthority: true);
        var s = await Prepare(f); var (file, read) = await UploadSource(f, s); var probe = new EffectProbe(Probe(s));
        (s, var request) = await BrowserModelApproval(f, s, probe, upload: file.ArtifactId, drop: drop);
        await BrowserContinuations(f, probe).PrepareExternalEffectAsync(s.Run);
        var saved = (await ((ITenantRunStore)f.Iam).FindTenantRunAsync(s.Tenant, s.Run.AgentRunId))!;
        Assert.Contains("owned observation", saved.Checkpoint!.PayloadJson); Assert.DoesNotContain("Private original", saved.Checkpoint.PayloadJson);
        var resumed = (await BrowserContinuations(f, probe).ResumeAsync(saved)).Run;
        await Effects(f, probe).RequireStoredObservationAsync(resumed);
        await f.Access.RevokeAssignmentAsync(s.Human.PrincipalId, s.Tenant, read.AssignmentId, (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision);
        await Assert.ThrowsAsync<AccessDeniedException>(() => Effects(f, probe).RequireStoredObservationAsync(resumed).AsTask());
        Assert.Equal(1, probe.NativeEffects);
    }

    [Theory] [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)] [InlineData(true, true)]
    public async Task Tenant_Browser_upload_cannot_use_another_Sessions_actual_Artifact_even_with_tenant_read(bool sqlite, bool drop)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true, artifactAuthority: true);
        var s = await Prepare(f);
        var second = await f.Sessions.CreateAsync(s.Tenant, s.Agent.Instance.InstanceId, s.Human, SessionMode.Text, (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision, 1);
        var admission = await f.RunService().SubmitTextAsync(s.Tenant, second.Owner.SessionId, s.Human, f.Ids.NewId(), "Other file source", (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision, 1);
        var other = (await ((ITenantRunStore)f.Iam).FindTenantRunAsync(s.Tenant, admission.RunId))!;
        other = await ((ITenantRuntimeStore)f.Iam).ApplyTenantRuntimeCommandAsync(other.Owner, other.AgentRunId,
            new AgentCore.Application.Execution.AgentRunCommand.Claim(other.Revision, f.Clock.GetUtcNow(), f.Ids.NewId(), f.Clock.GetUtcNow().AddMinutes(5)), f.Ids.NewId());
        var (file, _) = await UploadSource(f, s with { Run = other });
        var probe = Probe(s);
        Assert.Equal("resource_unavailable", (await Assert.ThrowsAsync<AccessDeniedException>(() => Request(Service(f, probe), s, FileTransferTool(drop), FileTransferArguments(drop, file.ArtifactId)).AsTask())).ReasonCode);
        Assert.Equal(0, probe.Reads);
    }

    [Theory] [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)] [InlineData(true, true)]
    public async Task Tenant_Browser_upload_review_requires_independent_raw_file_read_and_source_approval_scope(bool sqlite, bool drop)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true, artifactAuthority: true);
        var s = await Prepare(f); var (file, _) = await UploadSource(f, s); var service = Service(f, Probe(s));
        async Task<long> Revision() => (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision;
        var request = Assert.IsType<TenantApprovalRequest>((await Request(service, s, FileTransferTool(drop), FileTransferArguments(drop, file.ArtifactId))).Request);
        await new TenantOperationPolicyService(f.Iam, (ITenantOperationPolicyStore)f.Iam, f.Authorizer, f.Identity, f.Ids, f.Clock)
            .SaveAsync(s.Tenant, s.Human, "artifacts.read", new(OperationApprovalMode.Always, true), await Revision(), 0);
        var account = await f.Identity.RegisterAsync("upload-reviewer@example.test", "File reviewer", "strong-reviewer-password");
        var invite = await f.Access.InviteAsync(s.Human.PrincipalId, s.Tenant, account.LoginKey,
            (await f.Iam.ReadTenantAsync(s.Tenant))!.Roles.Single(r => r.Key == "approver").RoleId, await Revision());
        var member = await f.Access.AcceptInvitationAsync(account.AccountId, s.Tenant, invite.Invitation.InvitationId, invite.Token);
        foreach (var assignment in (await f.Iam.ReadTenantAsync(s.Tenant))!.Assignments.Where(a => a.PrincipalId == member.PrincipalId))
            await f.Access.RevokeAssignmentAsync(s.Human.PrincipalId, s.Tenant, assignment.AssignmentId, await Revision());
        var login = (await f.Identity.SignInAsync(account.LoginKey, "strong-reviewer-password"))!.Session;
        var reviewer = new TenantSessionInitiator(account.AccountId, login.SessionId, member.PrincipalId);
        async Task Grant(string name, string[] permissions, AccessScope scope)
        {
            var role = await f.Access.SaveRoleAsync(s.Human.PrincipalId, s.Tenant, null, name, permissions, await Revision());
            await f.Access.AssignRoleAsync(s.Human.PrincipalId, s.Tenant, member.PrincipalId, role.RoleId, scope, await Revision());
        }
        await Grant("Inbox", ["approvals.read"], AccessScope.ForTenant(s.Tenant));
        await Grant("Session", ["sessions.read", "approvals.approve"], TenantSessionRules.Scope(s.Tenant, s.Run.SessionId));
        await Grant("Browser", ["browser.read", "browser.use", "approvals.approve"], AccessScope.ForResource(s.Tenant, "agent", s.Agent.Instance.InstanceId.ToString("D")));
        Assert.Equal(0, (await service.ListAsync(s.Tenant, reviewer, new(0, 1))).Total);
        await Assert.ThrowsAsync<AccessDeniedException>(() => service.ReadViewAsync(s.Tenant, request.RequestId, reviewer).AsTask());
        await Grant("Read actual source", ["artifacts.read"], TenantArtifactRules.Scope(s.Tenant, file.ArtifactId));
        Assert.Equal(1, (await service.ListAsync(s.Tenant, reviewer, new(0, 1))).Total);
        Assert.False((await service.ReadViewAsync(s.Tenant, request.RequestId, reviewer)).Review!.CanDecide);
        Assert.False((await service.ReadViewAsync(s.Tenant, request.RequestId, s.Human)).Review!.CanDecide);
        await Grant("Approve exact source", ["approvals.approve"], TenantArtifactRules.Scope(s.Tenant, file.ArtifactId));
        var view = await service.ReadViewAsync(s.Tenant, request.RequestId, reviewer); Assert.True(view.Review!.CanDecide); Assert.True(view.Review.IndependentReview);
        await service.DecideAsync(s.Tenant, request.RequestId, reviewer, true, request.Operation.ActionHash, view.SecurityRevision, request.Revision);
        Assert.Null(await ((ITenantApprovalStore)f.Iam).FindOperationGrantAsync(s.Tenant, request.RequestId));
    }

    [Theory] [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)] [InlineData(true, true)]
    public async Task Tenant_Browser_upload_inbox_omits_deleted_Session_sources_before_totals(bool sqlite, bool drop)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true, artifactAuthority: true);
        var s = await Prepare(f); var (file, _) = await UploadSource(f, s); var service = Service(f, Probe(s));
        var request = (await Request(service, s, FileTransferTool(drop), FileTransferArguments(drop, file.ArtifactId))).Request!;
        Assert.Equal(1, (await ((ITenantApprovalStore)f.Iam).ListApprovalsAsync(s.Tenant, null, null, new())).Total);
        await service.CancelAsync(s.Tenant, request.RequestId, s.Human, request.Operation.ActionHash,
            (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision, request.Revision);
        var run = (await ((ITenantRunStore)f.Iam).FindTenantRunAsync(s.Tenant, s.Run.AgentRunId))!;
        if (!run.IsTerminal) await ((ITenantRuntimeStore)f.Iam).ApplyTenantRuntimeCommandAsync(run.Owner, run.AgentRunId,
            new AgentCore.Application.Execution.AgentRunCommand.RequestCancellation(run.Revision, f.Clock.GetUtcNow(), null), f.Ids.NewId());
        var session = (await ((ITenantSessionStore)f.Iam).FindTenantSessionAsync(s.Tenant, s.Run.SessionId))!;
        await f.Sessions.ChangeAsync(s.Tenant, s.Human, new(s.Run.SessionId, session.Snapshot.Revision, TenantSessionAction.Delete),
            (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision);
        Assert.Null(await ((ITenantArtifactStore)f.Artifacts).FindTenantAsync(s.Tenant, s.Run.SessionId, file.ArtifactId));
        Assert.Equal(0, (await ((ITenantApprovalStore)f.Iam).ListApprovalsAsync(s.Tenant, null, null, new())).Total);
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_Browser_upload_SQLite_guards_reject_missing_and_mutated_sources_at_storage_boundary(bool drop)
    {
        await using var f = await Fixture.Create(true, builtInAuthority: true, artifactAuthority: true);
        var s = await Prepare(f); var (file, _) = await UploadSource(f, s);
        var request = (await Request(Service(f, Probe(s)), s, FileTransferTool(drop), FileTransferArguments(drop, file.ArtifactId))).Request!;
        await using var db = await f.Contexts!.CreateDbContextAsync();
        var row = TenantApprovalRecord.From(request with { RequestId = f.Ids.NewId(), Operation = request.Operation with {
            BrowserCommand = request.Operation.BrowserCommand! with { ArtifactSourcesJson = "[]" } } });
        db.Add(row); var rejected = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.Contains("Browser file transfer requires exact owned Artifact sources", rejected.InnerException!.Message);
        db.ChangeTracker.Clear();
        await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE TenantApprovalRequests SET PayloadJson = json_set(PayloadJson, '$.Operation.BrowserCommand.ArtifactSourcesJson', '[]') WHERE RequestId = {request.RequestId}"));
        Assert.Equal(request, (await ((ITenantApprovalStore)f.Iam).FindApprovalAsync(s.Tenant, request.RequestId))!);
        await HistoricalTenantDatabase.AssertApprovalDowngradeRefusedAsync(f.Contexts!,
            drop ? "20261020040000_TenantBrowserArtifactDrops" : "20261019200000_TenantBrowserUploadSources",
            drop ? "20261020000100_TenantBrowserArtifactPublication" : "20261019160245_TenantHumanMaterializedArtifact",
            "OwnedRows = 0", HistoricalTenantDatabase.ArtifactAdmissionTables);
        Assert.Equal(request, (await ((ITenantApprovalStore)f.Iam).FindApprovalAsync(s.Tenant, request.RequestId))!);
    }
}
