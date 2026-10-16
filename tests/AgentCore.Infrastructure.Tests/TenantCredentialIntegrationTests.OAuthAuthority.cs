using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Credentials;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantCredentialIntegrationTests
{
    [Theory]
    [InlineData(false, "logout")]
    [InlineData(true, "logout")]
    [InlineData(false, "password")]
    [InlineData(true, "password")]
    [InlineData(false, "membership")]
    [InlineData(true, "membership")]
    [InlineData(false, "expiry")]
    [InlineData(true, "expiry")]
    public async Task OAuth_store_rechecks_original_human_session_at_commit(bool sqlite, string boundary)
    {
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-10-17T04:00:00Z"));
        await using var f = await Fixture.Create(sqlite, clock);
        var owner = await f.Identity.BootstrapAsync("owner@example.test", "Owner", "strong-owner-password", "Alpha", "alpha");
        var writer = await AddUser(f, owner.TenantId, owner.OwnerPrincipalId, "contributor");
        var session = (await f.Identity.SignInAsync("writer@example.test", "strong-writer-password"))!.Session;
        var state = (await f.Iam.ReadTenantAsync(owner.TenantId))!;
        var view = await f.Service.CreateAsync(writer.User.PrincipalId, owner.TenantId, OAuthInput(CredentialPurpose.EmailAccount), state.Tenant.Revision);
        var current = (await f.Credentials.FindTenantCredentialAsync(owner.TenantId, view.Credential.CredentialId))!.Value;
        var attempt = OAuthAttempt(f, owner.TenantId, current, new(writer.Account.AccountId, session.SessionId, writer.User.PrincipalId));
        var oauth = (ITenantCredentialOAuthStore)f.Iam; Assert.True(await BeginOAuth(f, attempt));
        state = (await f.Iam.ReadTenantAsync(owner.TenantId))!;
        var claim = (await oauth.TryClaimCredentialOAuthAsync(attempt.AttemptId, attempt.StateHash, attempt.CorrelationHash, state.Tenant.Revision))!;
        if (boundary == "logout") await f.Identity.SignOutAsync(session.SessionId);
        if (boundary == "password") await f.Identity.ChangePasswordAsync(writer.Account.AccountId, "strong-writer-password", "new-strong-writer-password");
        if (boundary == "membership") await f.Access.SetMemberStatusAsync(owner.OwnerPrincipalId, owner.TenantId, writer.User.UserId, IdentityStatus.Suspended, state.Tenant.Revision);
        if (boundary == "expiry") clock.Advance(TimeSpan.FromMinutes(5));
        state = (await f.Iam.ReadTenantAsync(owner.TenantId))!;
        var audits = (await f.Iam.ReadAuditAsync(owner.TenantId, 200)).Count;
        var prepared = OAuthConnected(f, current, "verified-subject");
        if (boundary == "expiry")
            Assert.False(await oauth.TryCommitCredentialOAuthAsync(Next(state), state.Tenant.Revision, attempt.AttemptId, claim.Attempt.Revision, prepared,
                current.SecretRevision, f.Audit(owner.TenantId, writer.User.PrincipalId, current.CredentialId)));
        else
            await Assert.ThrowsAsync<AccessDeniedException>(() => oauth.TryCommitCredentialOAuthAsync(Next(state), state.Tenant.Revision, attempt.AttemptId,
                claim.Attempt.Revision, prepared, current.SecretRevision, f.Audit(owner.TenantId, writer.User.PrincipalId, current.CredentialId)).AsTask());
        var retained = (await f.Credentials.FindTenantCredentialAsync(owner.TenantId, current.CredentialId))!.Value;
        Assert.Empty(retained.ProtectedPayload); Assert.Equal(current.Revision, retained.Revision); Assert.Equal(current.SecretRevision, retained.SecretRevision);
        Assert.Equal(state.Tenant.Revision, (await f.Iam.ReadTenantAsync(owner.TenantId))!.Tenant.Revision);
        Assert.Equal(audits, (await f.Iam.ReadAuditAsync(owner.TenantId, 200)).Count);
        Assert.Equal(CredentialOAuthOutcome.Claimed, (await oauth.ReadCredentialOAuthAttemptAsync(attempt.AttemptId))!.Outcome);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OAuth_store_rejects_session_mixups_forged_profile_writes_and_replayed_claims(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite);
        var owner = await f.Identity.BootstrapAsync("owner@example.test", "Owner", "strong-owner-password", "Alpha", "alpha");
        var writer = await AddUser(f, owner.TenantId, owner.OwnerPrincipalId, "contributor");
        var ownerSession = (await f.Identity.SignInAsync("owner@example.test", "strong-owner-password"))!.Session;
        var state = (await f.Iam.ReadTenantAsync(owner.TenantId))!;
        var view = await f.Service.CreateAsync(owner.OwnerPrincipalId, owner.TenantId, OAuthInput(CredentialPurpose.EmailAccount), state.Tenant.Revision);
        var root = (await f.Credentials.FindTenantCredentialAsync(owner.TenantId, view.Credential.CredentialId))!;
        var current = root.Value; var oauth = (ITenantCredentialOAuthStore)f.Iam;
        var mixed = OAuthAttempt(f, owner.TenantId, current, new(writer.Account.AccountId, ownerSession.SessionId, writer.User.PrincipalId));
        state = (await f.Iam.ReadTenantAsync(owner.TenantId))!;
        await Assert.ThrowsAsync<AccessDeniedException>(() => BeginOAuth(f, mixed));
        Assert.Null(await oauth.ReadCredentialOAuthAttemptAsync(mixed.AttemptId));
        var forged = OAuthConnected(f, current, "forged-subject");
        await Assert.ThrowsAsync<ArgumentException>(() => f.Credentials.TrySaveTenantCredentialAsync(Next(state), state.Tenant.Revision, root.Owner, forged,
            current.Revision, current.SecretRevision, f.Audit(owner.TenantId, owner.OwnerPrincipalId, current.CredentialId)).AsTask());
        var attempt = mixed with { AccountId = owner.AccountId, PrincipalId = owner.OwnerPrincipalId }; Assert.True(await BeginOAuth(f, attempt));
        state = (await f.Iam.ReadTenantAsync(owner.TenantId))!;
        var claims = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => Task.Run(async () =>
            await oauth.TryClaimCredentialOAuthAsync(attempt.AttemptId, attempt.StateHash, attempt.CorrelationHash, state.Tenant.Revision))));
        var claim = Assert.Single(claims, c => c is not null)!;
        foreach (var invalid in new[] { forged with { DisplayName = "Forged configuration" }, forged with { SecretRevision = 50 },
            forged with { EmailAccount = forged.EmailAccount! with { Synthetic = false } } })
            await Assert.ThrowsAsync<ArgumentException>(() => oauth.TryCommitCredentialOAuthAsync(Next(state), state.Tenant.Revision, attempt.AttemptId, claim.Attempt.Revision,
                invalid, current.SecretRevision, f.Audit(owner.TenantId, owner.OwnerPrincipalId, current.CredentialId)).AsTask());
        Assert.Equal(state.Tenant.Revision, (await f.Iam.ReadTenantAsync(owner.TenantId))!.Tenant.Revision);
        Assert.True(await oauth.TryCommitCredentialOAuthAsync(Next(state), state.Tenant.Revision, attempt.AttemptId, claim.Attempt.Revision,
            forged, current.SecretRevision, f.Audit(owner.TenantId, owner.OwnerPrincipalId, current.CredentialId)));
        state = (await f.Iam.ReadTenantAsync(owner.TenantId))!;
        Assert.False(await oauth.TryCommitCredentialOAuthAsync(Next(state), state.Tenant.Revision, attempt.AttemptId, claim.Attempt.Revision,
            forged, current.SecretRevision, f.Audit(owner.TenantId, owner.OwnerPrincipalId, current.CredentialId)));
        await f.Identity.SignOutAsync(ownerSession.SessionId);
        var nextAttempt = OAuthAttempt(f, owner.TenantId, (await f.Credentials.FindTenantCredentialAsync(owner.TenantId, current.CredentialId))!.Value,
            new(owner.AccountId, ownerSession.SessionId, owner.OwnerPrincipalId));
        await Assert.ThrowsAsync<AccessDeniedException>(() => BeginOAuth(f, nextAttempt));
    }

    [Theory]
    [InlineData(false, CredentialPurpose.EmailAccount)]
    [InlineData(true, CredentialPurpose.EmailAccount)]
    [InlineData(false, CredentialPurpose.GoogleDriveAccount)]
    [InlineData(true, CredentialPurpose.GoogleDriveAccount)]
    public async Task OAuth_identity_uniqueness_is_tenant_and_purpose_owned(bool sqlite, CredentialPurpose purpose)
    {
        await using var f = await Fixture.Create(sqlite);
        var owner = await f.Identity.BootstrapAsync("owner@example.test", "Owner", "strong-owner-password", "Alpha", "alpha");
        var session = (await f.Identity.SignInAsync("owner@example.test", "strong-owner-password"))!.Session;
        var system = (await f.Iam.ReadSystemAsync()).Principals.Single();
        var beta = await f.Access.CreateTenantAsync(system.PrincipalId, owner.AccountId, "Beta", "beta");
        async Task<Credential> Connect(Guid tenant, Guid actor, CredentialPurpose selected, bool duplicate = false)
        {
            var state = (await f.Iam.ReadTenantAsync(tenant))!;
            var created = await f.Service.CreateAsync(actor, tenant, OAuthInput(selected), state.Tenant.Revision);
            var current = (await f.Credentials.FindTenantCredentialAsync(tenant, created.Credential.CredentialId))!.Value;
            var attempt = OAuthAttempt(f, tenant, current, new(owner.AccountId, session.SessionId, actor)); Assert.True(await BeginOAuth(f, attempt));
            state = (await f.Iam.ReadTenantAsync(tenant))!; var oauth = (ITenantCredentialOAuthStore)f.Iam;
            var claim = (await oauth.TryClaimCredentialOAuthAsync(attempt.AttemptId, attempt.StateHash, attempt.CorrelationHash, state.Tenant.Revision))!;
            var connected = OAuthConnected(f, current, "same-google-subject");
            if (duplicate)
            {
                var error = await Assert.ThrowsAsync<SecurityConflictException>(() => oauth.TryCommitCredentialOAuthAsync(Next(state), state.Tenant.Revision, attempt.AttemptId,
                    claim.Attempt.Revision, connected, current.SecretRevision, f.Audit(tenant, actor, current.CredentialId)).AsTask());
                Assert.Equal("oauth_identity_already_connected", error.ReasonCode);
                Assert.Empty((await f.Credentials.FindTenantCredentialAsync(tenant, current.CredentialId))!.Value.ProtectedPayload);
                Assert.Equal(state.Tenant.Revision, (await f.Iam.ReadTenantAsync(tenant))!.Tenant.Revision);
            }
            else Assert.True(await oauth.TryCommitCredentialOAuthAsync(Next(state), state.Tenant.Revision, attempt.AttemptId, claim.Attempt.Revision, connected,
                current.SecretRevision, f.Audit(tenant, actor, current.CredentialId)));
            return connected;
        }
        var first = await Connect(owner.TenantId, owner.OwnerPrincipalId, purpose);
        await Connect(beta.Tenant.TenantId, beta.Users.Single().PrincipalId, purpose);
        await Connect(owner.TenantId, owner.OwnerPrincipalId, purpose, duplicate: true);
        await Connect(owner.TenantId, owner.OwnerPrincipalId, purpose == CredentialPurpose.EmailAccount ? CredentialPurpose.GoogleDriveAccount : CredentialPurpose.EmailAccount);
        Assert.Empty(await f.Legacy.ListAsync());
        var legacyId = f.Ids.NewId();
        await f.Legacy.SaveAsync(first with { CredentialId = legacyId, Revision = 1,
            ProtectedPayload = f.Protector.Protect(legacyId, first.ProtectionVersion, "legacy-private-grant") }, 0);
        Assert.Equal(legacyId, Assert.Single(await f.Legacy.ListAsync()).CredentialId);
        Assert.Equal(3, (await f.Service.ListAsync(owner.OwnerPrincipalId, owner.TenantId)).Total);
        if (sqlite)
        {
            await using var db = f.Contexts!.CreateDbContext();
            Assert.Equal(3, await db.Set<CredentialOAuthAttemptRow>().CountAsync(a => a.Outcome == CredentialOAuthOutcome.Connected));
            Assert.False(await db.Set<CredentialOAuthAttemptRow>().AnyAsync(a => a.ProtectedProof != ""));
        }
    }
}
