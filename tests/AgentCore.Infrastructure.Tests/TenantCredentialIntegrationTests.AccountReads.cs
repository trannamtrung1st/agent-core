using System.Net;
using System.Text.Json;
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
using AgentCore.Infrastructure.Security;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantCredentialIntegrationTests
{
    public static TheoryData<bool, CredentialPurpose, string> AgentAccountReadCases
    {
        get
        {
            var result = new TheoryData<bool, CredentialPurpose, string>();
            foreach (var sqlite in new[] { false, true })
            foreach (var purpose in new[] { CredentialPurpose.EmailAccount, CredentialPurpose.GoogleDriveAccount })
            foreach (var mode in new[] { "rotate", "exception", "invalid-grant", "token-revoked", "resource-revoked", "secret-changed", "independent", "family-missing" }) result.Add(sqlite, purpose, mode);
            return result;
        }
    }
    private static TenantAccountRuntimeReadService RuntimeAccountReads(Fixture f, IHttpClientFactory http, IIamStore? reopened = null, IDriveOperationStore? ledger = null)
    {
        var store = reopened ?? f.Iam; var iam = new IamAuthorizer(store);
        var drive = new GoogleDriveOptions { Enabled = true }; IDriveOperationStore files = ledger ?? new InMemoryDriveStore();
        var native = new TenantConnectedAccountReader(new(SharedOAuth(), drive, new(Path.Combine(f.Root, "keys")), f.Protector, http, false),
            new EmailTransportOptions { Enabled = true }, drive, http, f.Protector, files, (ISyntheticDriveStore)files, f.Ids, f.Clock, false, NullLoggerFactory.Instance, new SystemDiagnosticIdSource());
        return new(store, (ITenantRunStore)store, (ITenantSessionStore)store, (ITenantCredentialBindingStore)store, f.RunConfiguration(),
            new(store, (ITenantRunStore)store, (ITenantAgentStore)store, iam, new((ITenantAuthorizationAuditStore)store, f.Ids, f.Clock)),
            iam, (ITenantApprovalStore)store, (ITenantApprovedOperationStore)store, (ITenantConnectedAccountReadStore)store, native, f.Ids, f.Clock, files, new(f.Legacy, (IAgentCredentialBindingStore)f.Legacy, f.Instances, new NoLegacyDriveFactory(), files, f.Clock), native);
    }
    private static TenantApprovalService AccountApprovals(Fixture f, TenantAccountRuntimeReadService reads, IIamStore? reopened = null)
    {
        var store = reopened ?? f.Iam; var iam = new IamAuthorizer(store);
        return new(store, (ITenantApprovalStore)store, (ITenantAgentStore)store,
            new(store, (ITenantRunStore)store, (ITenantAgentStore)store, iam, new((ITenantAuthorizationAuditStore)store, f.Ids, f.Clock)), iam, f.Identity,
            f.Ids, f.Clock, f.RunConfiguration(), ModelCatalogFactory.Synthetic(), SyntheticProviderAliases.Default, (ITenantApprovedOperationStore)store, accountReads: reads);
    }

    [Theory] [MemberData(nameof(AgentAccountReadCases))]
    public async Task Agent_account_read_real_transport_preserves_exact_consent_rotation_and_current_compound_authority(bool sqlite, CredentialPurpose purpose, string mode)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true);
        var s = await RuntimeCredential(f, mode != "exception", purpose); var familyKey = purpose == CredentialPurpose.EmailAccount ? "email.read" : "drive.read";
        async Task<long> Revision() => (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision;
        var role = await f.Access.SaveRoleAsync(s.Owner.PrincipalId, s.Tenant, null, "Account source reads", ["sessions.read", familyKey], await Revision());
        RoleAssignment? family = null;
        if (mode != "family-missing") family = await f.Access.AssignRoleAsync(s.Owner.PrincipalId, s.Tenant, s.Agent.PrincipalId, role.RoleId, AccessScope.ForTenant(s.Tenant), await Revision());
        var posts = 0; var gets = 0;
        var http = new OAuthHttp(async request => {
            if (request.RequestUri!.AbsolutePath == "/token")
            {
                posts++; Assert.Equal("tenant-client", OAuthQuery(await request.Content!.ReadAsStringAsync())["client_id"]);
                if (mode == "token-revoked") await f.Access.RevokeAssignmentAsync(s.Owner.PrincipalId, s.Tenant, family!.AssignmentId, await Revision());
                if (mode == "invalid-grant") return new(HttpStatusCode.BadRequest) { Content = new StringContent("{\"error\":\"invalid_grant\"}") };
                return OAuthJson(new { access_token = "private-access", refresh_token = "rotated-private-refresh", token_type = "Bearer", expires_in = 3600 });
            }
            gets++; Assert.Equal(HttpMethod.Get, request.Method); Assert.Equal("private-access", request.Headers.Authorization!.Parameter);
            if (mode == "resource-revoked") await f.Access.RevokeAssignmentAsync(s.Owner.PrincipalId, s.Tenant, family!.AssignmentId, await Revision());
            if (purpose == CredentialPurpose.GoogleDriveAccount) return OAuthJson(new { files = new[] { ConnectedFile([1, 2, 3]) } });
            return request.RequestUri.AbsolutePath.EndsWith("/messages", StringComparison.Ordinal)
                ? OAuthJson(new { messages = new[] { new { id = "mail-id" } } })
                : OAuthJson(new { id = "mail-id", threadId = "thread-id", payload = new { headers = new[] { new { name = "Subject", value = "Exact private subject" } } } });
        });
        var reads = RuntimeAccountReads(f, http); var approvals = AccountApprovals(f, reads);
        var tool = purpose == CredentialPurpose.EmailAccount ? ToolCatalog.EmailSearch : ToolCatalog.DriveList;
        var input = TenantAccountReadContract.Parse(tool, """{"accountRef":"work-drive","query":"exact","limit":1}""");
        if (mode == "family-missing")
        {
            var denied = await Assert.ThrowsAsync<AccessDeniedException>(() => approvals.PrepareAccountReadAsync(new(s.Tenant, s.Run.AgentRunId), new(tool, input.Json), s.Run.Revision, s.Run.Claim!.Generation).AsTask());
            Assert.Equal("account_read_required", denied.ReasonCode); Assert.Equal(0, posts); Assert.Equal(0, gets); return;
        }
        var prepared = await approvals.PrepareAccountReadAsync(new(s.Tenant, s.Run.AgentRunId), new(tool, input.Json), s.Run.Revision, s.Run.Claim!.Generation);
        Assert.Equal(OperationDecisionKind.RequiresApproval, prepared.Decision); var request = Assert.IsType<TenantApprovalRequest>(prepared.Request);
        var view = await approvals.ReadViewAsync(s.Tenant, request.RequestId, s.Owner);
        await approvals.DecideAsync(s.Tenant, request.RequestId, s.Owner, true, request.Operation.ActionHash, view.SecurityRevision, request.Revision);
        Assert.Equal(1, await approvals.DispatchPendingAsync());
        var queued = (await ((ITenantRunStore)f.Iam).FindTenantRunAsync(s.Tenant, s.Run.AgentRunId))!;
        var run = await ((ITenantRuntimeStore)f.Iam).ApplyTenantRuntimeCommandAsync(queued.Owner, queued.AgentRunId,
            new AgentRunCommand.Claim(queued.Revision, f.Clock.GetUtcNow(), f.Ids.NewId(), f.Clock.GetUtcNow().AddMinutes(5)), f.Ids.NewId());
        var effects = new TenantApprovedOperationService(f.Iam, (ITenantRunStore)f.Iam, (ITenantAgentStore)f.Iam, (ITenantApprovalStore)f.Iam,
            (ITenantApprovedOperationStore)f.Iam, new(f.Iam, (ITenantRunStore)f.Iam, (ITenantAgentStore)f.Iam, f.Authorizer, new((ITenantAuthorizationAuditStore)f.Iam, f.Ids, f.Clock)),
            f.RunConfiguration(), f.Ids, f.Clock, ModelCatalogFactory.Synthetic(), SyntheticProviderAliases.Default, accountReads: reads);
        var receipt = await effects.ExecuteAsync(new(s.Tenant, run.AgentRunId), request.RequestId, request.Operation.OperationId, run.Claim!.Generation, request.Operation.ActionHash);
        Assert.Equal(TenantAccountReadRules.Reason, receipt.ReasonCode); Assert.Equal(0, posts); Assert.Equal(0, gets);
        run = (await ((ITenantRunStore)f.Iam).FindTenantRunAsync(s.Tenant, run.AgentRunId))!;
        request = (await ((ITenantApprovalStore)f.Iam).FindApprovalAsync(s.Tenant, request.RequestId))!;
        var before = (await f.Credentials.FindTenantCredentialAsync(s.Tenant, s.CredentialId))!.Value;
        if (mode == "secret-changed")
        {
            var state = (await f.Iam.ReadTenantAsync(s.Tenant))!;
            Assert.True(await ((ITenantCredentialOAuthStore)f.Iam).TryCheckCredentialOAuthAsync(Next(state), state.Tenant.Revision, s.CredentialId,
                new(s.Owner.AccountId, s.Owner.AuthenticationSessionId, s.Owner.PrincipalId), before.Revision, before.SecretRevision,
                new(f.Protector.Protect(s.CredentialId, before.ProtectionVersion, GoogleOAuthRefreshGrant.Write("different-private-refresh", "tenant-client"))), f.Audit(s.Tenant, s.Owner.PrincipalId, s.CredentialId)));
        }
        if (mode == "independent")
            await new TenantOperationPolicyService(f.Iam, (ITenantOperationPolicyStore)f.Iam, f.Authorizer, f.Identity, f.Ids, f.Clock).SaveAsync(s.Tenant, s.Owner,
                "credentials.use", new(OperationApprovalMode.Always, true), await Revision(), 0);
        // Recreate the SQLite owner and application facade after consent; transport still uses controlled real-adapter HTTP.
        if (sqlite) reads = RuntimeAccountReads(f, http, new SqliteIamStore(f.Contexts!, builtIns: f.BuiltIns, clock: f.Clock));
        string? result = null;
        var failure = await Record.ExceptionAsync(async () => result = await reads.InspectApprovedForRunAsync(new(run.Owner, run.SessionId, run.AgentRunId, run.Claim!.Generation), request, receipt, 32768));
        var after = (await f.Credentials.FindTenantCredentialAsync(s.Tenant, s.CredentialId))!.Value;
        var audit = await f.Iam.ReadAuditAsync(s.Tenant, 200);
        if (mode is "rotate" or "exception")
        {
            Assert.Null(failure); Assert.NotNull(result); Assert.Contains("tenantAccountRead", result); Assert.DoesNotContain("private-refresh", result); Assert.DoesNotContain("private-access", result);
            Assert.Equal(1, posts); Assert.Equal(purpose == CredentialPurpose.EmailAccount ? 2 : 1, gets);
            Assert.Equal(before.SecretRevision + 1, after.SecretRevision); Assert.Equal(before.Revision, after.Revision);
            Assert.Equal("rotated-private-refresh", GoogleOAuthRefreshGrant.Read(f.Protector.Unprotect(s.CredentialId, after.ProtectionVersion, after.ProtectedPayload)).Token);
            Assert.Contains(audit, a => a.Action == "operations.accounts.oauth.maintain" && a.Execution?.OperationId == request.Operation.OperationId);
            Assert.Single(audit, a => a.Action == "operations.accounts.read" && a.Execution?.OperationId == request.Operation.OperationId);
        }
        else
        {
            Assert.NotNull(failure); Assert.Null(result); Assert.DoesNotContain(audit, a => a.Action == "operations.accounts.read");
            Assert.Equal(mode is "secret-changed" or "independent" ? 0 : 1, posts); Assert.Equal(mode == "resource-revoked" ? 1 : 0, gets);
            if (mode == "invalid-grant") { Assert.Empty(after.ProtectedPayload); Assert.Equal(before.SecretRevision + 1, after.SecretRevision); }
            else if (mode is "secret-changed" or "resource-revoked") Assert.Equal(before.SecretRevision + 1, after.SecretRevision);
            else Assert.Equal(before.ProtectedPayload, after.ProtectedPayload);
        }
    }
}
