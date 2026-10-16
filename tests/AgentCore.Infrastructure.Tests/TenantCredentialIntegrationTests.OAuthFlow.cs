using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Credentials;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Credentials;
using AgentCore.Infrastructure.Drive;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Time.Testing;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantCredentialIntegrationTests
{
    [Theory]
    [InlineData(false, "permission")]
    [InlineData(true, "permission")]
    [InlineData(false, "logout")]
    [InlineData(true, "logout")]
    [InlineData(false, "membership")]
    [InlineData(true, "membership")]
    [InlineData(false, "disable")]
    [InlineData(true, "disable")]
    [InlineData(false, "supersede")]
    [InlineData(true, "supersede")]
    [InlineData(false, "expiry")]
    [InlineData(true, "expiry")]
    [InlineData(false, "cancel")]
    [InlineData(true, "cancel")]
    [InlineData(false, "host")]
    [InlineData(true, "host")]
    public async Task OAuth_orchestration_revalidates_after_provider_exchange(bool sqlite, string boundary)
    {
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-10-17T06:00:00Z"));
        await using var f = await Fixture.Create(sqlite, clock);
        var owner = await f.Identity.BootstrapAsync("owner@example.test", "Owner", "strong-owner-password", "Alpha", "alpha");
        var writer = await AddUser(f, owner.TenantId, owner.OwnerPrincipalId, "agent-operator");
        var session = (await f.Identity.SignInAsync("writer@example.test", "strong-writer-password"))!.Session;
        async Task<long> Revision() => (await f.Iam.ReadTenantAsync(owner.TenantId))!.Tenant.Revision;
        var view = await f.Service.CreateAsync(owner.OwnerPrincipalId, owner.TenantId, OAuthInput(CredentialPurpose.GoogleDriveAccount), await Revision());
        var id = view.Credential.CredentialId;
        var role = await f.Access.SaveRoleAsync(owner.OwnerPrincipalId, owner.TenantId, null, "Connect one", ["credentials.read", "credentials.manage"], await Revision());
        var assignment = await f.Access.AssignRoleAsync(owner.OwnerPrincipalId, owner.TenantId, writer.User.PrincipalId, role.RoleId, TenantCredentialRules.Scope(owner.TenantId, id), await Revision());
        var network = new OAuthNoNetwork(); var host = new GoogleDriveOptions();
        var gate = new GatedOAuthProvider(OAuthProvider(f, network, true, host: host)); var service = OAuthService(f, gate);
        var initiator = new CredentialOAuthInitiator(writer.Account.AccountId, session.SessionId, writer.User.PrincipalId);
        var start = await service.StartAsync(owner.TenantId, id, initiator, await Revision(), 1);
        var stateProof = StateFrom(start.AuthorizationUrl);
        Assert.True(TenantCredentialOAuthService.TryState(stateProof, out _, out var attemptId));
        using var cancellation = new CancellationTokenSource();
        var completion = service.CompleteAsync(stateProof, start.Correlation, null, "synthetic-consent", null, true, cancellation.Token).AsTask();
        try
        {
            await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (boundary == "permission") await f.Access.RevokeAssignmentAsync(owner.OwnerPrincipalId, owner.TenantId, assignment.AssignmentId, await Revision());
            if (boundary == "logout") await f.Identity.SignOutAsync(session.SessionId);
            if (boundary == "membership") await f.Access.SetMemberStatusAsync(owner.OwnerPrincipalId, owner.TenantId, writer.User.UserId, IdentityStatus.Suspended, await Revision());
            if (boundary == "disable") await f.Service.UpdateAsync(owner.OwnerPrincipalId, owner.TenantId, id, new("Disabled", CredentialStatus.Disabled, null, null), await Revision(), 1);
            if (boundary == "supersede") await service.StartAsync(owner.TenantId, id, initiator, await Revision(), 1);
            if (boundary == "expiry") clock.Advance(TimeSpan.FromMinutes(5));
            if (boundary == "cancel") cancellation.Cancel();
            if (boundary == "host") host.Enabled = false;
        }
        finally { gate.Proceed.TrySetResult(); }
        if (boundary == "cancel") await Assert.ThrowsAnyAsync<OperationCanceledException>(() => completion);
        else Assert.Equal("failed", (await completion).Outcome);
        var retained = (await f.Credentials.FindTenantCredentialAsync(owner.TenantId, id))!.Value;
        Assert.Empty(retained.ProtectedPayload); Assert.Equal(0, retained.SecretRevision);
        Assert.Equal(boundary == "disable" ? 2 : 1, retained.Revision);
        var terminal = (await ((ITenantCredentialOAuthStore)f.Iam).ReadCredentialOAuthAttemptAsync(attemptId))!;
        Assert.Equal(boundary == "supersede" ? CredentialOAuthOutcome.Superseded : CredentialOAuthOutcome.Failed, terminal.Outcome);
        Assert.Empty(terminal.ProtectedProof); Assert.Equal(0, network.Calls);
        Assert.DoesNotContain(await f.Iam.ReadAuditAsync(owner.TenantId, 200), a => a.Action == "credentials.oauth.connect");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OAuth_orchestration_requires_current_exact_authority_before_stale_witnesses_or_preparation(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite);
        var owner = await f.Identity.BootstrapAsync("owner@example.test", "Owner", "strong-owner-password", "Alpha", "alpha");
        var writer = await AddUser(f, owner.TenantId, owner.OwnerPrincipalId, "agent-operator");
        var session = (await f.Identity.SignInAsync("writer@example.test", "strong-writer-password"))!.Session;
        var revision = (await f.Iam.ReadTenantAsync(owner.TenantId))!.Tenant.Revision;
        var created = await f.Service.CreateAsync(owner.OwnerPrincipalId, owner.TenantId, OAuthInput(CredentialPurpose.EmailAccount), revision);
        var id = created.Credential.CredentialId; var network = new OAuthNoNetwork(); var service = OAuthService(f, OAuthProvider(f, network, true));
        var error = await Assert.ThrowsAsync<AccessDeniedException>(() => service.StartAsync(owner.TenantId, id,
            new(writer.Account.AccountId, session.SessionId, writer.User.PrincipalId), -1, -1).AsTask());
        Assert.Equal("permission_denied", error.ReasonCode);
        Assert.Equal(revision + 1, (await f.Iam.ReadTenantAsync(owner.TenantId))!.Tenant.Revision);
        Assert.False(Directory.Exists(Path.Combine(f.Root, "keys", "credential-oauth")));
        Assert.Equal(0, network.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OAuth_claim_rechecks_actual_Credential_revision_before_releasing_private_proof(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite);
        var owner = await f.Identity.BootstrapAsync("owner@example.test", "Owner", "strong-owner-password", "Alpha", "alpha");
        var session = (await f.Identity.SignInAsync("owner@example.test", "strong-owner-password"))!.Session;
        var view = await f.Service.CreateAsync(owner.OwnerPrincipalId, owner.TenantId, OAuthInput(CredentialPurpose.GoogleDriveAccount), 1);
        var id = view.Credential.CredentialId; var current = (await f.Credentials.FindTenantCredentialAsync(owner.TenantId, id))!.Value;
        var a = OAuthAttempt(f, owner.TenantId, current, new(owner.AccountId, session.SessionId, owner.OwnerPrincipalId));
        Assert.True(await BeginOAuth(f, a));
        var state = (await f.Iam.ReadTenantAsync(owner.TenantId))!;
        await f.Service.UpdateAsync(owner.OwnerPrincipalId, owner.TenantId, id, new("Changed during consent", CredentialStatus.Active, null, null), state.Tenant.Revision, 1);
        state = (await f.Iam.ReadTenantAsync(owner.TenantId))!;
        var store = (ITenantCredentialOAuthStore)f.Iam;
        Assert.Null(await store.TryClaimCredentialOAuthAsync(a.AttemptId, a.StateHash, a.CorrelationHash, state.Tenant.Revision));
        var retained = (await store.ReadCredentialOAuthAttemptAsync(a.AttemptId))!;
        Assert.Equal(CredentialOAuthOutcome.Pending, retained.Outcome); Assert.Equal(a.ProtectedProof, retained.ProtectedProof);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OAuth_deadline_cancels_exchange_without_waiting_for_provider_gate(bool sqlite)
    {
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-10-17T08:00:00Z"));
        await using var f = await Fixture.Create(sqlite, clock);
        var owner = await f.Identity.BootstrapAsync("owner@example.test", "Owner", "strong-owner-password", "Alpha", "alpha");
        var session = (await f.Identity.SignInAsync("owner@example.test", "strong-owner-password"))!.Session;
        var view = await f.Service.CreateAsync(owner.OwnerPrincipalId, owner.TenantId, OAuthInput(CredentialPurpose.GoogleDriveAccount), 1);
        var id = view.Credential.CredentialId; var gate = new GatedOAuthProvider(OAuthProvider(f, new OAuthNoNetwork(), true)); var service = OAuthService(f, gate);
        var start = await service.StartAsync(owner.TenantId, id, new(owner.AccountId, session.SessionId, owner.OwnerPrincipalId), 2, 1);
        var completion = service.CompleteAsync(StateFrom(start.AuthorizationUrl), start.Correlation, null, "synthetic-consent", null, true).AsTask();
        try
        {
            await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); clock.Advance(TimeSpan.FromMinutes(5));
            Assert.Equal("failed", (await completion.WaitAsync(TimeSpan.FromSeconds(5))).Outcome);
            Assert.False(gate.Proceed.Task.IsCompleted);
            Assert.Empty((await f.Credentials.FindTenantCredentialAsync(owner.TenantId, id))!.Value.ProtectedPayload);
        }
        finally { gate.Proceed.TrySetResult(); }
    }
    private static string StateFrom(string url) => Uri.UnescapeDataString(url.Split("?state=")[1]);
    private static TenantCredentialOAuthService OAuthService(Fixture f, ITenantCredentialOAuthProvider provider) => new(f.Iam, f.Credentials,
        (ITenantCredentialOAuthStore)f.Iam, provider, f.Identity, f.Authorizer, f.Service, f.Tokens, f.Ids, f.Clock);
    private static TenantGoogleOAuthProvider OAuthProvider(Fixture f, IHttpClientFactory clients, bool synthetic, GoogleOAuthConfiguration? configuration = null, GoogleDriveOptions? host = null) =>
        new(configuration ?? new(new ConfigurationBuilder().Build()), host ?? new(), new(Path.Combine(f.Root, "keys")), f.Protector, clients, synthetic);
    private sealed class OAuthNoNetwork : IHttpClientFactory
    {
        public int Calls;
        public HttpClient CreateClient(string name) { Interlocked.Increment(ref Calls); throw new InvalidOperationException("Unexpected OAuth network."); }
    }
    private sealed class GatedOAuthProvider(ITenantCredentialOAuthProvider inner) : ITenantCredentialOAuthProvider
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Proceed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CredentialOAuthDescriptor Describe(CredentialPurpose purpose) => inner.Describe(purpose);
        public CredentialOAuthPrepared Prepare(CredentialOAuthAttempt attempt, string state) => inner.Prepare(attempt, state);
        public async ValueTask<CredentialOAuthGrant> ExchangeAsync(CredentialOAuthClaim claim, string code, Credential current, CancellationToken ct)
        { Entered.TrySetResult(); await Proceed.Task.WaitAsync(ct); return await inner.ExchangeAsync(claim, code, current, ct); }
        public ValueTask<bool> RevokeAsync(Credential current, CancellationToken ct) => inner.RevokeAsync(current, ct);
    }
}
