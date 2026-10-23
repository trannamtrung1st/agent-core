using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Execution;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Security;
using AgentCore.Infrastructure.Providers;
using Fixture = AgentCore.Infrastructure.Tests.TenantCredentialIntegrationTests.Fixture;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantBrowserApprovalTests
{
    private static TenantRunContinuationService MetadataContinuations(Fixture f, IIamStore? store = null)
    {
        store ??= f.Iam;
        var reads = new TenantCredentialRuntimeReadService(Operations(f, store), (ITenantRunStore)store, (ITenantSessionStore)store,
            (ITenantCredentialBindingStore)store, f.RunConfiguration(), store, new(store), f.Clock, (ITenantApprovalStore)store, (ITenantApprovedOperationStore)store);
        var effect = new TenantApprovedOperationService(store, (ITenantRunStore)store, (ITenantAgentStore)store, (ITenantApprovalStore)store,
            (ITenantApprovedOperationStore)store, Operations(f, store), f.RunConfiguration(), f.Ids, f.Clock, ModelCatalogFactory.Synthetic(), SyntheticProviderAliases.Default,
            credentialReads: reads);
        return new((ITenantRunStore)store, (ITenantApprovalStore)store, effect, f.Clock, (ITenantRuntimeStore)store, f.Ids);
    }
    private static async Task<(Setup Setup, TenantApprovalRequest Request)> ApprovedMetadata(Fixture f, Setup s, string args = "{}", string choice = "approve")
    {
        var call = new ModelToolCall("read-metadata", ToolCatalog.RequestAuthorization, "{\"operation\":\"inspect_credential_metadata\",\"arguments\":" + args + "}");
        var runtime = (ITenantRuntimeStore)f.Iam;
        var checkpointed = await runtime.ApplyTenantRuntimeCommandAsync(s.Run.Owner, s.Run.AgentRunId,
            new AgentRunCommand.Checkpoint(s.Run.Revision, f.Clock.GetUtcNow(), s.Run.Claim!.Generation,
                new(AgentRunToolCallCheckpoint.Write([new(ModelRole.Assistant, "", ToolCalls: [call])]), 1, 0, 10000, 100), null), f.Ids.NewId());
        s = s with { Run = checkpointed }; var service = Service(f, Probe(s));
        var request = (await service.PrepareCredentialReadAsync(new(s.Tenant, s.Run.AgentRunId), args, s.Run.Revision, s.Run.Claim!.Generation)).Request!;
        var view = await service.ReadViewAsync(s.Tenant, request.RequestId, s.Human);
        request = choice == "cancel" ? await service.CancelAsync(s.Tenant, request.RequestId, s.Human, request.Operation.ActionHash, view.SecurityRevision, request.Revision)
            : await service.DecideAsync(s.Tenant, request.RequestId, s.Human, choice == "approve", request.Operation.ActionHash, view.SecurityRevision, request.Revision);
        await service.DispatchPendingAsync(); var source = (await ((ITenantRunStore)f.Iam).FindTenantRunAsync(s.Tenant, s.Run.AgentRunId))!;
        var next = (await ((ITenantRunStore)f.Iam).FindTenantRunAsync(s.Tenant, source.Result!.ContinuationRunId!.Value))!;
        next = await runtime.ApplyTenantRuntimeCommandAsync(next.Owner, next.AgentRunId,
            new AgentRunCommand.Claim(next.Revision, f.Clock.GetUtcNow(), f.Ids.NewId(), f.Clock.GetUtcNow().AddMinutes(5)), f.Ids.NewId());
        return (s with { Run = next }, request);
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Credential_metadata_Review_prepares_exact_read_page_and_releases_Run_without_secret_use_or_provider_work(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true); var s = await Prepare(f, password: true, credentialUseAllowed: false);
        await RequireReadReview(f, s, "credentials.read"); var target = Probe(s); var service = Service(f, target);
        var result = await service.PrepareCredentialReadAsync(new(s.Tenant, s.Run.AgentRunId), "{}", s.Run.Revision, s.Run.Claim!.Generation);
        Assert.Equal(OperationDecisionKind.RequiresApproval, result.Decision); var request = Assert.IsType<TenantApprovalRequest>(result.Request);
        Assert.Equal(ApprovalKind.Review, request.Kind); Assert.Equal("credentials.read", request.Permission); Assert.Equal("credential", request.ResourceType);
        Assert.Equal(s.Password!.Credential.Value.CredentialId.ToString("D"), request.ResourceId);
        Assert.Equal(1, request.Snapshot!.CredentialRead!.Count); Assert.Single(TenantApprovalRules.CredentialReadReferences(request.Operation.CredentialRead!));
        Assert.DoesNotContain("tenant-bound-private-password", JsonSerializer.Serialize(request)); Assert.DoesNotContain("ProtectedPayload", JsonSerializer.Serialize(request));
        var waiting = (await ((ITenantRunStore)f.Iam).FindTenantRunAsync(s.Tenant, s.Run.AgentRunId))!;
        Assert.Equal(AgentRunStatus.WaitingForApproval, waiting.Status); Assert.Null(waiting.Claim); Assert.Equal(0, target.Reads);
        var view = await service.ReadViewAsync(s.Tenant, request.RequestId, s.Human); Assert.True(view.Review!.CanDecide);
        Assert.Null(await ((ITenantApprovalStore)f.Iam).FindOperationGrantAsync(s.Tenant, request.RequestId));
        Assert.Null(await ((ITenantApprovedOperationStore)f.Iam).FindOperationReceiptAsync(s.Tenant, request.Operation.OperationId));
    }

    [Theory] [InlineData(false, "credentials.read")] [InlineData(true, "credentials.read")]
    [InlineData(false, "sessions.read")] [InlineData(true, "sessions.read")]
    public async Task Credential_metadata_Review_delivers_only_the_exact_saved_page_and_reopens_without_another_authorization(bool sqlite, string policy)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true);
        var s = await Prepare(f, password: true, credentialUseAllowed: false, passwordCount: 3); await RequireReadReview(f, s, policy);
        (s, var request) = await ApprovedMetadata(f, s, "{\"limit\":1}");
        Assert.Equal(2, request.Snapshot!.CredentialRead!.Count);
        var result = await MetadataContinuations(f).ResumeAsync(s.Run); Assert.Equal(TenantOperationOutcome.Completed, result.Outcome!.Outcome);
        Assert.True(AgentRunToolCallCheckpoint.TryRead(result.Run.Checkpoint, out var messages));
        var text = Assert.Single(messages!, m => m.Role == ModelRole.Tool).Text; using var doc = JsonDocument.Parse(text);
        var root = doc.RootElement; Assert.True(root.GetProperty("readAuthorized").GetBoolean()); Assert.False(root.GetProperty("applied").GetBoolean());
        var page = root.GetProperty("credentialMetadata"); Assert.Equal("account", Assert.Single(page.GetProperty("items").EnumerateArray()).GetProperty("reference").GetString());
        Assert.True(page.GetProperty("hasMore").GetBoolean()); Assert.Equal("account", page.GetProperty("nextCursor").GetString());
        Assert.DoesNotContain("extra-2", text); Assert.DoesNotContain("tenant-bound-private-password", text); Assert.DoesNotContain("ProtectedPayload", text);
        await CredentialReads(f).RequireStoredAsync(result.Run);
        IIamStore reopened = sqlite ? new SqliteIamStore(f.Contexts!, builtIns: f.BuiltIns, clock: f.Clock) : f.Iam;
        var again = await MetadataContinuations(f, reopened).ResumeAsync(result.Run);
        Assert.Equal(result.Run.Checkpoint!.PayloadJson, again.Run.Checkpoint!.PayloadJson); Assert.Equal(result.Run.Checkpoint.OutputBytes, again.Run.Checkpoint.OutputBytes);
        Assert.Equal(result.Run.Checkpoint.RemainingOverallBudgetMs, again.Run.Checkpoint.RemainingOverallBudgetMs); Assert.Equal(result.Run.Revision, again.Run.Revision); Assert.Equal(result.Outcome, again.Outcome);
        Assert.Null(await ((ITenantApprovalStore)reopened).FindOperationGrantAsync(s.Tenant, request.RequestId));
        Assert.Single(await reopened.ReadAuditAsync(s.Tenant, 200), a => a.Action == TenantCredentialReadOperationRules.AuditAction);
    }

    [Theory] [InlineData(false, "reject")] [InlineData(true, "reject")] [InlineData(false, "cancel")] [InlineData(true, "cancel")]
    public async Task Credential_metadata_Review_refusal_releases_the_saved_continuation_without_metadata_or_receipt(bool sqlite, string choice)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true); var s = await Prepare(f, password: true, credentialUseAllowed: false);
        await RequireReadReview(f, s, "credentials.read"); (s, var request) = await ApprovedMetadata(f, s, choice: choice);
        var result = await MetadataContinuations(f).ResumeAsync(s.Run); Assert.Equal(TenantOperationOutcome.Denied, result.Outcome!.Outcome);
        Assert.True(AgentRunToolCallCheckpoint.TryRead(result.Run.Checkpoint, out var messages));
        using var doc = JsonDocument.Parse(Assert.Single(messages!, m => m.Role == ModelRole.Tool).Text);
        Assert.False(doc.RootElement.GetProperty("readAuthorized").GetBoolean()); Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("credentialMetadata").ValueKind);
        Assert.Null(await ((ITenantApprovedOperationStore)f.Iam).FindOperationReceiptAsync(s.Tenant, request.Operation.OperationId));
        Assert.Null(await ((ITenantApprovalStore)f.Iam).FindOperationGrantAsync(s.Tenant, request.RequestId));
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Credential_metadata_Review_empty_page_uses_exact_Session_scope_without_a_fabricated_Credential(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true); var s = await Prepare(f, password: true, credentialUseAllowed: false);
        await RequireReadReview(f, s, "sessions.read"); (s, var request) = await ApprovedMetadata(f, s, "{\"cursor\":\"zzzz\",\"limit\":1}");
        Assert.Equal("sessions.read", request.Permission); Assert.Equal("session", request.ResourceType); Assert.Equal(s.Run.SessionId.ToString("D"), request.ResourceId);
        Assert.Null(request.Operation.CredentialRead!.CredentialId); Assert.Empty(TenantApprovalRules.CredentialReadReferences(request.Operation.CredentialRead));
        var result = await MetadataContinuations(f).ResumeAsync(s.Run);
        Assert.True(AgentRunToolCallCheckpoint.TryRead(result.Run.Checkpoint, out var messages));
        using var doc = JsonDocument.Parse(Assert.Single(messages!, m => m.Role == ModelRole.Tool).Text);
        Assert.Empty(doc.RootElement.GetProperty("credentialMetadata").GetProperty("items").EnumerateArray()); Assert.True(doc.RootElement.GetProperty("readAuthorized").GetBoolean());
        await CredentialReads(f).RequireStoredAsync(result.Run);
    }

    [Theory] [InlineData(false, "credential")] [InlineData(true, "credential")] [InlineData(false, "session")] [InlineData(true, "session")]
    [InlineData(false, "lookahead")] [InlineData(true, "lookahead")] [InlineData(false, "rotation")] [InlineData(true, "rotation")]
    public async Task Credential_metadata_Review_retained_page_requires_current_compound_scopes_and_exact_source(bool sqlite, string change)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true); var s = await Prepare(f, password: true, credentialUseAllowed: false, passwordCount: 3);
        await RequireReadReview(f, s, "credentials.read"); (s, var request) = await ApprovedMetadata(f, s, "{\"limit\":1}");
        var result = await MetadataContinuations(f).ResumeAsync(s.Run); await CredentialReads(f).RequireStoredAsync(result.Run);
        var state = (await f.Iam.ReadTenantAsync(s.Tenant))!;
        if (change == "rotation") await f.Service.ReplaceAsync(s.Human.PrincipalId, s.Tenant, s.Password!.Credential.Value.CredentialId,
            "new-private-password", state.Tenant.Revision, s.Password.Credential.Value.Revision);
        else
        {
            var id = change == "lookahead" ? TenantApprovalRules.CredentialReadReferences(request.Operation.CredentialRead!)[1].CredentialId.ToString("D")
                : change == "session" ? s.Run.SessionId.ToString("D") : s.Password!.Credential.Value.CredentialId.ToString("D");
            var assignment = state.Assignments.Single(a => a.PrincipalId == s.Agent.PrincipalId && a.Scope.ResourceId == id);
            await f.Access.RevokeAssignmentAsync(s.Human.PrincipalId, s.Tenant, assignment.AssignmentId, state.Tenant.Revision);
        }
        var error = await Record.ExceptionAsync(() => CredentialReads(f).RequireStoredAsync(result.Run).AsTask());
        Assert.True(error is AccessDeniedException or SecurityConflictException);
        Assert.Null(await new TenantRunExecutionAuthority(Operations(f, f.Iam), f.Iam, f.Clock, f.RunConfiguration(), credentialReads: CredentialReads(f)).CurrentDefinitionAsync(result.Run));
        Assert.Equal(TenantOperationOutcome.Completed, (await ((ITenantApprovedOperationStore)f.Iam).FindOperationReceiptAsync(s.Tenant, request.Operation.OperationId))!.Outcome);
    }
}
