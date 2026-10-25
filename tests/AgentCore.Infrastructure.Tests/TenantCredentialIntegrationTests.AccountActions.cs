using System.Net;
using System.Text.Json;
using AgentCore.Application.Drive;
using AgentCore.Application.Execution;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Credentials;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Credentials;
using AgentCore.Infrastructure.Drive;
using AgentCore.Infrastructure.Email;
using AgentCore.Infrastructure.Identity;
using AgentCore.Infrastructure.Persistence;
using AgentCore.Infrastructure.Providers;
using AgentCore.Infrastructure.Workspaces;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantCredentialIntegrationTests
{
    private sealed record AccountActionServices(TenantApprovalService Approvals, TenantAccountEffectService Effects, TenantRunContinuationService Continuations, FileSpaceWorkspaceStore Files, TenantAccountRuntimeReadService Reads, IDriveOperationStore Ledger);
    private static AccountActionServices ActionServices(Fixture f, IHttpClientFactory http, IIamStore? reopened = null)
    {
        var store = reopened ?? f.Iam; var iam = new IamAuthorizer(store);
        var operations = new TenantOperationAuthorizer(store, (ITenantRunStore)store, (ITenantAgentStore)store, iam, new((ITenantAuthorizationAuditStore)store, f.Ids, f.Clock));
        var options = new GoogleDriveOptions { Enabled = true };
        IDriveOperationStore ledger = f.Contexts is null ? new InMemoryDriveStore() : new SqliteDriveStore(f.Contexts);
        var native = new TenantConnectedAccountReader(new(SharedOAuth(), options, new(Path.Combine(f.Root, "keys")), f.Protector, http, false),
            new EmailTransportOptions { Enabled = true, SendEnabled = true }, options, http, f.Protector, ledger, (ISyntheticDriveStore)ledger, f.Ids, f.Clock, false, NullLoggerFactory.Instance, new SystemDiagnosticIdSource());
        var reads = RuntimeAccountReads(f, http, store, ledger);
        var files = new FileSpaceWorkspaceStore(Path.Combine(f.Root, "working"), (ITenantSpaceStore)store, (ITenantAgentStore)store, (ITenantSessionStore)store, (ITenantAgentDecommissionStore)store, f.Sandboxes);
        var target = new TenantAccountActionTarget(operations, (ITenantRunStore)store, store, (ITenantSessionStore)store,
            (ITenantCredentialBindingStore)store, f.RunConfiguration(), iam, reads, native, files, files, f.EmailDrafts, f.Ids, f.Clock);
        var approvals = new TenantApprovalService(store, (ITenantApprovalStore)store, (ITenantAgentStore)store, operations, iam, f.Identity, f.Ids, f.Clock,
            f.RunConfiguration(), ModelCatalogFactory.Synthetic(), SyntheticProviderAliases.Default, (ITenantApprovedOperationStore)store, accountReads: reads, accountActions: target);
        var effects = new TenantAccountEffectService(store, (ITenantRunStore)store, (ITenantApprovalStore)store, (ITenantApprovedOperationStore)store,
            (ITenantAccountOperationStore)store, target, iam, operations, native,
            new(ledger, f.Clock), f.EmailDrafts, files, f.Ids, f.Clock);
        var local = new TenantApprovedOperationService(store, (ITenantRunStore)store, (ITenantAgentStore)store, (ITenantApprovalStore)store,
            (ITenantApprovedOperationStore)store, operations, f.RunConfiguration(), f.Ids, f.Clock, ModelCatalogFactory.Synthetic(), SyntheticProviderAliases.Default, accountReads: reads);
        return new(approvals, effects, new((ITenantRunStore)store, (ITenantApprovalStore)store, local, f.Clock,
            (ITenantRuntimeStore)store, f.Ids, accountEffects: effects), files, reads, ledger);
    }
    public static TheoryData<bool, string> AccountActionBoundaryCases
    {
        get
        {
            var cases = new TheoryData<bool, string>();
            foreach (var sqlite in new[] { false, true })
            foreach (var mode in new[] { "draft", "draft-default-policy", "draft-exception", "send", "send-unknown", "send-revoked", "folder", "folder-rejected", "folder-revoked" }) cases.Add(sqlite, mode);
            return cases;
        }
    }
    [Theory] [MemberData(nameof(AccountActionBoundaryCases))]
    public async Task Account_actions_use_owned_atomic_drafts_exact_successors_and_factual_native_receipts_without_replay(bool sqlite, string mode)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true);
        var email = !mode.StartsWith("folder", StringComparison.Ordinal);
        var s = await RuntimeCredential(f, true, email ? CredentialPurpose.EmailAccount : CredentialPurpose.GoogleDriveAccount,
            password: mode == "draft-password", contextual: mode.EndsWith("-contextual", StringComparison.Ordinal) || mode.EndsWith("-variable", StringComparison.Ordinal), variableReference: mode.EndsWith("-variable", StringComparison.Ordinal));
        var tool = mode.StartsWith("draft", StringComparison.Ordinal) ? ToolCatalog.EmailCreateDraft : email ? ToolCatalog.EmailSend : ToolCatalog.DriveCreateFolder;
        var permission = tool == ToolCatalog.EmailCreateDraft ? "email.draft" : email ? "email.send" : "drive.write";
        async Task<long> Revision() => (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision;
        var keys = new List<string> { "sessions.read" }; if (!email) keys.Add("drive.read"); if (mode != "draft-exception") keys.Add(permission);
        var role = await f.Access.SaveRoleAsync(s.Owner.PrincipalId, s.Tenant, null, "Exact account action", keys, await Revision());
        var assignment = await f.Access.AssignRoleAsync(s.Owner.PrincipalId, s.Tenant, s.Agent.PrincipalId, role.RoleId, AccessScope.ForTenant(s.Tenant), await Revision());
        if (tool != ToolCatalog.EmailSend && mode != "draft-default-policy")
            await new TenantOperationPolicyService(f.Iam, (ITenantOperationPolicyStore)f.Iam, f.Authorizer, f.Identity, f.Ids, f.Clock).SaveAsync(s.Tenant, s.Owner,
                permission, new(OperationApprovalMode.Always, false), await Revision(), 0);
        var nativeWrites = 0; var tokenPosts = 0;
        var http = new OAuthHttp(async request => {
            if (request.RequestUri!.AbsolutePath == "/token")
            {
                tokenPosts++;
                if (mode == "send-preflight-refused") return new(HttpStatusCode.BadRequest) { Content = new StringContent("{\"error\":\"invalid_grant\"}") };
                return OAuthJson(new { access_token = "private-access", refresh_token = "rotated-private-refresh", token_type = "Bearer", expires_in = 3600 });
            }
            Assert.Equal("private-access", request.Headers.Authorization!.Parameter);
            if (request.RequestUri.AbsolutePath.EndsWith("generateIds", StringComparison.Ordinal)) return OAuthJson(new { ids = new[] { "exact-generated-folder" } });
            if (request.Method == HttpMethod.Post)
            {
                nativeWrites++;
                if (mode is "send-unknown" or "folder-reconcile-unknown") throw new HttpRequestException("Acknowledgement lost after dispatch.");
                if (mode == "folder-rejected") return new(HttpStatusCode.Forbidden);
                if (mode is "folder-revoked" or "send-revoked") await f.Access.RevokeAssignmentAsync(s.Owner.PrincipalId, s.Tenant, assignment.AssignmentId, await Revision());
                return email ? OAuthJson(new { id = "sent-message" }) : OAuthJson(new { id = "exact-generated-folder" });
            }
            return OAuthJson(new { id = "exact-generated-folder", name = "Exact folder", mimeType = "application/vnd.google-apps.folder", version = "1", parents = new[] { "root" }, trashed = false });
        });
        var services = ActionServices(f, http);
        if (tool == ToolCatalog.EmailSend)
        {
            var bound = Assert.Single((await ((ITenantCredentialBindingStore)f.Iam).ReadTenantCredentialBindingsAsync(s.Tenant, s.Agent.Instance.InstanceId))!.Bindings);
            var witness = TenantCredentialOperationRules.Parameters(bound);
            await f.EmailDrafts.CreateDraftAsync(new(s.Agent.Instance.InstanceId, s.Run.SessionId,
                new("exact-draft", ["friend@example.test"], [], [], "Exact subject", "Exact body", s.CredentialId, "work-drive", "fixture@example.test", 1,
                    witness.CredentialRevision, witness.BindingId, witness.BindingRevision, witness.AuthEpoch), f.Clock.GetUtcNow()));
        }
        var contextMessages = new List<ModelMessage>();
        var accountRef="work-drive";
        if(mode.EndsWith("-variable",StringComparison.Ordinal))
        {
            var operations=new TenantOperationAuthorizer(f.Iam,(ITenantRunStore)f.Iam,(ITenantAgentStore)f.Iam,f.Authorizer,new((ITenantAuthorizationAuditStore)f.Iam,f.Ids,f.Clock));
            var contexts=new TenantContextToolService(operations,(ITenantSessionStore)f.Iam,(ITenantSpaceStore)f.Iam,(ITenantEnvironmentStore)f.Iam,(ITenantSecretStore)f.Iam,f.Iam,f.RunConfiguration(),f.Clock);
            var lookup=new ModelToolCall("resolve-context-account","variables.resolve","{\"name\":\"ACCOUNT\"}");
            var descriptor=await contexts.ExecuteAsync(new(s.Run.Owner,s.Run.SessionId,s.Run.AgentRunId,s.Run.Claim!.Generation),lookup,16000);
            using var safe=JsonDocument.Parse(descriptor);
            accountRef=safe.RootElement.GetProperty("result").GetProperty("accountRef").GetString()!;
            Assert.Equal("work-drive",accountRef);Assert.True(safe.RootElement.GetProperty("result").GetProperty("authorizationRequiredAtConsumer").GetBoolean());
            Assert.DoesNotContain("private-access",descriptor);Assert.DoesNotContain("private-refresh",descriptor);
            contextMessages.Add(new(ModelRole.Assistant,"",ToolCalls:[lookup]));contextMessages.Add(new(ModelRole.Tool,descriptor,ToolCallId:lookup.Id,Name:lookup.Name));
        }
        var json = tool == ToolCatalog.EmailCreateDraft ? """{"accountRef":"work-drive","to":["friend@example.test"],"subject":"Exact subject","body":"Exact body"}"""
            : email ? """{"accountRef":"work-drive","draftId":"exact-draft"}""" : """{"accountRef":"work-drive","authEpoch":1,"name":"Exact folder","parentId":"root"}""";
        if(mode.EndsWith("-variable",StringComparison.Ordinal))
        {
            var contextualArgs=System.Text.Json.Nodes.JsonNode.Parse(json)!.AsObject();contextualArgs["accountRef"]=accountRef;json=contextualArgs.ToJsonString();
        }
        var intent = new TenantAccountActionIntent(tool, TenantAccountActionContract.Parse(s.Run.SessionId, tool, json).Json);
        using var arguments = JsonDocument.Parse(intent.ArgumentsJson);
        var call = new ModelToolCall("exact-action", ToolCatalog.RequestAuthorization, JsonSerializer.Serialize(new { operation = TenantAccountActionContract.Operation, action = new { tool, arguments = arguments.RootElement } }));
        var runtime = (ITenantRuntimeStore)f.Iam; var runs = (ITenantRunStore)f.Iam;
        var run = await runtime.ApplyTenantRuntimeCommandAsync(s.Run.Owner, s.Run.AgentRunId,
            new AgentRunCommand.Checkpoint(s.Run.Revision, f.Clock.GetUtcNow(), s.Run.Claim!.Generation, new(AgentRunToolCallCheckpoint.Write([..contextMessages, new(ModelRole.Assistant, "", ToolCalls: [call])]), 1, 0, 120000, 100), null), f.Ids.NewId());
        var prepared = await services.Approvals.PrepareAccountActionAsync(new(s.Tenant, run.AgentRunId), intent, run.Revision, run.Claim!.Generation);
        Assert.Equal(OperationDecisionKind.RequiresApproval, prepared.Decision); var request = prepared.Request!;
        Assert.Equal(mode == "draft-exception" ? ApprovalKind.ExceptionalGrant : ApprovalKind.Review, request.Kind);
        Assert.Equal(0, nativeWrites); Assert.Equal(0, tokenPosts);
        var inbox = (ITenantApprovalStore)f.Iam;
        Assert.Equal(1, (await services.Approvals.ListAsync(s.Tenant, s.Owner, new())).Total);
        Assert.Equal(0, (await inbox.ListApprovalsAsync(s.Tenant, null, null, new(), allowedCredentials: [], credentialReadVisibility: new(null, null))).Total);
        Assert.Equal(0, (await inbox.ListApprovalsAsync(s.Tenant, null, null, new(), credentialReadVisibility: new(null, []))).Total);
        Assert.Equal(0, (await inbox.ListApprovalsAsync(s.Tenant, null, null, new(), credentialReadVisibility: new([], null))).Total);
        var noPrimary = new TenantCredentialReadApprovalVisibility(null, null, EmailDraftCredentials: [], EmailSendCredentials: [], DriveWriteCredentials: [], DriveDeleteCredentials: []);
        Assert.Equal(0, (await inbox.ListApprovalsAsync(s.Tenant, null, null, new(), credentialReadVisibility: noPrimary)).Total);
        if (sqlite)
        {
            await using var db = f.Contexts!.CreateDbContext();
            var guards = await db.Database.SqlQueryRaw<string>("SELECT name AS Value FROM sqlite_master WHERE type='trigger' AND tbl_name='TenantApprovalRequests'").ToArrayAsync();
            Assert.Contains("TR_TenantApprovals_AccountAction_Insert", guards);
            Assert.Contains("TR_TenantApprovals_AccountAction_Update", guards);
            await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE TenantApprovalRequests SET PayloadJson=json_set(PayloadJson,'$.Operation.AccountAction.ArgumentsJson','{{}}') WHERE RequestId={request.RequestId}"));
            await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE TenantApprovalRequests SET PayloadJson=json_set(PayloadJson,'$.Snapshot.Credential.DisplayName','Changed') WHERE RequestId={request.RequestId}"));
        }
        var view = await services.Approvals.ReadViewAsync(s.Tenant, request.RequestId, s.Owner);
        await services.Approvals.DecideAsync(s.Tenant, request.RequestId, s.Owner, true, request.Operation.ActionHash, view.SecurityRevision, request.Revision);
        Assert.Equal(1, await services.Approvals.DispatchPendingAsync());
        var parent = (await runs.FindTenantRunAsync(s.Tenant, run.AgentRunId))!;
        var successor = (await runs.FindTenantRunAsync(s.Tenant, parent.Result!.ContinuationRunId!.Value))!;
        var claimed = await runtime.ApplyTenantRuntimeCommandAsync(successor.Owner, successor.AgentRunId,
            new AgentRunCommand.Claim(successor.Revision, f.Clock.GetUtcNow(), f.Ids.NewId(), f.Clock.GetUtcNow().AddMinutes(5)), f.Ids.NewId());
        if (sqlite) services = ActionServices(f, http, new AgentCore.Infrastructure.Security.SqliteIamStore(f.Contexts!, builtIns: f.BuiltIns, clock: f.Clock));
        await services.Continuations.PrepareExternalEffectAsync(claimed);
        var receipt = (await ((ITenantApprovedOperationStore)f.Iam).FindOperationReceiptAsync(s.Tenant, request.Operation.OperationId))!;
        Assert.Equal(mode is "send-unknown" or "folder-revoked" or "folder-reconcile-unknown" ? TenantOperationOutcome.Indeterminate : mode is "folder-rejected" or "send-preflight-refused" ? TenantOperationOutcome.Denied : TenantOperationOutcome.Completed, receipt.Outcome);
        Assert.Equal(claimed.AgentRunId, receipt.RunId);
        var saved = (await runs.FindTenantRunAsync(s.Tenant, claimed.AgentRunId))!;
        if (mode is "folder-revoked" or "send-revoked")
        {
            Assert.True(AgentRunToolCallCheckpoint.TryRead(saved.Checkpoint, out var messages));
            using var payload = JsonDocument.Parse(Assert.Single(messages!, m => m.Role == ModelRole.Tool).Text);
            Assert.Equal(JsonValueKind.Null, payload.RootElement.GetProperty("result").ValueKind);
        }
        else
        {
            var resumed = await services.Continuations.ResumeAsync(saved); Assert.Equal(receipt.Outcome, resumed.Outcome!.Outcome);
            saved = resumed.Run;
            await services.Continuations.PrepareExternalEffectAsync(resumed.Run);
            Assert.Equal(resumed.Run.Revision, (await services.Continuations.ResumeAsync(resumed.Run)).Run.Revision);
        }
        Assert.Equal(tool == ToolCatalog.EmailCreateDraft || mode == "send-preflight-refused" ? 0 : 1, nativeWrites);
        Assert.Equal(tool == ToolCatalog.EmailCreateDraft ? 0 : 1, tokenPosts);
        if (tool == ToolCatalog.EmailCreateDraft)
        {
            var draft = (await f.EmailDrafts.GetDraftAsync(request.Operation.AccountAction!.Draft!.DraftId))!;
            Assert.Equal(s.Run.SessionId, draft.SessionId); Assert.Equal("Exact body", draft.Draft.Body); Assert.Equal("friend@example.test", Assert.Single(draft.Draft.To));
        }
        if (mode == "send-preflight-refused") Assert.Null(await f.EmailDrafts.GetSendAsync("exact-draft"));
        else if (tool == ToolCatalog.EmailSend) Assert.Equal(mode != "send-unknown" ? EmailSendOutcome.Sent : EmailSendOutcome.Indeterminate, (await f.EmailDrafts.GetSendAsync("exact-draft"))!.Result!.Outcome);
        if (mode.StartsWith("send-final-", StringComparison.Ordinal))
        {
            var frame = new TenantRuntimeFrame(saved.Owner, saved.SessionId, saved.AgentRunId, saved.Claim!.Generation);
            var snapshot = (await runtime.LoadTenantRuntimeAsync(frame))!;
            var entry = new ConversationEntry(f.Ids.NewId(), snapshot.DurableLastEntrySequence + 1, saved.ActivationId, ConversationRole.Assistant,
                "Exact protected send result", saved.ResponseId, EntryStatus.Completed, SessionMode.Text, 0, 27, f.Clock.GetUtcNow());
            var output = snapshot with { Revision = snapshot.Revision + 1, LastEntrySequence = entry.Sequence, Entries = [..snapshot.Entries, entry] };
            var complete = new AgentRunCommand.Complete(saved.Revision, f.Clock.GetUtcNow(), saved.Claim.Generation, entry.Text, AgentRunOutcomeKind.Response, entry.EntryId);
            if (mode == "send-final-revoked")
            {
                await f.Access.RevokeAssignmentAsync(s.Owner.PrincipalId, s.Tenant, assignment.AssignmentId, await Revision());
                await Assert.ThrowsAsync<AccessDeniedException>(() => services.Effects.RequireStoredAsync(saved).AsTask());
                await Assert.ThrowsAsync<AccessDeniedException>(() => runtime.CommitTenantOutcomeAsync(frame, output, snapshot.Revision, complete, null, f.Ids.NewId()).AsTask());
                Assert.Equal(snapshot.Revision, (await ((ITenantSessionStore)f.Iam).FindTenantSessionAsync(s.Tenant, run.SessionId))!.Snapshot.Revision);
                Assert.False((await runs.FindTenantRunAsync(s.Tenant, saved.AgentRunId))!.IsTerminal);
            }
            else
            {
                await services.Effects.RequireStoredAsync(saved);
                var completed = await runtime.CommitTenantOutcomeAsync(frame, output, snapshot.Revision, complete, null, f.Ids.NewId());
                Assert.True(completed.IsTerminal); Assert.Equal(entry.EntryId, completed.Result!.OutcomeEntryId);
            }
        }
        if (mode.StartsWith("folder-reconcile", StringComparison.Ordinal))
        {
            await ReconcileAccountTransfer(f, s, services, saved, request, receipt, Enumerable.Repeat("folder", nativeWrites).ToList(),
                mode == "folder-reconcile-unknown" ? "uncertain" : "retained");
            Assert.Equal(1, nativeWrites);
        }
        Assert.Equal(0, await services.Effects.RecoverAsync());
        var audit = await f.Iam.ReadAuditAsync(s.Tenant, 200);
        Assert.Single(audit, a => a.Action == TenantAccountEffectRules.ResultAction && a.Execution?.OperationId == request.Operation.OperationId);
        Assert.DoesNotContain("private-refresh", JsonSerializer.Serialize(audit)); Assert.DoesNotContain("private-access", JsonSerializer.Serialize(audit));
    }

    [Theory] [InlineData(false, "folder-contextual")] [InlineData(true, "folder-contextual")]
    [InlineData(false, "draft-contextual")] [InlineData(true, "draft-contextual")]
    public Task Membership_only_account_alias_uses_existing_Drive_and_Email_effects_with_exact_consent(bool sqlite, string mode) =>
        Account_actions_use_owned_atomic_drafts_exact_successors_and_factual_native_receipts_without_replay(sqlite, mode);
    [Theory] [InlineData(false,"folder-variable")] [InlineData(true,"folder-variable")]
    [InlineData(false,"draft-variable")] [InlineData(true,"draft-variable")]
    public Task Credential_variable_alias_reaches_actual_Drive_and_Email_consumers_with_original_exact_consent(bool sqlite,string mode) =>
        Account_actions_use_owned_atomic_drafts_exact_successors_and_factual_native_receipts_without_replay(sqlite,mode);

}
