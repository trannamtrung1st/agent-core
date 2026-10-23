using System.Text.Json;
using AgentCore.Application.Execution;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantCredentialIntegrationTests
{
    public static TheoryData<bool, string, string> AccountOperationReadCases
    {
        get
        {
            var cases = new TheoryData<bool, string, string>();
            foreach (var sqlite in new[] { false, true })
            {
                foreach (var mode in new[] { "upload", "download", "rename", "move", "trash", "batch", "batch-partial", "rename-stale" }) cases.Add(sqlite, mode, "retained");
                foreach (var mode in new[] { "source-run-revoked", "source-session-revoked", "reopen", "missing", "foreign", "forged-source", "final-revoked", "rejected", "cancelled" }) cases.Add(sqlite, "rename", mode);
                foreach (var mode in new[] { "upload", "rename", "move", "trash", "batch" }) cases.Add(sqlite, mode, "uncertain");
                cases.Add(sqlite, "rename", "unconfirmed"); cases.Add(sqlite, "upload", "native-trashed");
            }
            cases.Add(true, "rename", "schema-source"); cases.Add(true, "rename", "schema-native");
            return cases;
        }
    }
    [Theory] [MemberData(nameof(AccountOperationReadCases))]
    public async Task Canonical_operation_reads_preserve_original_facts_independent_source_access_and_no_write_replay(bool sqlite, string tool, string mode)
        => await AccountTransferScenario(sqlite, tool, mode);

    [Theory] [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)] [InlineData(true, true)]
    public Task Canonical_folder_operation_read_preserves_recorded_outcome_after_retained_or_lost_acknowledgement(bool sqlite, bool uncertain) =>
        Account_actions_use_owned_atomic_drafts_exact_successors_and_factual_native_receipts_without_replay(sqlite, uncertain ? "folder-reconcile-unknown" : "folder-reconcile");

    private static async Task ReconcileAccountTransfer(Fixture f, RuntimeCredentialSetup s, AccountActionServices services, AgentRun prior,
        TenantApprovalRequest original, TenantOperationReceipt originalReceipt, List<string> writes, string mode)
    {
        var count = writes.Count;
        async Task<long> Revision() => (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision;
        // A new human activation has no old effect checkpoint or old write authority.
        // The saved source itself carries no usable grant.
        var session = await f.Sessions.CreateAsync(s.Tenant, s.Agent.Instance.InstanceId, s.Owner, SessionMode.Text, await Revision(), s.Agent.Instance.Revision);
        var submitted = await f.RunService().SubmitTextAsync(s.Tenant, session.Owner.SessionId, s.Owner, f.Ids.NewId(), "Inspect the previous exact Drive operation", await Revision(), 1);
        var runs = (ITenantRunStore)f.Iam; var runtime = (ITenantRuntimeStore)f.Iam;
        var queued = (await runs.FindTenantRunAsync(s.Tenant, submitted.RunId))!;
        var run = await runtime.ApplyTenantRuntimeCommandAsync(queued.Owner, queued.AgentRunId,
            new AgentRunCommand.Claim(queued.Revision, f.Clock.GetUtcNow(), f.Ids.NewId(), f.Clock.GetUtcNow().AddMinutes(5)), f.Ids.NewId());
        var operationId = mode == "foreign" ? Guid.NewGuid() : original.Operation.OperationId;
        if (mode == "foreign")
        {
            var existing = (await services.Ledger.GetAsync(s.Agent.Instance.InstanceId, original.Operation.OperationId, default))!;
            await services.Ledger.ClaimAsync(existing with { OperationId = operationId, Revision = 1 }, default);
        }
        if (mode == "missing") await services.Ledger.DeleteOwnerAsync(s.Agent.Instance.InstanceId, default);
        var input = TenantAccountReadContract.Parse(ToolCatalog.DriveGet, JsonSerializer.Serialize(new { accountRef = "work-drive", operationId }));
        run = await AccountPendingCall(f, run, AccountIntentCall(ToolCatalog.DriveGet, input.Json));
        // Keep only current read authority: the old write/delete/workspace
        // permissions are irrelevant to this separately approved read.
        {
            var permissions = mode == "source-run-revoked" ? new[] { "sessions.read", "drive.read" }
                : mode == "source-session-revoked" ? new[] { "agent-runs.read", "drive.read" } : new[] { "sessions.read", "agent-runs.read", "drive.read" };
            // Remove original Run access while retaining current Session/account reads.
            var role = await f.Access.SaveRoleAsync(s.Owner.PrincipalId, s.Tenant, null, "Only current source reads", permissions, await Revision());
            var state = (await f.Iam.ReadTenantAsync(s.Tenant))!;
            var oldRole = state.Roles.Single(r => r.Name is "Exact transfers" or "Exact account action");
            var oldAssignment = state.Assignments.Single(a => a.PrincipalId == s.Agent.PrincipalId && a.RoleId == oldRole.RoleId);
            await f.Access.RevokeAssignmentAsync(s.Owner.PrincipalId, s.Tenant, oldAssignment.AssignmentId, await Revision());
            await f.Access.AssignRoleAsync(s.Owner.PrincipalId, s.Tenant, s.Agent.PrincipalId, role.RoleId, AccessScope.ForTenant(s.Tenant), await Revision());
            if (mode == "source-session-revoked")
            {
                var currentRole = await f.Access.SaveRoleAsync(s.Owner.PrincipalId, s.Tenant, null, "Current Session only", ["sessions.read"], await Revision());
                await f.Access.AssignRoleAsync(s.Owner.PrincipalId, s.Tenant, s.Agent.PrincipalId, currentRole.RoleId, TenantSessionRules.Scope(s.Tenant, session.Owner.SessionId), await Revision());
            }
        }
        if (mode is "foreign" or "source-run-revoked" or "source-session-revoked")
        {
            await Assert.ThrowsAsync<AccessDeniedException>(() => services.Approvals.PrepareAccountReadAsync(new(s.Tenant, run.AgentRunId), new(ToolCatalog.DriveGet, input.Json), run.Revision, run.Claim!.Generation).AsTask());
            Assert.Equal(count, writes.Count); return;
        }
        var prepared = await services.Approvals.PrepareAccountReadAsync(new(s.Tenant, run.AgentRunId), new(ToolCatalog.DriveGet, input.Json), run.Revision, run.Claim!.Generation);
        var request = prepared.Request!; var source = request.Operation.AccountRead!.Source!;
        Assert.Equal(originalReceipt.RunId, source.RunId); Assert.Equal(originalReceipt.Outcome.ToString(), source.RecordedOutcome);
        Assert.Equal(count, writes.Count);
        var visibility = (ITenantApprovalStore)f.Iam;
        Assert.DoesNotContain((await visibility.ListApprovalsAsync(s.Tenant, null, null, new(), reportVisibility: new(true, null, []))).Items, r => r.RequestId == request.RequestId);
        Assert.DoesNotContain((await visibility.ListApprovalsAsync(s.Tenant, null, null, new(), reportVisibility: new(true, [session.Owner.SessionId], null))).Items, r => r.RequestId == request.RequestId);
        if (f.Contexts is not null)
        {
            await using var db = f.Contexts.CreateDbContext();
            Assert.Equal(2, await db.Database.SqlQueryRaw<int>("SELECT count(*) AS Value FROM sqlite_master WHERE type='trigger' AND name LIKE 'TR_TenantApprovals_AccountOperationRead_%'").SingleAsync());
            if (mode is "schema-source" or "schema-native")
            {
                var columns = await db.Database.SqlQueryRaw<string>("SELECT name AS Value FROM pragma_table_info('TenantApprovalRequests')").ToArrayAsync();
                var quoted = columns.Select(name => "\"" + name.Replace("\"", "\"\"") + "\"").ToArray();
                var mutation = mode == "schema-source" ? "json_set(PayloadJson,'$.Operation.AccountRead.Source.RequestId',{1})"
                    : "json_set(PayloadJson,'$.Operation.AccountRead.Source.NativeReferencesJson',{1})";
                var selected = columns.Select((name, i) => name == "PayloadJson" ? mutation : quoted[i]);
                var sql = "INSERT INTO TenantApprovalRequests (" + string.Join(",", quoted) + ") SELECT " + string.Join(",", selected) + " FROM TenantApprovalRequests WHERE RequestId={0}";
                var failure = await Assert.ThrowsAsync<SqliteException>(() => db.Database.ExecuteSqlRawAsync(sql, request.RequestId, mode == "schema-source" ? "00000000-0000-0000-0000-000000000000" : "[{}]"));
                Assert.Contains(mode == "schema-source" ? "canonical operation read source required" : "invalid native operation references", failure.Message);
            }
            await Assert.ThrowsAsync<SqliteException>(() => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE TenantApprovalRequests SET PayloadJson=json_remove(PayloadJson,'$.Operation.AccountRead.Source') WHERE RequestId={request.RequestId}"));
            var guardsBeforeVeto = await AccountConsentGuards(db);
            await Assert.ThrowsAsync<SqliteException>(() => db.GetService<IMigrator>().MigrateAsync("20261020160000_TenantAccountActionReview"));
            // Earlier empty migrations may already be down; restore the current model after the populated-data veto.
            await db.Database.MigrateAsync();
            Assert.False(db.Database.HasPendingModelChanges());
            Assert.Equal(guardsBeforeVeto, await AccountConsentGuards(db));
        }
        if (mode == "forged-source")
        {
            var forged = request.Operation.AccountRead with { Source = source with { RunId = Guid.NewGuid() } };
            await Assert.ThrowsAsync<AccessDeniedException>(() => services.Reads.RequireInspectionAsync(request with { Operation = request.Operation with { AccountRead = forged } }, (runs.FindTenantRunAsync(s.Tenant, request.RunId).AsTask().Result)!).AsTask());
            Assert.Equal(count, writes.Count); return;
        }
        if (mode is "rejected" or "cancelled")
        {
            var view = await services.Approvals.ReadViewAsync(s.Tenant, request.RequestId, s.Owner);
            if (mode == "rejected") await services.Approvals.DecideAsync(s.Tenant, request.RequestId, s.Owner, false, request.Operation.ActionHash, view.SecurityRevision, request.Revision);
            else await services.Approvals.CancelAsync(s.Tenant, request.RequestId, s.Owner, request.Operation.ActionHash, view.SecurityRevision, request.Revision);
            Assert.Equal(count, writes.Count); return;
        }
        if (mode == "reopen" && f.Contexts is not null)
            services = ActionServices(f, new OAuthHttp(_ => throw new InvalidOperationException("Retained verification must not call Google.")),
                new AgentCore.Infrastructure.Security.SqliteIamStore(f.Contexts, builtIns: f.BuiltIns, clock: f.Clock));
        run = await ApproveAccountContinuation(f, s, services, request);
        run = (await services.Continuations.ResumeAsync(run)).Run;
        Assert.True(AgentRunToolCallCheckpoint.TryRead(run.Checkpoint, out var messages));
        using var result = JsonDocument.Parse(messages!.Last(m => m.Role == ModelRole.Tool).Text);
        var operation = result.RootElement.GetProperty("accountRead").GetProperty("result").GetProperty("operation");
        Assert.Equal(originalReceipt.Outcome.ToString(), operation.GetProperty("recordedOutcome").GetString());
        Assert.False(operation.GetProperty("replayAllowed").GetBoolean());
        var items = operation.GetProperty("items").EnumerateArray().ToArray();
        if (original.Operation.AccountAction!.ToolName == ToolCatalog.DriveTrashBatch)
        {
            using var manifest = JsonDocument.Parse(original.Operation.AccountAction.ArgumentsJson);
            Assert.Equal(manifest.RootElement.GetProperty("files").EnumerateArray().Select(i => i.GetProperty("fileId").GetString()), items.Select(i => i.GetProperty("fileId").GetString()));
        }
        if (original.Operation.AccountAction!.ToolName == ToolCatalog.DriveDownload) Assert.Empty(items);
        else if (mode == "missing" || originalReceipt.Outcome == TenantOperationOutcome.Denied)
            Assert.All(items, i => Assert.Equal("no_retained_evidence", i.GetProperty("evidence").GetString()));
        else if (mode == "unconfirmed") Assert.All(items, i => Assert.Equal("indeterminate", i.GetProperty("status").GetString()));
        else if (mode == "native-trashed") Assert.All(items, i => { Assert.Equal("failed", i.GetProperty("status").GetString()); Assert.Equal("current_remote_metadata:verified_target_trashed", i.GetProperty("evidence").GetString()); });
        else if (mode is "batch-unknown-before" or "batch-unknown-after")
        {
            Assert.Equal("verified", items[0].GetProperty("status").GetString());
            Assert.Equal("retained_verification", items[0].GetProperty("evidence").GetString());
            Assert.Equal(mode == "batch-unknown-after" ? "verified" : "indeterminate", items[1].GetProperty("status").GetString());
            Assert.All(items.Skip(2), i => { Assert.Equal("unknown", i.GetProperty("status").GetString()); Assert.Equal("no_retained_evidence", i.GetProperty("evidence").GetString()); });
        }
        else if (mode == "uncertain")
        {
            Assert.Equal("current_remote_metadata", items[0].GetProperty("evidence").GetString());
            Assert.Equal("verified", items[0].GetProperty("status").GetString());
            if (original.Operation.AccountAction.ToolName == ToolCatalog.DriveTrashBatch)
                Assert.All(items.Skip(1), i => Assert.Equal("unknown", i.GetProperty("status").GetString()));
        }
        else if (original.Operation.AccountAction.ToolName == ToolCatalog.DriveTrashBatch && originalReceipt.Outcome == TenantOperationOutcome.Indeterminate)
            Assert.Equal(items.Select((_, i) => i == 0 ? "verified" : i == 1 ? "not_applied" : "unknown"), items.Select(i => i.GetProperty("status").GetString()));
        else Assert.All(items, i => Assert.Equal("retained_verification", i.GetProperty("evidence").GetString()));
        Assert.DoesNotContain("ProtectedUploadSession", result.RootElement.GetRawText()); Assert.DoesNotContain("resumeAllowed", result.RootElement.GetRawText());
        Assert.Equal(count, writes.Count);
        Assert.Equal(originalReceipt, await ((ITenantApprovedOperationStore)f.Iam).FindOperationReceiptAsync(s.Tenant, originalReceipt.OperationId));
        if (mode == "final-revoked")
        {
            var state = (await f.Iam.ReadTenantAsync(s.Tenant))!; var role = state.Roles.Single(r => r.Name == "Only current source reads");
            var assignment = state.Assignments.Single(a => a.PrincipalId == s.Agent.PrincipalId && a.RoleId == role.RoleId);
            await f.Access.RevokeAssignmentAsync(s.Owner.PrincipalId, s.Tenant, assignment.AssignmentId, await Revision());
            var remaining = await f.Access.SaveRoleAsync(s.Owner.PrincipalId, s.Tenant, null, "Current reads without original Run", ["sessions.read", "drive.read"], await Revision());
            await f.Access.AssignRoleAsync(s.Owner.PrincipalId, s.Tenant, s.Agent.PrincipalId, remaining.RoleId, AccessScope.ForTenant(s.Tenant), await Revision());
            await Assert.ThrowsAsync<AccessDeniedException>(() => services.Reads.RequireStoredAsync(run).AsTask());
            var frame = new TenantRuntimeFrame(run.Owner, run.SessionId, run.AgentRunId, run.Claim!.Generation);
            var snapshot = (await runtime.LoadTenantRuntimeAsync(frame))!;
            var complete = new AgentRunCommand.Complete(run.Revision, f.Clock.GetUtcNow(), run.Claim.Generation, "Protected operation facts", AgentRunOutcomeKind.Response, null);
            await Assert.ThrowsAsync<AccessDeniedException>(() => runtime.CommitTenantOutcomeAsync(frame, snapshot with { Revision = snapshot.Revision + 1 }, snapshot.Revision, complete, null, f.Ids.NewId()).AsTask());
        }
        else await services.Reads.RequireStoredAsync(run);
    }
}
