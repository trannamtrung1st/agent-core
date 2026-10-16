using AgentCore.Domain.Credentials;
using AgentCore.Infrastructure.Persistence;
using AgentCore.Infrastructure.Security;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantCredentialIntegrationTests
{
    [Fact]
    public async Task OAuth_database_constraints_bind_both_owners_and_the_original_account_session()
    {
        await using var f = await Fixture.Create(true);
        var owner = await f.Identity.BootstrapAsync("owner@example.test", "Owner", "strong-owner-password", "Alpha", "alpha");
        var writer = await AddUser(f, owner.TenantId, owner.OwnerPrincipalId, "contributor");
        var ownerSession = (await f.Identity.SignInAsync("owner@example.test", "strong-owner-password"))!.Session;
        var writerSession = (await f.Identity.SignInAsync("writer@example.test", "strong-writer-password"))!.Session;
        var system = (await f.Iam.ReadSystemAsync()).Principals.Single(p => p.AccountId == owner.AccountId);
        var beta = await f.Access.CreateTenantAsync(system.PrincipalId, owner.AccountId, "Beta", "beta");
        var state = (await f.Iam.ReadTenantAsync(owner.TenantId))!;
        var view = await f.Service.CreateAsync(owner.OwnerPrincipalId, owner.TenantId, OAuthInput(CredentialPurpose.EmailAccount), state.Tenant.Revision);
        var current = (await f.Credentials.FindTenantCredentialAsync(owner.TenantId, view.Credential.CredentialId))!.Value;
        var attempt = OAuthAttempt(f, owner.TenantId, current, new(owner.AccountId, ownerSession.SessionId, owner.OwnerPrincipalId));
        foreach (var invalid in new[] { attempt with { TenantId = beta.Tenant.TenantId, PrincipalId = beta.Users.Single().PrincipalId },
            attempt with { PrincipalId = beta.Users.Single().PrincipalId }, attempt with { AuthenticationSessionId = writerSession.SessionId },
            attempt with { AccountId = writer.Account.AccountId } })
        {
            await using var db = f.Contexts!.CreateDbContext();
            db.Add(CredentialOAuthAttemptRow.Map(invalid));
            var error = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
            Assert.Equal(19, Assert.IsType<SqliteException>(error.InnerException).SqliteErrorCode);
        }
        await using (var db = f.Contexts!.CreateDbContext())
        {
            var profile = await db.Set<EmailAccountProfileRecord>().SingleAsync(p => p.CredentialId == current.CredentialId.ToString("D"));
            Assert.Equal(owner.TenantId, profile.TenantId);
            profile.TenantId = beta.Tenant.TenantId;
            var error = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
            Assert.Equal(19, Assert.IsType<SqliteException>(error.InnerException).SqliteErrorCode);
        }
        Assert.True(await BeginOAuth(f, attempt));
        await using (var db = f.Contexts!.CreateDbContext())
        {
            db.Add(CredentialOAuthAttemptRow.Map(attempt with { AttemptId = f.Ids.NewId() }));
            var error = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
            Assert.Equal(19, Assert.IsType<SqliteException>(error.InnerException).SqliteErrorCode);
        }
        await using (var db = f.Contexts!.CreateDbContext())
        {
            Assert.Single(await db.Set<CredentialOAuthAttemptRow>().ToArrayAsync());
            Assert.False(db.Database.HasPendingModelChanges());
            // Null Tenant is reserved for unowned legacy rows; an owned root never adopts that profile shape.
            var profile = await db.Set<EmailAccountProfileRecord>().SingleAsync(p => p.CredentialId == current.CredentialId.ToString("D"));
            profile.TenantId = null;
            await db.SaveChangesAsync();
        }
        Assert.Null(await f.Credentials.FindTenantCredentialAsync(owner.TenantId, current.CredentialId));
        Assert.Equal(0, (await f.Credentials.ListTenantCredentialsAsync(owner.TenantId, null, 0, 20)).Total);
        Assert.Null(await f.Legacy.GetAsync(current.CredentialId));
    }
}
